using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JoesScanner.Helpers;

namespace JoesScanner.Services
{
    public sealed class SubscriptionService : ISubscriptionService, IDisposable
    {
        private static readonly TimeSpan VerificationInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private readonly ISettingsService _settings;
        private readonly ITelemetryService _telemetryService;
        private readonly HttpClient _httpClient;
        private readonly TimeProvider _clock;
        private readonly HostedEntitlement _entitlement;
        private readonly object _gate = new();
        private Verification? _inFlight;
        private LastCheck? _lastCheck;
        private long _requestOrder;
        private long _publishedOrder;
        private long _authoritativeOrder;

        public SubscriptionService(ISettingsService settings, ITelemetryService telemetryService)
            : this(settings, telemetryService, WordPressSession.CreateClient(settings))
        {
        }

        internal SubscriptionService(ISettingsService settings, ITelemetryService telemetryService, HttpClient httpClient,
            TimeProvider? clock = null)
        {
            _settings = settings;
            _telemetryService = telemetryService;
            _httpClient = httpClient;
            _clock = clock ?? TimeProvider.System;
            _entitlement = HostedEntitlement.For(settings);
            _settings.AccountIdentityChanged += OnAccountChanged;
        }

        public void Dispose()
        {
            _settings.AccountIdentityChanged -= OnAccountChanged;
            OnAccountChanged(null, EventArgs.Empty);
            _httpClient.Dispose();
        }

        public Task<SubscriptionCheckResult> EnsureSubscriptionAsync(CancellationToken cancellationToken) =>
            VerifyAsync(_settings.ServerUrl, requireSelection: true, cancellationToken);

        public Task<SubscriptionCheckResult> EnsureSubscriptionForRecorderAsync(string server, CancellationToken cancellationToken) =>
            VerifyAsync(server, requireSelection: false, cancellationToken);

