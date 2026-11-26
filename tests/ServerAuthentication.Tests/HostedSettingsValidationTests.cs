using System.Net;
using System.Text.Json;
using JoesScanner.Services;
using JoesScanner.ViewModels;

internal static partial class WordPressOwnershipTests
{
    internal static async Task SettingsValidatePublishesEarliestDeadline()
    {
        var now = VerificationStart;
        foreach (var boundary in new[] { "period", "trial", "credential", "session" })
        {
            using var fixture = new SavedCredentialTests.Fixture();
            var settings = fixture.Load();
            var clock = new EntitlementClock(now);
            var deadline = now.AddMinutes(20);
            using var client = new HttpClient(new AsyncStubHandler(async (request, token) =>
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var payload = json.RootElement;
                Check(payload.GetProperty("username").GetString() == "new-subscriber" &&
                    payload.GetProperty("password").GetString() == " synthetic'pass " &&
                    payload.GetProperty("session_token").GetString() == "",
                    "A candidate must keep password characters and use no other account's token.");
                clock.Advance(TimeSpan.FromSeconds(1));
                return SettingsVerifiedResponse(2, now.AddDays(3),
                    period: boundary == "period" ? deadline : now.AddDays(2),
                    trial: boundary == "trial" ? deadline : null,
                    credential: boundary == "credential" ? deadline : null,
                    session: boundary == "session" ? deadline : null);
            }));
            using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
            var editor = new SettingsViewModel(settings, service)
            {
                AuthServerBaseUrl = OtherEndpoint,
                BasicAuthUsername = "new-subscriber",
                BasicAuthPassword = " synthetic’pass "
            };

            Check(await editor.PressValidateAsync() && !editor.ServerValidationIsError && editor.ShowValidationPrefix &&
                editor.ServerValidationMessage == "Joe's Scanner account validated." && editor.UseDefaultConnection &&
                editor.SavedSelections == 1 && editor.FirewallProbes == 1,
                "Successful Settings validation must keep its saved connection, confirmation, plan display and firewall verification.");
            var reopened = fixture.Load();
            var grant = reopened.ReadHostedSubscription();
            Check(reopened.AuthServerBaseUrl == OtherEndpoint && reopened.ServerUrl == Recorder &&
                reopened.BasicAuthUsername == "new-subscriber" && reopened.BasicAuthPassword == " synthetic'pass " &&
                settings.AuthSessionToken == "settings-verified" && reopened.AuthSessionToken.Length == 0 &&
                grant is { IsAllowed: true, VerifiedOnline: true, Tier: 2 } && grant.CheckedUtc == clock.GetUtcNow().UtcDateTime &&
                grant.ExpiresUtc == deadline.UtcDateTime && (boundary == "trial" ? grant.RenewalUtc == null : grant.RenewalUtc != null),
                "Settings must save the correct account, deadline and actual verification time while keeping the token in memory.");
            using var access = HostedEntitlement.For(settings).Protect(default, clock);
            clock.Advance(deadline - clock.GetUtcNow());
            Check(access.Token.IsCancellationRequested && HostedEntitlement.For(settings).Current(clock) == null,
                "Every known deadline must end hosted access at the boundary.");
        }
    }

    internal static async Task SettingsValidateRejectsExpiredAndInvalidGrants()
    {
        foreach (var invalid in new[] { "subscription", "trial", "credential", "tier", "status" })
        {
            using var fixture = new SavedCredentialTests.Fixture();
            var settings = fixture.Load();
            SignIn(settings, "verified-account", "test-session");
            var clock = new EntitlementClock(VerificationStart);
            var late = VerificationStart.AddDays(2);
            var calls = 0;
            using var client = new HttpClient(new StubHandler(_ => ++calls == 1
                ? SettingsVerifiedResponse(1, late)
                : SettingsVerifiedResponse(invalid == "tier" ? 7 : 2,
                    invalid == "subscription" ? VerificationStart : late,
                    trial: invalid == "trial" ? VerificationStart : null,
                    credential: invalid == "credential" ? VerificationStart : null,
                    status: invalid == "status" ? "expired" : null,
                    token: "denied-token")));
            using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
            var editor = new SettingsViewModel(settings, service);
            Check(await editor.PressValidateAsync(), "The initial Settings grant should be allowed.");
            using var access = HostedEntitlement.For(settings).Protect(default, clock);
            Check(!await editor.PressValidateAsync() && editor.ServerValidationIsError && editor.FirewallProbes == 1 &&
                settings.AuthSessionToken == "settings-verified" && settings.ReadHostedSubscription().CheckedUtc == null &&
                access.Token.IsCancellationRequested,
                "Even an active response must retire the current grant if an authenticated deadline has passed or the tier is invalid.");
        }

        using var newFixture = new SavedCredentialTests.Fixture();
        var newSettings = newFixture.Load();
        var newClock = new EntitlementClock(VerificationStart);
        using var validClient = new HttpClient(new StubHandler(_ => SettingsVerifiedResponse(1, VerificationStart.AddSeconds(1))));
        using var candidate = new SubscriptionService(newSettings, new TelemetryStub(newSettings), validClient, newClock);
        var newEditor = new SettingsViewModel(newSettings, candidate)
        {
            BasicAuthUsername = "candidate",
            BasicAuthPassword = "candidate-password",
            BeforeAccountTransition = () =>
            {
                newClock.Advance(TimeSpan.FromSeconds(1));
                return Task.CompletedTask;
            }
        };
        Check(!await newEditor.PressValidateAsync() && newEditor.ServerValidationIsError && newEditor.FirewallProbes == 0 &&
            !newSettings.TryGetServerCredentials(Recorder, out _, out _) && !newSettings.ReadHostedSubscription().IsAllowed,
            "A candidate's verified proof must still be within its deadline when the Settings transition commits.");
    }

    internal static async Task SettingsValidateDenialsRevokeTheCurrentAccount()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.OK })
        {
            using var fixture = new SavedCredentialTests.Fixture();
            var settings = fixture.Load();
            SignIn(settings, "subscriber", "initial");
            var clock = new EntitlementClock(VerificationStart);
            var calls = 0;
            using var client = new HttpClient(new StubHandler(_ => ++calls switch
            {
                1 => SettingsVerifiedResponse(2, VerificationStart.AddDays(2)),
                2 when status == HttpStatusCode.OK => JsonResponse(new
                {
                    ok = true,
                    subscription = new { active = false, tier_level = 2 }
                }),
                2 => new HttpResponseMessage(status),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            }));
            using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
            var editor = new SettingsViewModel(settings, service);
            Check(await editor.PressValidateAsync(), "The current account starts with a Settings grant.");
            using var access = HostedEntitlement.For(settings).Protect(default, clock);
            var error = status == HttpStatusCode.OK
                ? "Account validated but subscription is not active."
                : $"Auth server responded with HTTP {(int)status}.";
            Check(!await editor.PressValidateAsync() && editor.ServerValidationIsError && editor.ServerValidationMessage == error &&
                settings.BasicAuthUsername == "subscriber" && settings.ReadHostedSubscription().CheckedUtc == null &&
                access.Token.IsCancellationRequested && editor.FirewallProbes == 1,
                "Definitive Settings denial must immediately clear proof for the saved credentials and keep the error visible.");
            Check(!await editor.PressValidateAsync() && !settings.ReadHostedSubscription().IsAllowed,
                "An outage following definitive denial must have no grant to reuse.");
        }
    }

    internal static async Task SettingsValidateTransientFailuresHonorGrace()
    {
        foreach (var status in new[] { 0, 408, 429, 503, -1 })
        {
            using var fixture = new SavedCredentialTests.Fixture();
            var settings = fixture.Load();
            SignIn(settings, "subscriber", "initial");
            var clock = new EntitlementClock(VerificationStart);
            var calls = 0;
            using var client = new HttpClient(new AsyncStubHandler(async (_, token) =>
            {
                if (++calls == 1)
                    return SettingsVerifiedResponse(1, VerificationStart.AddDays(2));
                if (status == -1)
                    throw new HttpRequestException("Synthetic connectivity failure.");
                if (status != 0)
                    return new HttpResponseMessage((HttpStatusCode)status);
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The controlled timeout should cancel the request.");
            }));
            using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
            var editor = new SettingsViewModel(settings, service);
            Check(await editor.PressValidateAsync(), "The prior successful check must establish Standard access.");
            var initial = settings.ReadHostedSubscription();
            clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
            Check(!await ValidateDuringOutage() && editor.ServerValidationIsError &&
                editor.ServerValidationMessage.Contains("Using the verified subscription", StringComparison.Ordinal) &&
                settings.ReadHostedSubscription() is { IsAllowed: true, VerifiedOnline: false, Tier: 1 } &&
                settings.ReadHostedSubscription().CheckedUtc == initial.CheckedUtc && editor.FirewallProbes == 1,
                "An unavailable Settings check must reuse proof only through its original deadline and skip the firewall probe.");
            clock.Advance(TimeSpan.FromMinutes(2));
            Check(!await ValidateDuringOutage() && editor.ServerValidationIsError &&
                !settings.ReadHostedSubscription().IsAllowed && settings.ReadHostedSubscription().CheckedUtc == initial.CheckedUtc,
                "The same failures after 24 hours must deny without extending prior proof.");

            async Task<bool> ValidateDuringOutage()
            {
                var pending = editor.PressValidateAsync();
                if (status == 0)
                    clock.Advance(TimeSpan.FromSeconds(10));
                return await pending.WaitAsync(Timeout);
            }
        }
    }

    internal static async Task SettingsAndBackgroundDenialsFenceLateSuccess()
    {
        foreach (var settingsFirst in new[] { true, false })
        {
            using var fixture = new SavedCredentialTests.Fixture();
            var settings = fixture.Load();
            SignIn(settings, "subscriber", "initial");
            var clock = new EntitlementClock(VerificationStart);
            var entered = Signal();
            var release = Signal();
            var calls = 0;
            using var client = new HttpClient(new AsyncStubHandler(async (_, _) =>
            {
                switch (Interlocked.Increment(ref calls))
                {
                    case 1:
                        return SettingsVerifiedResponse(1, VerificationStart.AddDays(2));
                    case 2:
                        entered.SetResult();
                        await release.Task;
                        return SettingsVerifiedResponse(2, VerificationStart.AddDays(2), token: "retired-success");
                    case 3:
                        return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    case 4:
                        return SettingsVerifiedResponse(1, VerificationStart.AddDays(2), token: "new-success");
                    default:
                        throw new InvalidOperationException("A recent Settings result should be reusable by background checks.");
                }
            }));
            using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
            var editor = new SettingsViewModel(settings, service);
            Check(await editor.PressValidateAsync(), "The initial grant should be verified through Settings.");
            clock.Advance(TimeSpan.FromMinutes(30));

            Task<bool> Background() => IsAllowedAsync(service.EnsureSubscriptionAsync(default));
            var older = settingsFirst ? editor.PressValidateAsync() : Background();
            await entered.Task.WaitAsync(Timeout);
            var newer = settingsFirst ? Background() : editor.PressValidateAsync();
            Check(!await newer.WaitAsync(Timeout) && !settings.ReadHostedSubscription().IsAllowed,
                "The newer denial must retire the grant while the older success is still pending.");
            release.SetResult();
            Check(!await older.WaitAsync(Timeout) && !settings.ReadHostedSubscription().IsAllowed &&
                settings.AuthSessionToken == "settings-verified" && editor.FirewallProbes == 1,
                "An old success from either entry point cannot restore the grant, replace the session or verify the firewall.");
            Check(await editor.PressValidateAsync() && settings.AuthSessionToken == "new-success" &&
                (await service.EnsureSubscriptionAsync(default)).EffectiveTier == 1 && calls == 4,
                "A newly started successful verification must establish fresh proof after denial and supply the shared result.");
        }
    }

    internal static async Task SettingsValidateFencesOverlapsSwitchAndSignOut()
    {
        foreach (var transition in new[] { "new validation", "account switch", "sign out" })
        {
            using var fixture = new SavedCredentialTests.Fixture();
            var settings = fixture.Load();
            SignIn(settings, "first", "initial");
            var clock = new EntitlementClock(VerificationStart);
            var entered = Signal();
            var release = Signal();
            var calls = 0;
            using var client = new HttpClient(new AsyncStubHandler(async (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.SetResult();
                    await release.Task;
                    return SettingsVerifiedResponse(2, VerificationStart.AddDays(1), token: "obsolete");
                }
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }));
            using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
            var editor = new SettingsViewModel(settings, service);
            var older = editor.PressValidateAsync();
            await entered.Task.WaitAsync(Timeout);
            var expectedUser = "";
            var expectedToken = "";
            if (transition == "new validation")
            {
                Check(!await editor.PressValidateAsync(), "The newer Settings attempt is a denial.");
                expectedUser = "first";
                expectedToken = "initial";
            }
            else if (transition == "account switch")
            {
                settings.AuthServerBaseUrl = OtherEndpoint;
                SignIn(settings, "replacement", "other-session");
                expectedUser = "replacement";
                expectedToken = "other-session";
            }
            else
                settings.ClearServerCredentials(Recorder);

            release.SetResult();
            Check(!await older.WaitAsync(Timeout) && editor.FirewallProbes == 0 && editor.SavedSelections == 0 &&
                settings.BasicAuthUsername == expectedUser && settings.AuthSessionToken == expectedToken &&
                !settings.ReadHostedSubscription().IsAllowed,
                "Earlier Settings HTTP success cannot replace a newer validation, a different account or sign-out.");
        }
    }

    internal static async Task SettingsValidateKeepsCurrentAccountOnCandidateFailure()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "saved-account", "current-token");
        var clock = new EntitlementClock(VerificationStart);
        var seen = 0;
        using var client = new HttpClient(new AsyncStubHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(json.RootElement.GetProperty("session_token").GetString() == "",
                "A candidate password, user or authority must never get the old account's token.");
            return new HttpResponseMessage(++seen == 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized);
        }));
        using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
        var proof = new HostedSubscriptionSnapshot(true, true, 2, VerificationStart.UtcDateTime,
            VerificationStart.AddDays(1).UtcDateTime, null, "Premium", "", "Verified");
        settings.SaveHostedSubscription(proof);
        var editor = new SettingsViewModel(settings, service);
        using var active = HostedEntitlement.For(settings).Protect(default, clock);
        var accountVersion = settings.ReadAccountSession().AccountVersion;
        editor.BasicAuthUsername = "other-account";
        Check(!await editor.PressValidateAsync(), "A new account's invalid credentials must fail.");
        editor.BasicAuthUsername = "saved-account";
        editor.BasicAuthPassword = "incorrect-replacement";
        Check(!await editor.PressValidateAsync(), "Invalid replacement credentials are a failed candidate login.");
        editor.AuthServerBaseUrl = OtherEndpoint;
        Check(!await editor.PressValidateAsync(), "An unrelated authority may not borrow the current grant during an outage.");
        editor.BasicAuthUsername = "";
        editor.BasicAuthPassword = "";
        Check(!await editor.PressValidateAsync() && editor.ServerValidationIsError && seen == 3,
            "Missing fields on another authority must show a validation error without an auth request.");
        Check(settings.ReadHostedSubscription() == proof && settings.ReadAccountSession().AccountVersion == accountVersion &&
            settings.AuthServerBaseUrl.Length == 0 && settings.BasicAuthUsername == "saved-account" &&
            settings.BasicAuthPassword == "synthetic-password" && settings.AuthSessionToken == "current-token" &&
            !active.Token.IsCancellationRequested && editor.FirewallProbes == 0 && editor.SavedSelections == 0,
            "A failed candidate must preserve the saved credentials, signed-in session, grant and active access.");
    }

    internal static async Task SettingsOutageCannotMaskAnAuthoritativeDenial()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "subscriber", "existing-session");
        var clock = new EntitlementClock(VerificationStart);
        var deny = Signal();
        var calls = 0;
        using var client = new HttpClient(new AsyncStubHandler(async (_, _) =>
        {
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    return SettingsVerifiedResponse(1, VerificationStart.AddDays(2));
                case 2:
                    await deny.Task;
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                default:
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
        }));
        using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
        var editor = new SettingsViewModel(settings, service);
        Check(await editor.PressValidateAsync(), "The test needs a verified grant.");
        clock.Advance(TimeSpan.FromMinutes(30));
        var automatic = service.EnsureSubscriptionAsync(default);
        Check(!await editor.PressValidateAsync() && settings.ReadHostedSubscription().IsAllowed && calls == 3,
            "The overlapping Settings outage initially retains prior proof.");
        deny.SetResult();
        Check(!(await automatic.WaitAsync(Timeout)).IsAllowed && !settings.ReadHostedSubscription().IsAllowed,
            "An authoritative denial is still binding after a newer transient failure.");
    }

    internal static async Task SettingsDenialFencesPendingSessionAdoption()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "subscriber", "current-session");
        var clock = new EntitlementClock(VerificationStart);
        var adopting = Signal();
        var allowAdoption = Signal();
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ => ++calls == 1
            ? SettingsVerifiedResponse(2, VerificationStart.AddDays(1), token: "obsolete-session")
            : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var telemetry = new TelemetryStub(settings)
        {
            BeforeAdoption = async () =>
            {
                adopting.SetResult();
                await allowAdoption.Task;
            }
        };
        using var service = new SubscriptionService(settings, telemetry, client, clock);
        var automatic = service.EnsureSubscriptionAsync(default);
        await adopting.Task.WaitAsync(Timeout);
        var editor = new SettingsViewModel(settings, service);
        Check(!await editor.PressValidateAsync() && !settings.ReadHostedSubscription().IsAllowed,
            "Settings denial must be applied before a pending session adoption completes.");
        allowAdoption.SetResult();
        Check(!(await automatic.WaitAsync(Timeout)).IsAllowed && settings.AuthSessionToken == "current-session",
            "The older token and allowed result must both be fenced.");
    }

    internal static async Task SettingsValidateHonorsLocalOnlyGeneration()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "subscriber", "current-session");
        var clock = new EntitlementClock(VerificationStart);
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        using var client = new HttpClient(new AsyncStubHandler(async (_, _) =>
        {
            calls++;
            entered.SetResult();
            await release.Task;
            return SettingsVerifiedResponse(2, VerificationStart.AddDays(1), token: "privacy-retired");
        }));
        using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
        var editor = new SettingsViewModel(settings, service);
        var older = editor.PressValidateAsync();
        await entered.Task.WaitAsync(Timeout);
        settings.LocalOnlyMode = true;
        Check(!await editor.PressValidateAsync() && calls == 1, "Local Only must block new Settings validation requests.");
        settings.LocalOnlyMode = false;
        release.SetResult();
        Check(!await older.WaitAsync(Timeout) && settings.AuthSessionToken == "current-session" &&
            !settings.ReadHostedSubscription().IsAllowed && editor.FirewallProbes == 0,
            "Returning to Normal Mode must not resurrect a retired in-flight Settings check.");
    }

    private static async Task<bool> IsAllowedAsync(Task<SubscriptionCheckResult> request) => (await request).IsAllowed;

    private static HttpResponseMessage SettingsVerifiedResponse(int tier, DateTimeOffset expires,
        DateTimeOffset? period = null, DateTimeOffset? trial = null, DateTimeOffset? credential = null,
        DateTimeOffset? session = null, string? status = null, string token = "settings-verified") => JsonResponse(new
    {
        ok = true,
        session_token = token,
        credential_expires_at = credential?.ToString("O"),
        session = new { expires_at = session?.ToString("O") },
        subscription = new
        {
            active = true,
            tier_level = tier,
            status = status ?? (trial.HasValue ? "trialing" : "active"),
            expires_at = expires.ToString("O"),
            period_end_at = period?.ToString("O"),
            trial_ends_at = trial?.ToString("O")
        }
    });
}
