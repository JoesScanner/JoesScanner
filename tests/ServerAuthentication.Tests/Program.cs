using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;

var cases = new (string Name, Func<Task> Run)[]
{
    ("Built-in and directory requests use only endpoint-issued credentials", HostedSelection),
    ("Missing session, inactive subscription, or empty issuance blocks requests", MissingCredentials),
    ("A denied POST refreshes and replays the same body once", RotatedCredentials),
    ("A second denial limits refreshes without blocking other endpoints", DeniedCredentials),
    ("API denial after rotation does not replay or fall back", DeniedRefresh),
    ("Custom Basic Auth stays on the configured origin", CustomCredentials),
    ("Redirects never carry scanner credentials", RedirectIsolation),
    ("Port changes, downgrades, and embedded credentials are blocked", OriginIsolation),
    ("A changed WordPress session does not use credentials from the old session", SessionIsolation),
    ("The WebSocket handshake refreshes firewall credentials", WebSocketRotation),
    ("Rapid selections cancel obsolete playback and late completion cannot replace the latest selection", RapidPlaybackSelections),
    ("A canceled firewall waiter sends no audio request and does not poison the next selection", CanceledFirewallWait),
    ("Canceled credential issuance releases the firewall gate for the next selection", CanceledFirewallIssuance),
    ("HTTP timeouts and unexpected cancellation remain playback failures", UnexpectedPlaybackTimeout),
    ("Only completion or deliberate skip advances playback; explicit retry retains the failed item", MobileRecoveryTests.PlaybackOutcomes),
    ("Foreground recovery verifies hosted tiers and preserves custom history", MobileRecoveryTests.Entitlement),
    ("Foreground recovery aborts a half-open socket, merges late audio, and delivers subsequent live calls once", MobileRecoveryTests.ReconnectAndLateAudio)
};

foreach (var (name, run) in cases)
{
    await run();
    Console.WriteLine("PASS " + name);
}

return;

static async Task HostedSelection()
{
    using var fixture = new Fixture();
    using var client = fixture.CreateClient(request =>
    {
        var expected = request.RequestUri!.Host == "directory.test" ? fixture.Rotated : fixture.Issued;
        Check(request.Headers.Authorization?.Equals(expected) == true, "Wrong credential source.");
        return Reply(HttpStatusCode.OK);
    });

    using var edited = new HttpRequestMessage(HttpMethod.Head, HostedServerRules.DefaultServerUrl);
    edited.Options.Set(ServerAuthenticationHandler.CustomProbe, true);
    edited.Headers.Authorization = fixture.Manual;
    using var hosted = await client.SendAsync(edited);
    using var directory = await client.GetAsync("https://directory.test:8443/history");
    using var cached = await client.GetAsync(HostedServerRules.DefaultServerUrl + "/talkgroupsjson");
    Check(fixture.IssuanceCount == 2 && fixture.DirectoryId == 42 &&
          fixture.DirectoryOrigin == "https://directory.test:8443", "Wrong firewall issuance scope.");
}

static async Task MissingCredentials()
{
    using var fixture = new Fixture();
    using var client = fixture.CreateClient(_ => throw new Exception("Scanner request must be blocked."));
    fixture.Settings.Session = string.Empty;
    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "verified");
    fixture.Settings.Session = Guid.NewGuid().ToString();
    fixture.Settings.ActiveSubscription = false;
    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "active subscription");
    Check(fixture.IssuanceCount == 0, "Ineligible accounts contacted WordPress.");

    fixture.Settings.ActiveSubscription = true;
    fixture.Issue = _ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { ok = true })
    };
    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "usable");
    Check(fixture.IssuanceCount == 1, "Expected one issuance attempt.");
}