        public async Task MaintainHostedSubscriptionAsync(string server, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && !_settings.LocalOnlyMode &&
                    HostedServerRules.RequiresApiFirewallCredentials(_settings, server))
                {
                    var version = _settings.ReadAccountSession().AccountVersion;
                    var now = _clock.GetUtcNow();
                    DateTimeOffset next;
                    lock (_gate)
                        next = _lastCheck is { } last && last.AccountVersion == version ? last.NextCheck : now;
                    if (next > now)
                        await Task.Delay(next - now, _clock, token).ConfigureAwait(false);

                    try
                    {
                        var result = await EnsureSubscriptionForRecorderAsync(server, token).ConfigureAwait(false);
                        if (!result.IsAllowed)
                            return;
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        AppLog.Basic(() => $"Periodic subscription verification failed ({DiagnosticRedaction.Describe(ex)}).");
                        await Task.Delay(RetryInterval, _clock, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private async Task<SubscriptionCheckResult> VerifyAsync(string server, bool requireSelection, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var generation = PrivacyNetworkPolicy.For(_settings).Generation;
            if (generation.IsCancellationRequested)
                return LocalOnly();

            bool IsSelected() => requireSelection
                ? string.Equals(server, _settings.ServerUrl, StringComparison.Ordinal)
                : HostedServerRules.RequiresApiFirewallCredentials(_settings, server);
            if (!IsSelected())
                return Replaced();

            var account = WordPressSession.Capture(_settings);
            var now = _clock.GetUtcNow();
            Verification? flight = null;
            SubscriptionCheckResult? recent = null;
            var start = false;
            if (!account.TryApplyIdentity(() =>
            {
                lock (_gate)
                {
                    // A completed Settings check takes precedence over an older automatic flight.
                    if (_inFlight is { } active && active.Scope.Order >= (_lastCheck?.Order ?? 0) &&
                        active.Matches(account, generation))
                        flight = active;
                    else if (_lastCheck is { } last && last.AccountVersion == account.AccountVersion &&
                        last.Generation == generation && last.CompletedAt <= now && now < last.NextCheck)
                        recent = last.Result;
                    else
                    {
                        _inFlight = flight = new Verification(NewScope(account, generation));
                        start = true;
                    }

                    if (flight != null)
                        flight.Waiters++;
                }
            }))
                return Replaced();

            if (start)
                _ = CompleteVerificationAsync(flight!);

            try
            {
                var result = recent ?? await flight!.Completion.Task.WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!account.IsCurrentAccount || !IsSelected())
                    return Replaced();
                if (generation.IsCancellationRequested)
                    return LocalOnly();
                if (!result.IsAllowed)
                    return result;

                var snapshot = _entitlement.Current(_clock);
                if (!account.IsCurrentAccount)
                    return Replaced();
                if (snapshot == null)
                    return new(false, "grace_expired", "The verified hosted entitlement has expired. Verify your account again.");
                return new(true, result.ErrorCode, result.Message)
                {
                    VerifiedOnlineTier = snapshot.VerifiedOnline ? snapshot.Tier : null,
                    EffectiveTier = snapshot.Tier
                };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !account.IsCurrentAccount)
            {
                return Replaced();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && generation.IsCancellationRequested)
            {
                return LocalOnly();
            }
            finally
            {
                if (flight != null)
                    Release(flight);
            }
        }

        private async Task CompleteVerificationAsync(Verification flight)
        {
            try
            {
                var result = await RequestVerificationAsync(flight).ConfigureAwait(false);
                flight.Token.ThrowIfCancellationRequested();
                flight.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (flight.Token.IsCancellationRequested)
            {
                flight.Completion.TrySetCanceled(flight.Token);
            }
            catch (Exception ex)
            {
                AppLog.Basic(() => $"Subscription verification could not finish ({DiagnosticRedaction.Describe(ex)}).");
                flight.Completion.TrySetResult(new(false, "verification_failed", "Subscription verification could not finish."));
            }
            finally
            {
                lock (_gate)
                    if (ReferenceEquals(_inFlight, flight))
                        _inFlight = null;
                flight.Dispose();
            }
        }

        private void Release(Verification flight)
        {
            var cancel = false;
            lock (_gate)
            {
                if (--flight.Waiters == 0 && ReferenceEquals(_inFlight, flight))
                {
                    _inFlight = null;
                    cancel = true;
                }
            }
            if (cancel)
                flight.Cancel();
        }

        private void OnAccountChanged(object? sender, EventArgs args)
        {
            Verification? retired;
            lock (_gate)
            {
                retired = _inFlight;
                _inFlight = null;
                _lastCheck = null;
                _publishedOrder = _authoritativeOrder = 0;
            }
            retired?.Cancel();
        }

        internal async Task<(SubscriptionCheckResult Result, WordPressSession? VerifiedAccount)> ValidateFromSettingsAsync(
            WordPressSession account, string accountBase, string user, string password, Func<bool> ownsInputs,
            Func<string, bool, Func<bool>, Action, Task> applyHostedAccount)
        {
            var generation = PrivacyNetworkPolicy.For(_settings).Generation;
            if (generation.IsCancellationRequested || _settings.LocalOnlyMode)
                return (LocalOnly(), null);
            if (!ownsInputs() || !account.IsCurrentAccount)
                return (Replaced(), null);

            var endpoint = SavedConnectionUrl.AccountEndpoint(accountBase);
            var matchesEndpoint = account.Endpoint("auth") == endpoint;
            var sameAccount = false;
            var currentCredentials = false;
            if (matchesEndpoint && _settings.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out var savedUser, out var savedPassword) &&
                string.Equals(user, savedUser, StringComparison.OrdinalIgnoreCase))
            {
                sameAccount = true;
                currentCredentials = password == TextNormalizationHelper.NormalizeSmartQuotes(savedPassword);
            }
            var scope = NewScope(account, generation);
            if (!account.IsCurrentAccount)
                return (Replaced(), null);

            AuthorizationReply reply;
            try
            {
                reply = await RequestAuthorizationAsync(endpoint, user, password, currentCredentials ? account.Token : "", generation);
            }
            catch (OperationCanceledException) when (generation.IsCancellationRequested)
            {
                return (LocalOnly(), null);
            }

            if (reply.Grant == null)
                return (Publish(scope, reply, ownsInputs, sameAccount, currentCredentials), null);

            // Use the existing UI-owned transition for any account replacement. The store
            // rechecks ownership, ordering, and the deadline inside the publication lock.
            var result = Replaced();
            WordPressSession? committedAccount = null;
            await applyHostedAccount(user, !matchesEndpoint, CanApply, () =>
            {
                result = Publish(scope, reply, ownsInputs, sameAccount, currentCredentials, () =>
                {
                    _settings.AuthServerBaseUrl = accountBase;
                    if (!currentCredentials)
                        _settings.SetServerCredentials(HostedServerRules.DefaultServerUrl, user, password);
                    _settings.ServerUrl = HostedServerRules.DefaultServerUrl;
                    if (reply.SessionToken is { Length: > 0 } issuedToken && _settings.AuthSessionToken != issuedToken)
                        _settings.AuthSessionToken = issuedToken;
                    committedAccount = WordPressSession.Capture(_settings);
                });
            });
            return (result, committedAccount);

            bool CanApply()
            {
                if (!ownsInputs() || !account.IsCurrentAccount)
                    return false;
                if (generation.IsCancellationRequested)
                {
                    result = LocalOnly();
                    return false;
                }
                lock (_gate)
                    return !sameAccount || scope.Order >= _authoritativeOrder;
            }
        }

        private async Task<SubscriptionCheckResult> RequestVerificationAsync(Verification flight)
        {
            var account = flight.Scope.Account;
            var token = flight.Token;
            if (!account.IsCurrentAccount)
                return Replaced();

            string user, password;
            Uri endpoint;
            try
            {
                endpoint = account.Endpoint("auth");
                _settings.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out user, out password);
            }
            catch (CredentialStorageException ex)
            {
                AppLog.Basic("Account verification could not read protected credentials.");
                return new(false, "credential_storage_unavailable", ex.Message);
            }

            password = TextNormalizationHelper.NormalizeSmartQuotes(password);
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
                return new(false, "missing_credentials", _settings.SavedCredentialWarning ??
                    "Enter your username and password in Settings before connecting.");

            token.ThrowIfCancellationRequested();
            if (!account.IsCurrentAccount)
                return Replaced();

            var reply = await RequestAuthorizationAsync(endpoint, user, password, account.Token, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = Publish(flight.Scope, reply, () => !token.IsCancellationRequested && ReferenceEquals(_inFlight, flight),
                sameAccount: true, currentCredentials: true);
            if (result.VerifiedOnlineTier.HasValue && reply.SessionToken is { Length: > 0 } issuedToken)
                await _telemetryService.AdoptSessionTokenAsync(issuedToken, "auth_session_token_issued", token,
                    account.Version).ConfigureAwait(false);
            return result;
        }

        private async Task<AuthorizationReply> RequestAuthorizationAsync(Uri endpoint, string user, string password,
            string sessionToken, CancellationToken token)
        {
            var payload = new
            {
                username = user,
                password,
                client = "JoesScannerApp",
                version = AppInfo.Current.VersionString ?? string.Empty,
                device_platform = DeviceInfo.Platform.ToString(),
                device_type = DeviceInfo.Idiom.ToString(),
                device_model = DeviceInfoHelper.CombineManufacturerAndModel(DeviceInfo.Manufacturer, DeviceInfo.Model),
                app_version = AppInfo.Current.VersionString ?? string.Empty,
                app_build = AppInfo.Current.BuildString ?? string.Empty,
                os_version = DeviceInfo.VersionString ?? string.Empty,
                device_id = _settings.DeviceInstallId,
                session_token = sessionToken
            };

            token.ThrowIfCancellationRequested();

            AuthResponseDto? body = null;
            try
            {
                var options = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                using var deadline = new CancellationTokenSource(RequestTimeout, _clock);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, options), Encoding.UTF8, "application/json")
                };
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                var code = (int)response.StatusCode;
                if (code is 408 or 429 or >= 500 and <= 599)
                {
                    AppLog.Basic(() => $"Account verification returned a transient HTTP {code}.");
                    return Transient("http_" + code);
                }
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Basic(() => $"Account verification returned HTTP {code}.");
                    return new(null, null, "http_" + code, $"Authentication request denied (HTTP {code}).");
                }

