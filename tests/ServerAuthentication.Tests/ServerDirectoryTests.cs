using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;

internal static class ServerDirectoryTests
{
    private const string Account = "https://account.directory.test";
    private const string Recorder = "https://managed.directory.test/listen";
    private const string Other = "https://independent.directory.test";
    private const string End = "/wp-json/joes-scanner/v1/server-directory/";
    private static readonly DirectorySite Site = new(new Uri(Account + "/owner-dashboard"), "v1", "Synthetic terms");
    private static readonly DirectoryListingDraft Draft = new("Synthetic recorder",
        "https://recorder-user:recorder-password@managed.directory.test/listen?token=recorder-token#fragment", "Boise, ID", true, false);

    internal static async Task Sharing()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        settings.AuthServerBaseUrl = Account;
        settings.LocalOnlyMode = false;
        var store = new ScannerConnectionStore(fixture);
        var issued = 0;
        var submitted = 0;
        var submissionStatus = HttpStatusCode.Created;
        var grant = new string('a', 64);
        var client = new ServerDirectoryClient(settings, new StubHandler(request =>
        {
            Check(request.RequestUri!.GetLeftPart(UriPartial.Authority) == Account && request.Headers.Authorization == null,
                "Only the configured HTTPS account location is valid, with no reused authorization header.");
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var body = payload.RootElement;
            if (request.RequestUri.AbsolutePath == End + "submission-grant")
            {
                issued++;
                Check(body.EnumerateObject().Count() == 2 && body.GetProperty("account").GetString() == "test-account" &&
                    body.GetProperty("password").GetString() == "account-secret", "Account verification alone accepts the account login.");
                return Json(HttpStatusCode.OK, new { grant, user_id = 71 });
            }
            submitted++;
            var listing = body.GetProperty("listing");
            Check(request.RequestUri.AbsolutePath == End + "submissions" && body.EnumerateObject().Count() == 3 &&
                body.GetProperty("grant").GetString() == grant && body.GetProperty("terms_version").GetString() == Site.TermsVersion &&
                listing.EnumerateObject().Count() == 5 && listing.GetProperty("server_url").GetString() == Recorder &&
                listing.GetProperty("requires_scanner_login").GetString() == "1" &&
                listing.GetProperty("requires_firewall_auth").GetString() == "0" &&
                !body.GetRawText().Contains("secret") && !body.GetRawText().Contains("recorder-password") &&
                !body.GetRawText().Contains("recorder-token"), "The submission may only carry the one-use grant and permitted nonsecret fields.");
            return Json(submissionStatus, new { id = 42, status = "pending" });
        }));
        var local = store.Save(Recorder, "Synthetic recorder", null, null);
        Check(issued == 0 && submitted == 0 && local.DirectoryId == null, "Keeping a recorder local must make no cloud request.");
        Check(!client.HasAccount && await client.SubmitAsync(client.Begin(), Site, Draft) == DirectorySubmissionStatus.SignInRequired &&
            issued == 0 && submitted == 0, "A signed-out recorder owner must remain independent and use the website option.");
        settings.SetServerCredentials(HostedServerRules.DefaultServerUrl, "test-account", "account-secret");
        settings.AuthSessionToken = "saved-session-secret";
        settings.VerifiedFavoritesUserId = 71;
        Check(client.HasAccount && await client.SubmitAsync(client.Begin(), Site, Draft) == DirectorySubmissionStatus.Pending,
            "A free account should reach Pending Approval.");
        submissionStatus = HttpStatusCode.Conflict;
        Check(await client.SubmitAsync(client.Begin(), Site, Draft) == DirectorySubmissionStatus.Conflict,
            "An already-submitted listing must be reported separately.");
        submissionStatus = HttpStatusCode.ServiceUnavailable;
        await ExpectAsync<HttpRequestException>(() => client.SubmitAsync(client.Begin(), Site, Draft));
        var saved = store.Load(null).Single();
        Check(saved.Id == local.Id && saved.DirectoryId == null && saved.Enabled && issued == 3 && submitted == 3,
            "Sharing outcomes must never remove, change, or reclassify the local recorder.");
    }

    internal static async Task Handoff()
    {
        var account = new Uri(Account + End + "config");
        var plain = new Uri(Account + "/?page_id=17");
        foreach (var visibility in new[] { "private", "restricted", "public" })
        {
            var prefill = ServerDirectoryClient.BuildPrefill(account, plain, Draft.Address, visibility);
            Check(prefill.Host == account.Host && Uri.UnescapeDataString(prefill.Query) ==
                $"?page_id=17&directory_visibility={visibility}&directory_address={Recorder}",
                "The website handoff must preserve the canonical page and add only safe listing details.");
        }
        foreach (var unsafePage in new[] { "http://account.directory.test/page", "https://other.test/page",
            Account + ":8443/page", Account + "/page?token=unsafe", Account + "/page#unsafe" })
            await ExpectAsync<InvalidDataException>(() => Task.FromResult(
                ServerDirectoryClient.BuildPrefill(account, new Uri(unsafePage), Recorder, "private")));

        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        settings.AuthServerBaseUrl = Account;
        settings.LocalOnlyMode = false;
        var sent = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new PendingTransport(entered, () => Interlocked.Increment(ref sent));
        var client = new ServerDirectoryClient(settings, wire);
        var checking = client.GetSiteAsync(client.Begin());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        settings.LocalOnlyMode = true;
        await ExpectAsync<OperationCanceledException>(() => checking.WaitAsync(TimeSpan.FromSeconds(3)));
        Check(!client.CanShare && !client.HasAccount, "Local Only must close sharing and cancel pending website discovery.");
        await ExpectAsync<HttpRequestException>(() => Task.FromResult(client.Begin()));
        Check(sent == 1, "Local Only may not issue a discovery request.");
        settings.LocalOnlyMode = false;

        var available = new ServerDirectoryClient(settings, new StubHandler(_ =>
            Json(HttpStatusCode.OK, new { my_servers_url = Site.MyServers, terms_version = "v1", terms = "Expected" })));
        Check((await available.GetSiteAsync(available.Begin())).MyServers == Site.MyServers,
            "Opening My Servers must become possible again without restarting.");
    }

    internal static async Task DirectoryConnections()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        settings.AuthServerBaseUrl = Account;
        settings.LocalOnlyMode = false;
        var store = new ScannerConnectionStore(fixture);
        var source = ServerDirectoryClient.Source(WordPressSession.Capture(settings));
        var managed = store.Save(Recorder, "Initial name", 91, null, directorySource: source, directoryFirewall: false);
        var independent = store.Save(Other, "Independent", null, null);
        var third = store.Save("https://third.directory.test", "Another independent", null, null);
        settings.SetRecorderCredentials(managed.Id, Recorder, "managed-login", "managed-private");
        var guard = DirectoryRecorderPolicy.For(settings);
        var manager = new ServerConnectionManager(TimeProvider.System);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task Monitor(ServerConnectionManager.Connection connection) => Task.Delay(Timeout.InfiniteTimeSpan, connection.Token);
        var current = store.Load(null);
        guard.Update(current);
        manager.Synchronize(current, lifetime.Token, Monitor);
        var independentRun = manager.Snapshot().Single(connection => connection.Profile.Id == independent.Id);
        var thirdRun = manager.Snapshot().Single(connection => connection.Profile.Id == third.Id);

        var newName = Entry(91, Recorder, "Published name");
        var duplicateAddress = Entry(90, Other, "Different directory identity", firewall: true);
        settings.PersistDirectorySnapshot([newName, duplicateAddress], new HashSet<int>(), source);
        current = store.Load(null);
        guard.Update(current);
        Check(current[0].Id == managed.Id && current[0].Name == "Published name" &&
            current[1].Id == independent.Id && !current[1].Managed && !HostedServerRules.RequiresApiFirewallCredentials(settings, Other),
            "A metadata update must match a permanent directory ID and leave an independent address match alone.");
        await ExpectAsync<InvalidOperationException>(() => Task.FromResult(store.Save(Other, "Directory copy", 90, null)));

        settings.PersistDirectorySnapshot([], new HashSet<int> { 90, 91 }, source);
        current = new ScannerConnectionStore(fixture).Load(null);
        guard.Update(current);
        manager.Synchronize(current, lifetime.Token, Monitor);
        Check(current[0].Hold == DirectoryHold.Suspended && current[0].Endpoint == Recorder &&
            current[0].FilterKey == managed.FilterKey && manager.IsCurrent(independentRun) && manager.IsCurrent(thirdRun) &&
            manager.Snapshot().Length == 2, "A verified suspension persists and stops only the managed recorder.");
        var outbound = 0;
        using var scanner = ServerAuthenticationHandler.CreateClient(settings, new StubHandler(_ =>
        { outbound++; return Json(HttpStatusCode.OK, new { ok = true }); }));
        await ExpectAsync<HttpRequestException>(() => scanner.GetAsync(Recorder + "/calljson"));
        settings.LocalOnlyMode = true;
        using var independentReply = await scanner.GetAsync(Other + "/calljson");
        Check(outbound == 1 && independentReply.IsSuccessStatusCode, "Only the independent recorder is eligible in Local Only Mode.");
        settings.LocalOnlyMode = false;

        var error = new ServerDirectoryClient(settings, new StubHandler(_ => Json(HttpStatusCode.BadGateway, new { })));
        await ExpectAsync<HttpRequestException>(() => error.FetchAsync(error.Begin()));
        Check(store.Load(null)[0].Hold == DirectoryHold.Suspended && settings.GetCachedDirectoryServers().Count == 0,
            "A failed fetch cannot change the last committed directory or clear a suspension.");
        settings.PersistDirectorySnapshot([newName], null, source);
        Check(store.Load(null)[0].DirectoryCandidate == null, "An older response without suspension state cannot reinstate a held listing.");
        settings.PersistDirectorySnapshot([newName], new HashSet<int>(), source);
        current = store.Load(null);
        manager.Synchronize(current, lifetime.Token, Monitor);
        Check(current[0].DirectoryBlocked && current[0].DirectoryCandidate == Recorder && manager.Snapshot().Length == 2,
            "Verified availability alone cannot reconnect a previously suspended listing.");
        Check(store.ConfirmDirectory(managed.Id, Recorder, "https://other-account.test") == null &&
            store.ConfirmDirectory(managed.Id, Recorder, source)?.Id == managed.Id,
            "Reinstatement requires confirmation of the verified source and address.");

        const string revised = "https://new-managed.directory.test/listen";
        settings.PersistDirectorySnapshot([Entry(91, revised, "Approved new address")], new HashSet<int>(), source);
        current = store.Load(null);
        Check(current[0].Hold == DirectoryHold.EndpointChanged && current[0].Endpoint == Recorder &&
            store.ConfirmDirectory(managed.Id, Recorder, source) == null,
            "An approved address change must wait for explicit confirmation.");
        var changed = store.ConfirmDirectory(managed.Id, revised, source);
        Check(changed is { DirectoryBlocked: false } && changed.Id == managed.Id && changed.FilterKey == managed.FilterKey &&
            settings.TryGetRecorderCredentials(managed.Id, Recorder, out _, out _) &&
            !settings.TryGetRecorderCredentials(managed.Id, revised, out _, out _),
            "Changing origins must retain identity and never transfer the old login.");
        settings.PersistDirectorySnapshot([], new HashSet<int>(), source);
        Check(settings.GetCachedDirectoryServers().Count == 0 &&
            store.Load(null).Any(profile => profile.Id == managed.Id && !profile.DirectoryBlocked),
            "Ordinary removal must clear the public directory choice and preserve the local recorder.");
        manager.Retire(_ => true);
    }

    private static ServerDirectoryEntry Entry(int id, string url, string name, bool firewall = false) =>
        new() { DirectoryId = id, Url = url, Name = name, UsesApiFirewallCredentials = firewall };

    private static HttpResponseMessage Json(HttpStatusCode status, object data) =>
        new(status) { Content = JsonContent.Create(data) };

    private static async Task ExpectAsync<T>(Func<Task> test) where T : Exception
    {
        try { await test(); }
        catch (T) { return; }
        throw new InvalidOperationException("The expected directory failure did not occur.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class PendingTransport(TaskCompletionSource entered, Action record) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            record();
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The directory request should have been canceled.");
        }
    }
}