static async Task RotatedCredentials()
{
    using var fixture = new Fixture();
    var sends = 0;
    fixture.Issue = count => fixture.IssuedReply(count == 1 ? fixture.Issued : fixture.Rotated);
    using var client = fixture.CreateClient(request =>
    {
        sends++;
        Check(request.Method == HttpMethod.Post &&
              request.Content!.ReadAsStringAsync().GetAwaiter().GetResult() == "draw=1", "POST replay changed its method or body.");
        Check(request.Headers.Authorization?.Equals(sends == 1 ? fixture.Issued : fixture.Rotated) == true,
            "The request did not use the expected issued credential.");
        return Reply(sends == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
    });

    using var result = await client.PostAsync(HostedServerRules.DefaultServerUrl + "/calljson", new StringContent("draw=1"));
    Check(result.IsSuccessStatusCode && sends == 2 && fixture.IssuanceCount == 2, "Expected one refresh and retry.");
}

static async Task DeniedCredentials()
{
    using var fixture = new Fixture();
    var sends = 0;
    using var client = fixture.CreateClient(request =>
    {
        Check(request.Headers.Authorization?.Equals(fixture.Issued) == true, "Unexpected credential after denial.");
        sends++;
        return Reply(request.RequestUri!.AbsolutePath == "/audio" ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
    });

    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "rejected");
    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "attempted recently");
    using var audio = await client.GetAsync(HostedServerRules.DefaultServerUrl + "/audio");
    Check(sends == 4 && fixture.IssuanceCount == 2 && audio.IsSuccessStatusCode, "Denial caused excess refresh or blocked another endpoint.");
}

static async Task DeniedRefresh()
{
    using var fixture = new Fixture();
    fixture.Issue = count => count == 1 ? fixture.IssuedReply(fixture.Issued) : Reply(HttpStatusCode.Unauthorized);
    var sends = 0;
    using var client = fixture.CreateClient(_ =>
    {
        sends++;
        return Reply(HttpStatusCode.Unauthorized);
    });

    await FailsAsync(() => client.GetAsync(HostedServerRules.DefaultServerUrl), "could not issue");
    Check(sends == 1 && fixture.IssuanceCount == 2, "Request ran again after WordPress denied refresh.");
}

static async Task CustomCredentials()
{
    using var fixture = new Fixture();
    fixture.Settings.ServerUrl = "http://scanner.test:8080/root";
    using var client = fixture.CreateClient(request =>
    {
        var isCustom = request.RequestUri!.Scheme == "http" &&
            request.RequestUri.Host == "scanner.test" && request.RequestUri.Port == 8080;
        Check(isCustom
            ? request.Headers.Authorization?.Equals(fixture.Manual) == true
            : request.Headers.Authorization == null, "Custom credentials exceeded their origin.");
        return Reply(isCustom ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
    });
    using var result = await client.GetAsync("http://scanner.test:8080/calljson");
    using var external = await client.GetAsync("https://cdn.test/clip");
    using var otherPort = await client.GetAsync("http://scanner.test:8081/clip");
    Check(result.StatusCode == HttpStatusCode.Unauthorized && fixture.IssuanceCount == 0, "Custom denial used the WordPress API.");
}

static async Task RedirectIsolation()
{
    using var fixture = new Fixture();
    var sends = 0;
    using var client = fixture.CreateClient(request =>
    {
        sends++;
        if (sends == 1)
        {
            Check(request.Headers.Authorization?.Equals(fixture.Issued) == true, "Initial request lacks issued credentials.");
            return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://cdn.test/clip") } };
        }

        Check(request.RequestUri!.Host == "cdn.test" && request.Headers.Authorization == null,
            "Credentials crossed a redirect.");
        return Reply(HttpStatusCode.OK);
    });

    using var result = await client.GetAsync(HostedServerRules.DefaultServerUrl + "/clip");
    Check(result.IsSuccessStatusCode && sends == 2 && fixture.IssuanceCount == 1, "Unexpected redirect outcome.");
}

static async Task OriginIsolation()
{
    using var fixture = new Fixture();
    using var client = fixture.CreateClient(_ => throw new Exception("Invalid origin was contacted."));
    await FailsAsync(() => client.GetAsync("http://app.joesscanner.com/calljson"), "HTTPS origin");
    await FailsAsync(() => client.GetAsync("https://app.joesscanner.com:444/calljson"), "HTTPS origin");
    var embedded = new UriBuilder(HostedServerRules.DefaultServerUrl) { UserName = Guid.NewGuid().ToString() }.Uri;
    try
    {
        using var response = await client.GetAsync(embedded);
        throw new Exception("Expected embedded credentials to be blocked.");
    }
    catch (HttpRequestException ex) when (ex.Message.Contains("embedded credentials", StringComparison.Ordinal))
    {
    }

    Check(fixture.IssuanceCount == 0, "Invalid origin contacted WordPress.");
}

static async Task SessionIsolation()
{
    using var fixture = new Fixture();
    fixture.Issue = count => fixture.IssuedReply(count == 1 ? fixture.Issued : fixture.Rotated);
    using var client = fixture.CreateClient(request =>
    {
        var expected = fixture.IssuanceCount == 1 ? fixture.Issued : fixture.Rotated;
        Check(request.Headers.Authorization?.Equals(expected) == true, "Unexpected credential for session.");
        return Reply(HttpStatusCode.OK);
    });
    using var first = await client.GetAsync(HostedServerRules.DefaultServerUrl);
    fixture.Settings.Session = Guid.NewGuid().ToString();
    using var second = await client.GetAsync(HostedServerRules.DefaultServerUrl);
    Check(fixture.IssuanceCount == 2, "The old session cache survived a session change.");
}

static async Task WebSocketRotation()
{
    using var fixture = new Fixture();
    fixture.Issue = count => fixture.IssuedReply(count == 1 ? fixture.Issued : fixture.Rotated);
    var sends = 0;
    using var invoker = new HttpMessageInvoker(fixture.CreateHandler(request =>
    {
        sends++;
        Check(request.RequestUri!.AbsolutePath == "/Calls", "Wrong upgrade destination.");
        Check(request.Headers.Authorization?.Equals(sends == 1 ? fixture.Issued : fixture.Rotated) == true,
            "The upgrade request used the wrong firewall credential.");
        if (sends == 1)
            return Reply(HttpStatusCode.Unauthorized);

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            request.Headers.GetValues("Sec-WebSocket-Key").Single() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)
        {
            Content = new UpgradeContent()
        };
        response.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        response.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        response.Headers.TryAddWithoutValidation("Sec-WebSocket-Accept", accept);
        return response;
    }));

    using var socket = new ClientWebSocket();
    await socket.ConnectAsync(new Uri("wss://app.joesscanner.com/Calls"), invoker, CancellationToken.None);
    Check(socket.State == WebSocketState.Open && sends == 2 && fixture.IssuanceCount == 2, "Upgrade retry failed.");
}

