using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JoesScanner.Helpers;
using JoesScanner.Services;

internal static class WordPressOwnershipTests
{
    private const string Recorder = HostedServerRules.DefaultServerUrl;
    private const string OtherEndpoint = "https://other-wordpress.test";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static HostedSubscriptionSnapshot Active(int tier) =>
        new(true, true, tier, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), null, "Plan", "", "Verified");

    internal static async Task ValidationOrdering()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "original", "original-token");
        var attempts = new ConnectionValidationRequests();
        var oldSession = WordPressSession.Capture(settings);
        var old = attempts.Begin();
        var release = Signal();
        var finished = Task.Run(async () =>
        {
            await release.Task;
            Check(!attempts.TryApply(old, oldSession, () => true, () => SignIn(settings, "obsolete", "old-token")),
                "A late validation success must not replace the new account.");
            Check(!attempts.TryApply(old, oldSession, () => true, () => settings.ClearServerCredentials(Recorder)),
                "A late failed request must not erase the new account.");
        });
        var newer = attempts.Begin();
        Check(!attempts.TryApply(old, oldSession, () => true, () => throw new InvalidOperationException()),
            "The older response must be rejected as soon as a newer request begins.");
        settings.AuthSessionToken = "refreshed-original-token";
        Check(attempts.TryApply(newer, oldSession, () => true, () => SignIn(settings, "replacement", "new-token")),
            "The current request must publish.");
        release.SetResult();
        await finished.WaitAsync(Timeout);
        Check(settings.BasicAuthUsername == "replacement" && settings.AuthSessionToken == "new-token", "The replacement login must survive.");
        var signOut = attempts.Begin();
        Check(attempts.TryApply(signOut, WordPressSession.Capture(settings), () => true,
            () => settings.ClearServerCredentials(Recorder)), "Explicit removal must be accepted.");
        Check(!fixture.Load().TryGetServerCredentials(Recorder, out _, out _) && settings.AuthSessionToken.Length == 0 &&
            !settings.ReadHostedSubscription().IsAllowed, "Logout must retire authentication and cached entitlement.");
    }

    internal static async Task SubscriptionSwitch()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "earlier", "earlier-token");
        settings.SaveHostedSubscription(Active(2));
        var sent = Signal();
        var respond = Signal();
        using var client = new HttpClient(new AsyncStubHandler(async (_, _) =>
        {
            sent.SetResult();
            await respond.Task;
            return ActiveResponse(2);
        }));
        using var subscription = new SubscriptionService(settings, new TelemetryStub(settings), client);
        var result = subscription.EnsureSubscriptionAsync(default);
        await sent.Task.WaitAsync(Timeout);
        settings.AuthServerBaseUrl = OtherEndpoint;
        SignIn(settings, "replacement", "other-token");
        Check(!settings.ReadHostedSubscription().IsAllowed, "A new account must not inherit the previous account's tier.");
        settings.SaveHostedSubscription(Active(1));
        respond.SetResult();
        Check((await result.WaitAsync(Timeout)).ErrorCode == "account_changed" &&
            settings.ReadHostedSubscription().Tier == 1 && settings.AuthSessionToken == "other-token",
            "A late verification must not publish its tier or token into the replacement account.");
    }

    internal static async Task SubscriptionFailuresAndRestart()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var first = fixture.Load();
        SignIn(first, "saved-login", "initial-token");
        first.SaveHostedSubscription(Active(1));
        var settings = fixture.Load();
        Check(settings.AuthSessionToken.Length == 0 && settings.ReadHostedSubscription().Tier == 1,
            "A restart must reload the protected pair and per-account entitlement without persisting its token.");
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ => Interlocked.Increment(ref calls) switch
        {
            1 => new((HttpStatusCode)429),
            2 => new(HttpStatusCode.ServiceUnavailable),
            3 => throw new TaskCanceledException("Synthetic timeout"),
            4 => throw new HttpRequestException("Synthetic connection failure"),
            _ => ActiveResponse(1)
        }));
        using var service = new SubscriptionService(settings, new TelemetryStub(settings), client);
        Check((await service.EnsureSubscriptionAsync(default)).ErrorCode == "http_429" &&
            (await service.EnsureSubscriptionAsync(default)).ErrorCode == "http_503",
            "Transient HTTP responses must be reported.");
        Check((await service.EnsureSubscriptionAsync(default)).ErrorCode == "offline_cached" &&
            (await service.EnsureSubscriptionAsync(default)).ErrorCode == "offline_cached",
            "Timeout and connection failures may reuse only this account's valid cache.");
        Check((await service.EnsureSubscriptionAsync(default)).VerifiedOnlineTier == 1 &&
            fixture.Load().BasicAuthUsername == "saved-login", "A successful reconnect must reuse the stored login.");
    }

    internal static async Task LookupSwitch()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "initial", "initial-token");
        var started = Signal();
        var finish = Signal();
        using var client = new HttpClient(new AsyncStubHandler(async (request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "joesscanner.com")
            {
                Check(uri.Query.Contains("session_token=initial-token"), "The original endpoint must only receive its own token.");
                started.SetResult();
                await finish.Task;
            }
            else
                Check(uri.Host == "other-wordpress.test" && uri.Query.Contains("session_token=other-token"),
                    "The replacement endpoint must only receive its own token.");
            var labels = "[{\"label\":\"Receiver\",\"value\":\"1\"}]";
            return JsonResponse(new { ok = true, receivers_json = labels });
        }));
        using var service = new AuthLookupsSyncService(settings, client);
        var stale = service.TryFetchSeedAsync(Recorder, default);
        await started.Task.WaitAsync(Timeout);
        settings.AuthServerBaseUrl = OtherEndpoint;
        SignIn(settings, "replacement", "other-token");
        var fresh = await service.TryFetchSeedAsync(Recorder, default).WaitAsync(Timeout);
        finish.SetResult();
        Check(fresh?.Receivers.Count == 1 && await stale.WaitAsync(Timeout) == null,
            "The current lookup must be usable and the earlier response must be discarded.");
    }

    internal static async Task Redirect()
    {
        using var original = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        original.Start();
        target.Start();
        using var cancellation = new CancellationTokenSource(Timeout);
        var redirect = Task.Run(async () =>
        {
            using var connection = await original.AcceptTcpClientAsync(cancellation.Token);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellation.Token) is { Length: > 0 }) { }
            var port = ((IPEndPoint)target.LocalEndpoint).Port;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port}/other\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
                cancellation.Token);
        });
        using var client = WordPressSession.CreateClient();
        using var content = new StringContent("synthetic-token");
        using var response = await client.PostAsync($"http://127.0.0.1:{((IPEndPoint)original.LocalEndpoint).Port}/auth",
            content, cancellation.Token);
        await redirect;
        Check(response.StatusCode == HttpStatusCode.TemporaryRedirect && !target.Pending(),
            "The protected transport must never send the body to a redirect target.");
    }

    internal static async Task CommunicationsSwitch()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "initial", "initial-token");
        var started = Signal();
        var finish = Signal();
        using var client = new HttpClient(new AsyncStubHandler(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var old = request.RequestUri!.Host == "joesscanner.com";
            Check(root.GetProperty("session_token").GetString() == (old ? "initial-token" : "other-token") &&
                root.GetProperty("since_seq").GetInt64() == 0 && root.GetProperty("force_snapshot").GetInt32() == 1,
                "Communications must send the endpoint's own token and request a fresh snapshot.");
            if (old)
            {
                started.SetResult();
                await finish.Task;
            }
            return JsonResponse(new { ok = true, next_seq = old ? 100 : 200,
                snapshot = new[] { new { id = old ? 10 : 20, message_text = "sample", created_at_utc = DateTime.UtcNow.ToString("O") } } });
        }));
        var coordinator = new CommunicationsSyncCoordinator(new CommunicationsService(client), settings);
        var stale = coordinator.SyncOnceAsync(false, false, default);
        await started.Task.WaitAsync(Timeout);
        settings.AuthServerBaseUrl = OtherEndpoint;
        SignIn(settings, "replacement", "other-token");
        finish.SetResult();
        await stale.WaitAsync(Timeout);
        Check(coordinator.Messages.Count == 0, "The superseded account response must be dropped.");
        await coordinator.SyncOnceAsync(false, false, default).WaitAsync(Timeout);
        Check(coordinator.Messages.Single().Id == 20, "Only the current account's snapshot may be published.");
    }

    private static void SignIn(SettingsService settings, string user, string token)
    {
        settings.ServerUrl = Recorder;
        settings.SetServerCredentials(Recorder, user, "synthetic-password");
        settings.AuthSessionToken = token;
    }

    private static HttpResponseMessage ActiveResponse(int tier) => JsonResponse(new
    {
        ok = true, session_token = "refreshed-token",
        subscription = new { active = true, tier_level = tier, expires_at = DateTime.UtcNow.AddDays(1).ToString("O") }
    });
    private static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class TelemetryStub(ISettingsService settings) : ITelemetryService
    {
        public void TrackAppStarted() { }
        public void TrackAppStopping() { }
        public void TrackConnectionAttempt(string streamServerUrl, bool isHostedServer) { }
        public void TrackConnectionStatusChanged(string status, string? detailMessage, string streamServerUrl) { }
        public void StartMonitoringHeartbeat(string streamServerUrl) { }
        public void StopMonitoringHeartbeat(string reason) { }
        public Task ResetSessionAsync(string reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TryFlushQueueAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AdoptSessionTokenAsync(string newSessionToken, string reason, CancellationToken cancellationToken,
            long? expectedVersion = null)
        {
            settings.TryReplaceAccountSession(expectedVersion ?? settings.ReadAccountSession().Version, newSessionToken);
            return Task.CompletedTask;
        }
    }
}

