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
            lock (_gate)
            {
                if (_inFlight is { } active && active.Matches(account, generation))
                    flight = active;
                else if (_lastCheck is { } last && last.AccountVersion == account.AccountVersion &&
                    last.Generation == generation && last.CompletedAt <= now && now < last.NextCheck)
                    recent = last.Result;
                else
                {
                    _inFlight = flight = new Verification(account, generation);
                    start = true;
                }

                if (flight != null)
                    flight.Waiters++;
            }

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
                var now = _clock.GetUtcNow();
                var next = now + (result.VerifiedOnlineTier != null ? VerificationInterval : RetryInterval);
                if (result.IsAllowed && HostedEntitlement.Deadline(_settings.ReadHostedSubscription(), now) is { } end && end < next)
                    next = end;
                lock (_gate)
                {
                    if (ReferenceEquals(_inFlight, flight))
                    {
                        _lastCheck = new(flight.Account.AccountVersion, flight.Generation, now, next, result);
                        _inFlight = null;
                    }
                }
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
            }
            retired?.Cancel();
        }

        private async Task<SubscriptionCheckResult> RequestVerificationAsync(Verification flight)
        {
            var account = flight.Account;
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

            var payload = new
            {
                username = user,
                password,
                device_platform = DeviceInfo.Platform.ToString(),
                device_type = DeviceInfo.Idiom.ToString(),
                device_model = DeviceInfoHelper.CombineManufacturerAndModel(DeviceInfo.Manufacturer, DeviceInfo.Model),
                app_version = AppInfo.Current.VersionString ?? string.Empty,
                app_build = AppInfo.Current.BuildString ?? string.Empty,
                os_version = DeviceInfo.VersionString ?? string.Empty,
                device_id = _settings.DeviceInstallId,
                session_token = account.Token
            };

            token.ThrowIfCancellationRequested();
            if (!account.IsCurrentAccount)
                return Replaced();

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
                var code = (int)response.StatusCode;
                if (code is 408 or 429 or >= 500 and <= 599)
                {
                    AppLog.Basic(() => $"Account verification returned a transient HTTP {code}.");
                    return ApplyTransient(flight, "http_" + code);
                }
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Basic(() => $"Account verification returned HTTP {code}.");
                    return PublishDenial(flight, "http_" + code, $"Authentication request denied (HTTP {code}).");
                }

                var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
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
                return ApplyTransient(flight, "unreachable");
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
            string Safe(string? value) => DiagnosticRedaction.SanitizeAuthenticationMessage(value, user, password, account.Token, issuedToken);
            if (expires <= now)
                return PublishDenial(flight, "expired", "The verified subscription or credential has expired.");
            if (body is not { Ok: true } || subscription is not { Active: true, TierLevel: 1 or 2 })
            {
                var message = Safe(body?.Message);
                return PublishDenial(flight, "invalid_authorization", string.IsNullOrWhiteSpace(message)
                    ? "The account server did not confirm an active subscription." : message);
            }

            var summary = Safe(BuildPlanSummary(subscription));
            if (string.IsNullOrWhiteSpace(summary))
                summary = "Active subscription";
            var verified = new HostedSubscriptionSnapshot(true, true, subscription.TierLevel, now, expires,
                trial ? null : renewal, Safe(subscription.LevelLabel ?? subscription.Level),
                Safe(subscription.PriceId), summary);
            if (!ApplyCurrent(flight, () => _settings.SaveHostedSubscription(verified)))
                return Replaced();

            if (issuedToken.Length != 0)
                await _telemetryService.AdoptSessionTokenAsync(issuedToken, "auth_session_token_issued", token,
                    account.Version).ConfigureAwait(false);

            return new(true, message: summary) { VerifiedOnlineTier = verified.Tier, EffectiveTier = verified.Tier };
        }

        private SubscriptionCheckResult ApplyTransient(Verification flight, string reason)
        {
            SubscriptionCheckResult result = Replaced();
            ApplyCurrent(flight, () =>
            {
                var snapshot = _settings.ReadHostedSubscription();
                var deadline = HostedEntitlement.Deadline(snapshot, _clock.GetUtcNow());
                var message = deadline is { } end
                    ? $"Account verification is unavailable. Using the verified subscription until {end.ToLocalTime():g}."
                    : "Account verification is unavailable and no current verified subscription is cached.";
                _settings.SaveHostedSubscription(snapshot with
                {
                    IsAllowed = deadline != null,
                    VerifiedOnline = false,
                    Message = message
                });
                result = new(deadline != null, deadline != null ? "offline_cached" : reason, message)
                {
                    EffectiveTier = deadline != null ? snapshot.Tier : null
                };
            });
            return result;
        }

        private SubscriptionCheckResult PublishDenial(Verification flight, string code, string message)
        {
            var denied = new HostedSubscriptionSnapshot(false, false, 0, null, null, null, "", "", message);
            return ApplyCurrent(flight, () => _settings.SaveHostedSubscription(denied))
                ? new(false, code, message) : Replaced();
        }

        private bool ApplyCurrent(Verification flight, Action apply)
        {
            var applied = false;
            flight.Account.TryApplyIdentity(() =>
            {
                lock (_gate)
                {
                    flight.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_inFlight, flight))
                        return;
                    apply();
                    applied = true;
                }
            });
            return applied;
        }

        private static SubscriptionCheckResult LocalOnly() =>
            new(false, "local_only", "Subscription verification is paused in Local Only Mode.");

        private static SubscriptionCheckResult Replaced() =>
            new(false, "account_changed", "The recorder or hosted account changed during verification.");

        private sealed record LastCheck(long AccountVersion, CancellationToken Generation, DateTimeOffset CompletedAt,
            DateTimeOffset NextCheck, SubscriptionCheckResult Result);

        private sealed class Verification(WordPressSession account, CancellationToken generation) : IDisposable
        {
            private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(generation);
            internal WordPressSession Account { get; } = account;
            internal CancellationToken Generation { get; } = generation;
            internal CancellationToken Token => _lifetime.Token;
            internal TaskCompletionSource<SubscriptionCheckResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Waiters { get; set; }

            internal bool Matches(WordPressSession session, CancellationToken privacy) =>
                session.AccountVersion == Account.AccountVersion && privacy == Generation;

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
        internal sealed class FlexibleIntConverter : JsonConverter<int>
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