static async Task RapidPlaybackSelections()
{
    var requests = new LivePlaybackRequests();
    using var queue = requests.Begin(manual: false);
    var earlyDownload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var nextDownload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var first = requests.Begin(manual: true);
    var firstToken = first.Token;
    var firstWork = FinishSelectionAsync(first, earlyDownload.Task);
    using var second = requests.Begin(manual: true);
    var secondToken = second.Token;
    var secondWork = FinishSelectionAsync(second, nextDownload.Task);
    using var last = requests.Begin(manual: true);
    Check(queue.Token.IsCancellationRequested && firstToken.IsCancellationRequested && secondToken.IsCancellationRequested,
        "A tap must cancel the queue and previous selections immediately.");

    Check(await FinishSelectionAsync(last, Task.FromResult("latest")) == "latest",
        "The latest selection should be the only request eligible to start playback.");
    nextDownload.SetResult("obsolete");
    earlyDownload.SetResult("even older");
    Check(await firstWork == null && await secondWork == null, "Late downloads must not start audio.");
    second.Dispose();
    first.Dispose();
    Check(last.CanContinue && requests.HasManualSelection, "Cleanup for an old request must leave the new request active.");
    last.Dispose();
    Check(!requests.HasManualSelection, "Queue playback can resume after the current selection finishes.");

    static async Task<string?> FinishSelectionAsync(LivePlaybackRequests.Request request, Task<string> download)
    {
        var path = await download;
        return request.CanContinue ? path : null;
    }
}