internal static class AppInfo
{
    internal static (string VersionString, string BuildString) Current => ("1", "1");
}

internal static class DeviceInfo
{
    internal static string Platform => "Test";
    internal static string Idiom => "Test";
    internal static string Manufacturer => "Test";
    internal static string Model => "Test";
    internal static string VersionString => "1";
}

namespace JoesScanner.Services
{
    internal static class CommsMessageFormatter
    {
        internal static Microsoft.Maui.Controls.FormattedString? ToFormattedString(string? text) => null;
    }

    internal static class AppStateStore
    {
        internal static string GetString(string key, string fallback) => fallback;
        internal static void SetString(string key, string value) { }
        internal static long GetLong(string key, long fallback) => fallback;
        internal static void SetLong(string key, long value) { }
    }
}

namespace Microsoft.Maui.Controls
{
    public sealed class FormattedString
    {
        public List<Span> Spans { get; } = new();
    }

    public sealed class Span
    {
        public string? Text { get; init; }
        public Microsoft.Maui.Graphics.Color? TextColor { get; init; }
        public int FontAttributes { get; init; }
        public int TextDecorations { get; init; }
        public double FontSize { get; init; }
    }
}

namespace Microsoft.Maui.Graphics
{
    public sealed class Color
    {
        public float Red { get; init; }
        public float Green { get; init; }
        public float Blue { get; init; }
        public float Alpha { get; init; }
    }
}