                var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    body = JsonSerializer.Deserialize<AuthResponseDto>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException) { }
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is HttpRequestException or IOException or OperationCanceledException)
            {
                AppLog.Basic(() => $"Account verification request failed ({DiagnosticRedaction.Describe(ex)}).");
                return Transient(ex is OperationCanceledException ? "timeout" : "unreachable");
            }

            var subscription = body?.Subscription;
            var now = _clock.GetUtcNow().UtcDateTime;
            var renewal = DateParseHelper.TryParseUtc(subscription?.PeriodEndAt);
            var expires = HostedEntitlement.Earliest(renewal, DateParseHelper.TryParseUtc(subscription?.ExpiresAt));
            var trial = string.Equals(subscription?.Status, "trialing", StringComparison.OrdinalIgnoreCase);
            if (trial)
                expires = HostedEntitlement.Earliest(expires, DateParseHelper.TryParseUtc(subscription?.TrialEndsAt));
            var credentialExpiry = HostedEntitlement.Earliest(DateParseHelper.TryParseUtc(body?.CredentialExpiresAt),
                DateParseHelper.TryParseUtc(body?.Session?.ExpiresAt));
            expires = HostedEntitlement.Earliest(expires, credentialExpiry);

            var issuedToken = (body?.SessionToken ?? "").Trim();
            string Safe(string? value) => DiagnosticRedaction.SanitizeAuthenticationMessage(value, user, password, sessionToken, issuedToken);
            if (expires <= now || string.Equals(subscription?.Status, "expired", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(body?.Error, "expired", StringComparison.OrdinalIgnoreCase))
                return Expired();
            if (body == null)
                return new(null, null, "invalid_authorization", "Auth server returned an unexpected response.");
            if (!body.Ok || !string.IsNullOrWhiteSpace(body.Error))
            {
                var message = Safe(body.Message);
                return new(null, null, "invalid_authorization", string.IsNullOrWhiteSpace(message) ? "Account validation failed." : message);
            }
            if (subscription is not { Active: true, TierLevel: 1 or 2 } ||
                string.Equals(subscription.Status, "revoked", StringComparison.OrdinalIgnoreCase))
                return new(null, null, "invalid_authorization", "Account validated but subscription is not active.");

            var summary = Safe(BuildPlanSummary(subscription));
            if (string.IsNullOrWhiteSpace(summary))
                summary = "Active subscription";
            var verified = new HostedSubscriptionSnapshot(true, true, subscription.TierLevel, now, expires,
                trial ? null : renewal, Safe(subscription.LevelLabel ?? subscription.Level),
                Safe(subscription.PriceId), summary);
            return new(verified, issuedToken, null, summary);
        }

        private VerificationScope NewScope(WordPressSession account, CancellationToken generation) =>
            new(account, generation, Interlocked.Increment(ref _requestOrder));

        private SubscriptionCheckResult Publish(VerificationScope scope, AuthorizationReply reply, Func<bool> ownsRequest,
            bool sameAccount, bool currentCredentials, Action? saveLogin = null)
        {
            var result = Replaced();
            scope.Account.TryApplyIdentity(() =>
            {
                lock (_gate)
                {
                    // A newer outage report cannot supersede a definitive subscription decision.
                    var latest = reply.IsTransient ? _publishedOrder : _authoritativeOrder;
                    if (!ownsRequest() || (sameAccount && scope.Order < latest))
                        return;
                    if (scope.Generation.IsCancellationRequested || _settings.LocalOnlyMode)
                    {
                        result = LocalOnly();
                        return;
                    }

                    var now = _clock.GetUtcNow();
                    // A verified reply can expire while waiting for the Settings account transition.
                    if (reply.Grant is { } grant && HostedEntitlement.Deadline(grant, now) == null)
                        reply = Expired();
                    if (reply.Grant == null && !currentCredentials)
                    {
                        result = new(false, reply.ErrorCode, reply.Message);
                        return;
                    }

                    var snapshot = ResultSnapshot(reply, now);
                    if (reply.Grant != null)
                        saveLogin?.Invoke();
                    else if (!reply.IsTransient)
                    {
                        // Retire any pending token adoption from older verification while
                        // preserving the signed-in account and its existing session value.
                        var session = _settings.ReadAccountSession();
                        _settings.TryReplaceAccountSession(session.Version, session.Token);
                    }

                    _settings.SaveHostedSubscription(snapshot);
                    _publishedOrder = Math.Max(_publishedOrder, scope.Order);
                    if (!reply.IsTransient)
                        _authoritativeOrder = scope.Order;
                    result = new(snapshot.IsAllowed, reply.IsTransient && snapshot.IsAllowed ? "offline_cached" : reply.ErrorCode, snapshot.Message)
                    {
                        EffectiveTier = snapshot.IsAllowed ? snapshot.Tier : null,
                        VerifiedOnlineTier = snapshot.VerifiedOnline ? snapshot.Tier : null
                    };
                    var next = now + (snapshot.VerifiedOnline ? VerificationInterval : RetryInterval);
                    if (HostedEntitlement.Deadline(snapshot, now) is { } end && end < next)
                        next = end;
                    _lastCheck = new(scope.Order, _settings.ReadAccountSession().AccountVersion, scope.Generation, now, next, result);
                }
            });
            return result;
        }

        private HostedSubscriptionSnapshot ResultSnapshot(AuthorizationReply reply, DateTimeOffset now)
        {
            if (reply.Grant is { } grant)
                return grant;
            if (!reply.IsTransient)
                return new(false, false, 0, null, null, null, "", "", reply.Message ?? "");

            var saved = _settings.ReadHostedSubscription();
            var deadline = HostedEntitlement.Deadline(saved, now);
            var message = deadline is { } end
                ? $"Account verification is unavailable. Using the verified subscription until {end.ToLocalTime():g}."
                : "Account verification is unavailable and no current verified subscription is cached.";
            return saved with { IsAllowed = deadline != null, VerifiedOnline = false, Message = message };
        }

        private static AuthorizationReply Transient(string code) =>
            new(null, null, code, "Account verification is temporarily unavailable.", IsTransient: true);

        private static AuthorizationReply Expired() =>
            new(null, null, "expired", "The verified subscription or credential has expired.");

        private sealed record AuthorizationReply(HostedSubscriptionSnapshot? Grant, string? SessionToken,
            string? ErrorCode, string? Message, bool IsTransient = false);

        private readonly record struct VerificationScope(WordPressSession Account, CancellationToken Generation, long Order);

        private static SubscriptionCheckResult LocalOnly() =>
            new(false, "local_only", "Subscription verification is paused in Local Only Mode.");

        private static SubscriptionCheckResult Replaced() =>
            new(false, "account_changed", "The recorder or hosted account changed during verification.");

        private sealed record LastCheck(long Order, long AccountVersion, CancellationToken Generation, DateTimeOffset CompletedAt,
            DateTimeOffset NextCheck, SubscriptionCheckResult Result);

        private sealed class Verification(VerificationScope scope) : IDisposable
        {
            private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(scope.Generation);
            internal VerificationScope Scope { get; } = scope;
            internal CancellationToken Token => _lifetime.Token;
            internal TaskCompletionSource<SubscriptionCheckResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Waiters { get; set; }

            internal bool Matches(WordPressSession session, CancellationToken privacy) =>
                session.AccountVersion == Scope.Account.AccountVersion && privacy == Scope.Generation;

            internal void Cancel()
            {
                try { _ = _lifetime.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }

            public void Dispose() => _lifetime.Dispose();
        }

        private static string BuildPlanSummary(AuthSubscriptionDto? sub)
        {
            if (sub == null)
                return string.Empty;

            var planLabel = (sub.LevelLabel ?? sub.Level ?? string.Empty).Trim();
            var priceText = (sub.PriceId ?? string.Empty).Trim();
            var statusText = (sub.Status ?? string.Empty).Trim().ToLowerInvariant();

            var includePriceText =
                !string.IsNullOrEmpty(priceText) &&
                !string.Equals(planLabel, priceText, StringComparison.OrdinalIgnoreCase) &&
                !planLabel.Contains(priceText, StringComparison.OrdinalIgnoreCase) &&
                !priceText.Contains(planLabel, StringComparison.OrdinalIgnoreCase);

            var periodRaw = statusText == "trialing"
                ? (sub.TrialEndsAt ?? sub.PeriodEndAt ?? sub.ExpiresAt ?? string.Empty)
                : (sub.PeriodEndAt ?? sub.ExpiresAt ?? sub.TrialEndsAt ?? string.Empty);

            var dtUtc = DateParseHelper.TryParseUtc(periodRaw);
            var formattedDate = dtUtc.HasValue
                ? DateTime.SpecifyKind(dtUtc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd")
                : string.Empty;

            var dateLabel = statusText == "trialing"
                ? "Trial end date:"
                : "Renewal:";

            if (!string.IsNullOrEmpty(planLabel) &&
                includePriceText &&
                !string.IsNullOrEmpty(formattedDate))
            {
                return $"Plan: {planLabel} - {priceText} - {dateLabel} {formattedDate}";
            }

            if (!string.IsNullOrEmpty(planLabel) &&
                includePriceText)
            {
                return $"Plan: {planLabel} - {priceText}";
            }

            if (!string.IsNullOrEmpty(planLabel) &&
                !string.IsNullOrEmpty(formattedDate))
            {
                return $"Plan: {planLabel} - {dateLabel} {formattedDate}";
            }

            if (!string.IsNullOrEmpty(planLabel))
            {
                return $"Plan: {planLabel}";
            }

            if (!string.IsNullOrEmpty(formattedDate))
            {
                return $"{dateLabel} {formattedDate}";
            }

            return string.Empty;
        }


        private sealed class AuthResponseDto
        {
            [JsonPropertyName("ok")]
            public bool Ok { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }

            [JsonPropertyName("session_token")]
            public string? SessionToken { get; set; }

            [JsonPropertyName("subscription")]
            public AuthSubscriptionDto? Subscription { get; set; }

            [JsonPropertyName("credential_expires_at")]
            public string? CredentialExpiresAt { get; set; }

            [JsonPropertyName("session")]
            public AuthSessionDto? Session { get; set; }
        }

        private sealed class AuthSessionDto
        {
            [JsonPropertyName("expires_at")]
            public string? ExpiresAt { get; set; }
        }

        private sealed class AuthSubscriptionDto
        {
            [JsonPropertyName("active")]
            public bool Active { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }

            [JsonPropertyName("level")]
            public string? Level { get; set; }

            [JsonPropertyName("level_label")]
            public string? LevelLabel { get; set; }

            [JsonPropertyName("price_id")]
            public string? PriceId { get; set; }

            [JsonPropertyName("tier_level")]
            [JsonConverter(typeof(FlexibleIntConverter))]
            public int TierLevel { get; set; }

            [JsonPropertyName("period_end_at")]
            public string? PeriodEndAt { get; set; }

            [JsonPropertyName("trial_ends_at")]
            public string? TrialEndsAt { get; set; }

            [JsonPropertyName("expires_at")]
            public string? ExpiresAt { get; set; }
        }

        // Some deployments may serialize numeric fields as strings.
        // Accept both JSON number and JSON string for tier level.
        private sealed class FlexibleIntConverter : JsonConverter<int>
        {
            public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                try
                {
                    if (reader.TokenType == JsonTokenType.Number)
                    {
                        if (reader.TryGetInt32(out var n))
                            return n;

                        // Fall back to a wider parse if needed.
                        var l = reader.GetInt64();
                        if (l > int.MaxValue)
                            return int.MaxValue;
                        if (l < int.MinValue)
                            return int.MinValue;
                        return (int)l;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var s = (reader.GetString() ?? string.Empty).Trim();
                        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                            return n;

                        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                        {
                            if (l > int.MaxValue)
                                return int.MaxValue;
                            if (l < int.MinValue)
                                return int.MinValue;
                            return (int)l;
                        }

                        return 0;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                        return 0;
                }
                catch
                {
                }

                // Unknown token type or parse failure.
                try { reader.Skip(); } catch { }
                return 0;
            }

            public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
            {
                writer.WriteNumberValue(value);
            }
        }
    }
}