static async Task CanceledFirewallWait()
{
    using var fixture = new Fixture();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var issuer = new HttpClient(new AsyncStubHandler(async (_, token) =>
    {
        started.SetResult();
        await release.Task.WaitAsync(token);
        return fixture.IssuedReply(fixture.Issued);
    }));
    var auth = new ServerAuthentication((ISettingsService)(object)fixture.Settings, issuer);
    var sent = new List<string>();
    using var audioClient = new HttpClient(new ServerAuthenticationHandler(auth, new StubHandler(request =>
    {
        lock (sent) sent.Add(request.RequestUri!.AbsolutePath);
        return Reply(HttpStatusCode.OK);
    })));
    var holdingCall = audioClient.GetAsync(HostedServerRules.DefaultServerUrl + "/holding.wav");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var requests = new LivePlaybackRequests();
    using var earlier = requests.Begin(manual: true);
    var obsoleteAudio = audioClient.GetAsync(HostedServerRules.DefaultServerUrl + "/obsolete.wav", earlier.Token);
    using var latest = requests.Begin(manual: true);
    await ExpectPlaybackCancellationAsync(obsoleteAudio, earlier.Token);
    var latestAudio = audioClient.GetAsync(HostedServerRules.DefaultServerUrl + "/latest.wav", latest.Token);
    release.SetResult();
    using var firstResponse = await holdingCall.WaitAsync(TimeSpan.FromSeconds(5));
    using var latestResponse = await latestAudio.WaitAsync(TimeSpan.FromSeconds(5));
    Check(sent.Count == 2 && sent.Contains("/holding.wav") && sent.Contains("/latest.wav"),
        "The canceled auth waiter must never reach the audio transport.");
}

static async Task CanceledFirewallIssuance()
{
    using var fixture = new Fixture();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var issued = 0;
    using var issuer = new HttpClient(new AsyncStubHandler(async (_, token) =>
    {
        if (Interlocked.Increment(ref issued) == 1)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        return fixture.IssuedReply(fixture.Issued);
    }));
    var auth = new ServerAuthentication((ISettingsService)(object)fixture.Settings, issuer);
    using var client = new HttpClient(new ServerAuthenticationHandler(auth, new StubHandler(_ => Reply(HttpStatusCode.OK))));
    using var obsolete = new CancellationTokenSource();
    var earlier = client.GetAsync(HostedServerRules.DefaultServerUrl + "/early.wav", obsolete.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    obsolete.Cancel();
    await ExpectPlaybackCancellationAsync(earlier, obsolete.Token);
    using var result = await client.GetAsync(HostedServerRules.DefaultServerUrl + "/next.wav").WaitAsync(TimeSpan.FromSeconds(5));
    Check(result.IsSuccessStatusCode && issued == 2, "Canceled issuance must release the gate without applying an auth failure cooldown.");
}

static async Task UnexpectedPlaybackTimeout()
{
    using var client = new HttpClient(new AsyncStubHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Reply(HttpStatusCode.OK);
    })) { Timeout = TimeSpan.FromMilliseconds(30) };
    using var selection = new CancellationTokenSource();
    try
    {
        using var response = await client.GetAsync("https://audio.test/call.wav", selection.Token);
        throw new Exception("A request timeout was expected.");
    }
    catch (TaskCanceledException ex)
    {
        Check(LivePlaybackRequests.IsTimeout(ex) && !LivePlaybackRequests.IsExpectedCancellation(ex, selection.Token),
            "An actual HttpClient timeout must remain a failure when the selection is active.");
        selection.Cancel();
        Check(!LivePlaybackRequests.IsExpectedCancellation(ex, selection.Token),
            "A subsequent selection cancellation must not mask an HTTP timeout.");
        using var unrelated = new CancellationTokenSource();
        Check(!LivePlaybackRequests.IsExpectedCancellation(new OperationCanceledException(unrelated.Token), selection.Token),
            "An exception tied to an uncanceled token must remain a failure.");
    }
    Check(!LivePlaybackRequests.IsExpectedCancellation(new OperationCanceledException(), CancellationToken.None),
        "An unexpected cancellation without a caller cancellation must remain a failure.");
}

static async Task ExpectPlaybackCancellationAsync(Task<HttpResponseMessage> work, CancellationToken token)
{
    try
    {
        using var response = await work.WaitAsync(TimeSpan.FromSeconds(5));
        throw new Exception("Obsolete playback should have canceled.");
    }
    catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, token))
    {
        // Both OperationCanceledException and TaskCanceledException are expected here.
    }
}

static async Task FailsAsync(Func<Task<HttpResponseMessage>> send, string expectedMessage)
{
    try
    {
        using var response = await send();
        throw new Exception("Expected firewall authentication to fail.");
    }
    catch (FirewallAuthenticationException ex) when (ex.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
    }
}

static HttpResponseMessage Reply(HttpStatusCode status) => new(status);

static void Check(bool predicate, string failure)
{
    if (!predicate)
        throw new Exception(failure);
}

sealed class Fixture : IDisposable
{
    private readonly ServerAuthentication _authentication;
    private readonly HttpClient _issuer;
    internal SettingsStub Settings { get; }
    internal AuthenticationHeaderValue Issued { get; } = RandomBasic();
    internal AuthenticationHeaderValue Rotated { get; } = RandomBasic();
    internal AuthenticationHeaderValue Manual => Basic(Settings.Username, Settings.Password);
    internal int IssuanceCount { get; private set; }
    internal int? DirectoryId { get; private set; }
    internal string? DirectoryOrigin { get; private set; }
    internal Func<int, HttpResponseMessage>? Issue { get; set; }

    internal Fixture()
    {
        var settings = DispatchProxy.Create<ISettingsService, SettingsStub>();
        Settings = (SettingsStub)(object)settings;
        _issuer = new HttpClient(new StubHandler(request =>
        {
            if (request.Method != HttpMethod.Post ||
                request.RequestUri!.AbsoluteUri != "https://wordpress.test/wp-json/joes-scanner/v1/server-firewall-credentials" ||
                request.Headers.Authorization != null)
            {
                throw new Exception("Wrong WordPress request or authentication mechanism.");
            }

            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var body = payload.RootElement;
            if (body.GetProperty("session_token").GetString() != Settings.Session ||
                body.GetProperty("device_id").GetString() != Settings.DeviceId ||
                body.TryGetProperty("username", out _) || body.TryGetProperty("password", out _))
            {
                throw new Exception("Wrong WordPress firewall issuance payload.");
            }

            if (body.GetProperty("directory_id").ValueKind == JsonValueKind.Number)
            {
                DirectoryId = body.GetProperty("directory_id").GetInt32();
                DirectoryOrigin = body.GetProperty("server_url").GetString();
            }

            IssuanceCount++;
            return Issue?.Invoke(IssuanceCount) ?? IssuedReply(DirectoryId.HasValue ? Rotated : Issued);
        }));
        _authentication = new ServerAuthentication(settings, _issuer);
    }

    internal HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> send) => new(CreateHandler(send));

    internal HttpMessageHandler CreateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new ServerAuthenticationHandler(_authentication, new StubHandler(send));

    internal HttpResponseMessage IssuedReply(AuthenticationHeaderValue sample)
    {
        var pair = Encoding.UTF8.GetString(Convert.FromBase64String(sample.Parameter!)).Split(':', 2);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { ok = true, username = pair[0], password = pair[1] })
        };
    }

    public void Dispose() => _issuer.Dispose();

    private static AuthenticationHeaderValue RandomBasic() => Basic(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

    private static AuthenticationHeaderValue Basic(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
}

class SettingsStub : DispatchProxy
{
    internal string Session { get; set; } = Guid.NewGuid().ToString();
    internal string DeviceId { get; } = Guid.NewGuid().ToString();
    internal string Username { get; } = Guid.NewGuid().ToString();
    internal string Password { get; } = Guid.NewGuid().ToString();
    internal string ServerUrl { get; set; } = HostedServerRules.DefaultServerUrl;
    internal bool ActiveSubscription { get; set; } = true;

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_AuthServerBaseUrl" => "https://wordpress.test",
        "get_ServerUrl" => ServerUrl,
        "get_BasicAuthUsername" => Username,
        "get_BasicAuthPassword" => Password,
        "get_AuthSessionToken" => Session,
        "get_DeviceInstallId" => DeviceId,
        nameof(ISettingsService.HasActiveHostedSubscription) => ActiveSubscription,
        nameof(ISettingsService.TryGetServerCredentials) => false,
        nameof(ISettingsService.GetCachedDirectoryServers) => new[]
        {
            new ServerDirectoryEntry
            {
                DirectoryId = 42,
                Url = "https://directory.test:8443/scanner",
                UsesApiFirewallCredentials = true
            }
        },
        _ => throw new NotSupportedException(method.Name)
    };
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(send(request));
}

sealed class AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
}

sealed class UpgradeContent : HttpContent
{
    protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => new MemoryStream();

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new NotSupportedException();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
