using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;

internal static class FavoritesTests
{
    private const string AccountBase = "https://account.test";
    private const string Password = "synthetic-test-password";
    private static readonly Uri Endpoint = SavedConnectionUrl.AccountEndpoint(AccountBase, "favorites/sync");
    private static readonly FavoritesOwner Alpha = new(Endpoint.AbsoluteUri, 101);
    private static readonly FavoritesOwner Beta = new(Endpoint.AbsoluteUri, 202);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static Task LocalMembership()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        settings.LocalOnlyMode = true;
        var db = new FavoritesRepository(fixture);
        var favorites = new FavoritesService(settings, db);
        var north = new SavedScannerConnection(Guid.NewGuid(), "https://north.test", "North", 129, true);
        var south = new SavedScannerConnection(Guid.NewGuid(), "https://south.test", "South", null, true);
        favorites.SetProfiles([north, south]);
        var northCall = Card(north.Id, "RX-1");
        var southCall = Card(south.Id, "RX-1");
        var anotherReceiver = Card(north.Id, "RX-2");
        var anotherSite = Card(north.Id, "RX-1", site: "20");
        var anotherSystem = Card(north.Id, "RX-1", system: "30");
        var cards = new ObservableCollection<CallItem>([northCall, southCall, anotherReceiver, anotherSite, anotherSystem]);
        var view = new LiveCallDisplay(cards, favorite: favorites.Contains);
        view.SetServers([north.Id, south.Id]);
        view.Observe();
        favorites.Changed += view.RefreshFavorites;

        var owner = favorites.OwnerKey;
        Check(owner == FavoritesRepository.Device && !settings.HasActiveHostedSubscription(), "Local Favorites must work signed out.");
        var group = FavoriteValue.Group("County agencies");
        favorites.SetValue(owner, group);
        favorites.SetCall(owner, northCall, true, group.Id);
        Check(view.Favorites.SequenceEqual([northCall]) && ReferenceEquals(view.Favorites[0], cards[0]),
            "A saved call must project the same card, scoped to the exact recorder, system, site and receiver.");
        anotherReceiver.CardMetadata = northCall.CardMetadata;
        Check(view.Favorites.SequenceEqual([northCall, anotherReceiver]), "Late call metadata must update the Favorites projection.");
        anotherReceiver.CardMetadata = anotherReceiver.CardMetadata! with { Receiver = "RX-2" };

        var scheduler = new LivePlaybackScheduler(favorites);
        var waiting = new[] { Candidate(1, southCall), Candidate(2, northCall) };
        Check(scheduler.Choose(waiting, LivePlaybackPreferences.Default)?.Candidate.Arrival == 2,
            "A local edit must change priority through the production scheduler immediately.");
        favorites.SetCall(owner, northCall, false);
        Check(scheduler.Choose(waiting, LivePlaybackPreferences.Default)?.Candidate.Arrival == 1 && view.Favorites.Count == 0,
            "Removing a Favorite must immediately restore the normal order.");
        favorites.SetCall(owner, northCall, true, group.Id);
        favorites.SetCall(owner, southCall, true);
        favorites.SetValue(owner, group with { Label = "County dispatch" });
        Check(new FavoritesRepository(fixture).Read(owner).Values.Any(value => value.Id == group.Id && value.Label == "County dispatch"),
            "Group renames and Favorites must survive reopening the database.");
        favorites.SetValue(owner, group, deleted: true);
        Check(favorites.Collection.Values.Length == 2, "Deleting a group must preserve its Favorites.");

        var previousCode = favorites.GetLinkCode(owner, south.Id);
        favorites.SetProfiles([north, south with { Endpoint = "https://different-address.test" }]);
        Check(favorites.Contains(southCall) && previousCode == favorites.GetLinkCode(owner, south.Id),
            "Recorder address changes must preserve the server ID and its custom association.");
        favorites.SetProfiles([north, south with { Enabled = false }]);
        view.SetServers([north.Id]);
        Check(!favorites.Contains(southCall) && view.Favorites.SequenceEqual([northCall]) && favorites.Collection.Values.Length == 2,
            "Disabled recorders must retain their Favorites and leave the active projection.");
        view.Detach();
        return Task.CompletedTask;
    }

    internal static async Task MatchingAcrossDevices()
    {
        using var first = new SavedCredentialTests.Fixture();
        using var second = new SavedCredentialTests.Fixture();
        var cloud = new EndpointFixture();
        using var firstHttp = cloud.Open();
        using var secondHttp = cloud.Open();
        var firstSettings = Settings(first);
        var secondSettings = Settings(second);
        var a = new FavoritesService(firstSettings, new FavoritesRepository(first), firstHttp);
        var b = new FavoritesService(secondSettings, new FavoritesRepository(second), secondHttp);
        await Login(a, "alpha");
        await Login(b, "alpha");
        Check(!firstSettings.HasActiveHostedSubscription() && !secondSettings.HasActiveHostedSubscription(),
            "Authentication for Favorites must not turn into a subscription entitlement.");

        var savedDirectory = new SavedScannerConnection(Guid.NewGuid(), "https://first-directory.test", "First name", 72, true);
        var custom = new SavedScannerConnection(Guid.NewGuid(), "https://same-address.test", "Same name", null, true);
        var directoryB = savedDirectory with { Id = Guid.NewGuid(), Endpoint = "https://new-address.test", Name = "Second name" };
        var customB = custom with { Id = Guid.NewGuid() };
        a.SetProfiles([savedDirectory, custom]);
        b.SetProfiles([directoryB, customB]);
        a.SetCall(a.OwnerKey, Card(savedDirectory.Id), true);
        a.SetCall(a.OwnerKey, Card(custom.Id), true);
        await a.SyncOnceAsync();
        await b.SyncOnceAsync();
        Check(b.Contains(Card(directoryB.Id)) && !b.Contains(Card(customB.Id)),
            "Use the directory ID across devices and never infer a custom match from a name or URL.");
        var code = a.GetLinkCode(a.OwnerKey, custom.Id)!;
        Check(b.Link(b.OwnerKey, customB.Id, code) && b.Contains(Card(customB.Id)), "Link codes must map a saved custom recorder explicitly.");
        var unrelated = customB with { Id = Guid.NewGuid(), Endpoint = "https://unrelated.test" };
        b.SetProfiles([directoryB, customB, unrelated]);
        Check(!b.Link(b.OwnerKey, unrelated.Id, code) && !b.Contains(Card(unrelated.Id)),
            "A link already assigned to one recorder cannot attach to another.");
    }

    internal static async Task OfflineConflictsAndTombstones()
    {
        using var first = new SavedCredentialTests.Fixture();
        using var second = new SavedCredentialTests.Fixture();
        using var third = new SavedCredentialTests.Fixture();
        var cloud = new EndpointFixture();
        using var http = cloud.Open();
        var sync = new FavoritesSyncClient(http);
        var a = new FavoritesRepository(first);
        var b = new FavoritesRepository(second);
        var value = Value();
        var group = FavoriteValue.Group("Dispatch");
        a.Set(Alpha.Key, value with { GroupId = group.Id });
        a.Set(Alpha.Key, group);
        cloud.Offline = true;
        await ExpectAsync<HttpRequestException>(() => Reconcile(sync, a));
        a = new FavoritesRepository(first);
        Check(a.Read(Alpha.Key) is { HasPending: true, Values.Length: 2 }, "Offline rows and queued edits must survive restart.");
        cloud.Offline = false;
        await Reconcile(sync, a);
        await Reconcile(sync, b);
        Check(b.Read(Alpha.Key).Values.Any(item => item.Id == value.Id && item.GroupId == group.Id),
            "The same account must receive both types of records from another device.");

        a.Set(Alpha.Key, group with { Label = "County" });
        b.Set(Alpha.Key, group with { Label = "Regional" });
        var separatelyAdded = FavoriteValue.Group("New on second device");
        b.Set(Alpha.Key, separatelyAdded);
        await Reconcile(sync, a);
        await Reconcile(sync, b);
        await Reconcile(sync, a);
        Check(a.Read(Alpha.Key).Values.Any(item => item.Id == separatelyAdded.Id)
            && b.Read(Alpha.Key).Conflicts.Single().Value.Label == "Regional"
            && b.Read(Alpha.Key).Values.Any(item => item.Id == group.Id && item.Label == "County"),
            "Disjoint edits must merge. A competing edit must remain reviewable rather than overwriting the cloud.");
        b.Resolve(Alpha.Key, group.Id, useLocal: true);
        await Reconcile(sync, b);
        Check(b.Read(Alpha.Key).Values.Any(item => item.Id == group.Id && item.Label == "Regional"),
            "An explicit conflict resolution should reapply against the current revision.");

        a.Set(Alpha.Key, value with { Label = "stale edit" });
        b.Set(Alpha.Key, value, deleted: true);
        await Reconcile(sync, b);
        await Reconcile(sync, a);
        var c = new FavoritesRepository(third);
        await Reconcile(sync, c);
        Check(a.Read(Alpha.Key).Conflicts.Single().Value.Label == "stale edit" &&
            !a.Read(Alpha.Key).Values.Any(item => item.Id == value.Id) &&
            !c.Read(Alpha.Key).Values.Any(item => item.Id == value.Id) && !b.Read(Alpha.Key).HasPending,
            "A tombstone must beat the stale edit and must not endlessly stage its own acknowledgement.");
        a.Resolve(Alpha.Key, value.Id, useLocal: false);
        await Reconcile(sync, a);
        Check(a.Read(Alpha.Key).Conflicts.Length == 0 &&
            cloud.Record(Alpha.UserId, value.Id) is { Deleted: true, Revision: 2 },
            "Keeping the cloud deletion must remain durable without another write.");
    }

    internal static async Task LostResponseAndNewerDraft()
    {
        using var first = new SavedCredentialTests.Fixture();
        var cloud = new EndpointFixture { DropNextReply = true };
        using var http = cloud.Open();
        var sync = new FavoritesSyncClient(http);
        var a = new FavoritesRepository(first);
        var group = FavoriteValue.Group("First");
        a.Set(Alpha.Key, group);
        await ExpectAsync<HttpRequestException>(() => Reconcile(sync, a));

        a = new FavoritesRepository(first);
        a.Set(Alpha.Key, group with { Label = "Second" });
        await Reconcile(sync, a);
        Check(a.Read(Alpha.Key).HasPending && a.Read(Alpha.Key).Values.Single().Label == "Second"
            && cloud.Record(Alpha.UserId, group.Id)?.Revision == 1,
            "Retry the identical outgoing mutation. A later draft must survive its acknowledgement.");
        await Reconcile(sync, a);
        Check(a.Read(Alpha.Key) is { HasPending: false, Conflicts.Length: 0 }
            && cloud.Record(Alpha.UserId, group.Id) is { Revision: 2, Value.Label: "Second" },
            "A newer local edit should follow its own successful predecessor without duplication.");
    }

    internal static async Task AccountIsolation()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var cloud = new EndpointFixture();
        using var http = cloud.Open();
        var settings = Settings(fixture);
        var db = new FavoritesRepository(fixture);
        var service = new FavoritesService(settings, db, http);
        var local = FavoriteValue.Group("Signed-out group");
        service.SetValue(service.OwnerKey, local);
        await Login(service, "alpha");
        Check(service.Collection.Values.Length == 0, "A sign-in cannot implicitly upload signed-out Favorites.");
        service.ImportDevice(service.OwnerKey);
        var pending = FavoriteValue.Group("Alpha only");
        service.SetValue(service.OwnerKey, pending);
        var staleOwner = service.OwnerKey;
        service.SignOut();
        Check(service.Collection.Values.SequenceEqual([local]), "Sign-out must restore the separate device collection.");
        await Login(service, "beta");
        service.SetValue(staleOwner, FavoriteValue.Group("Stale dialog"));
        Check(service.Collection.Values.Length == 0, "Another account must not see a previous account's cached groups or stale UI edits.");
        await service.SyncOnceAsync();
        Check(cloud.Record(Beta.UserId, pending.Id) == null, "The previous account's pending changes cannot upload to the new account.");
        await Login(service, "alpha");
        Check(service.Collection.Values.Length == 2 && service.Collection.HasPending, "An original account must regain its own edits.");
        await service.SyncOnceAsync();
        Check(cloud.Record(Alpha.UserId, pending.Id) is { Deleted: false }, "Only the original account may send the queued group.");

        // Reusing credentials from the installed account with a different expected
        // owner must fail before the fixture applies any write.
        var transport = new FavoritesSyncClient(http);
        var forged = new FavoriteChange(Guid.NewGuid().ToString("N"), 0, FavoriteValue.Group("forged"), false);
        await ExpectAsync<HttpRequestException>(() => transport.ExchangeAsync(Endpoint, "beta", Password,
            new(Alpha.UserId, [forged]), default), HttpStatusCode.Forbidden);
        await ExpectAsync<HttpRequestException>(() => transport.ExchangeAsync(Endpoint, "missing", Password,
            new(null, []), default), HttpStatusCode.Unauthorized);
        Check(cloud.Record(Alpha.UserId, forged.Value.Id) == null &&
            cloud.Record(Beta.UserId, forged.Value.Id) == null, "Invalid authentication or another owner cannot modify any data.");
    }

    internal static async Task PrivacyBlocksAndCancels()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        fixture.Seed("telemetry_enabled", "false");
        var settings = Settings(fixture);
        var policy = PrivacyNetworkPolicy.For(settings);
        var recorder = new Uri("https://independent.example:8443/recorder");
        var hosted = new Uri("https://app.joesscanner.com/calls");
        var accountAlias = new Uri(AccountBase);
        var optional = new Uri("https://updates.example");
        var seen = new List<Uri>();
        using var recorderClient = new HttpClient(policy.Wrap(new StubHandler(request =>
        {
            seen.Add(request.RequestUri!);
            return new(HttpStatusCode.OK);
        })));
        using var cloud = policy.CreateCloudClient(new StubHandler(request =>
        {
            seen.Add(request.RequestUri!);
            return new(HttpStatusCode.OK);
        }));
        Check(settings.LocalOnlyMode, "Migration of an explicit telemetry opt-out must enable Local Only Mode.");
        await ExpectAsync<HttpRequestException>(() => recorderClient.GetAsync(hosted));
        await ExpectAsync<HttpRequestException>(() => recorderClient.GetAsync(accountAlias));
        await ExpectAsync<HttpRequestException>(() => cloud.GetAsync(optional));
        foreach (var route in new[] { "auth", "ping", "app-event", "servers", "communications", "lookups", "favorites/sync", "firewall-credentials" })
            await ExpectAsync<HttpRequestException>(() => cloud.GetAsync(SavedConnectionUrl.AccountEndpoint(AccountBase, route)));
        using var independent = await recorderClient.GetAsync(recorder);
        Check(seen.SequenceEqual([recorder]), "An independent internet recorder must reach its transport in Local Only Mode.");

        using var redirected = ServerAuthenticationHandler.CreateClient(settings, new StubHandler(request =>
        {
            seen.Add(request.RequestUri!);
            return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://joesscanner.com/redirected") } };
        }), new(recorder));
        await ExpectAsync<HttpRequestException>(() => redirected.GetAsync(recorder));
        Check(seen.SequenceEqual([recorder, recorder]), "Third-party redirects must not bypass Local Only Mode.");

        settings.LocalOnlyMode = false;
        Check(!fixture.Load().LocalOnlyMode, "An explicit change back to Normal Mode must persist after migration.");
        using var bodyClient = policy.CreateCloudClient(new StubHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream([1, 2, 3]))
        }));
        using var received = await bodyClient.GetAsync(optional, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await received.Content.ReadAsStreamAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inFlight = policy.CreateCloudClient(new BlockingHandler(started));
        var request = inFlight.GetAsync(optional);
        await started.Task.WaitAsync(Timeout);
        settings.LocalOnlyMode = true;
        await ExpectAsync<OperationCanceledException>(() => request.WaitAsync(Timeout));
        await ExpectAsync<ObjectDisposedException>(async () => { await body.ReadExactlyAsync(new byte[1]); });
        using var again = await recorderClient.GetAsync(recorder);
        Check(seen[^1] == recorder, "Changing privacy mode must not cancel a third-party recorder.");
    }

    internal static async Task PrivacyKeepsOfflineChanges()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = Settings(fixture);
        var cloud = new EndpointFixture();
        using var http = PrivacyNetworkPolicy.For(settings).CreateCloudClient(cloud.Handler());
        var service = new FavoritesService(settings, new FavoritesRepository(fixture), http);
        await Login(service, "alpha");
        var atSignIn = cloud.Requests;
        settings.LocalOnlyMode = true;
        var group = FavoriteValue.Group("Saved offline");
        service.SetValue(service.OwnerKey, group);
        await service.SyncOnceAsync();
        Check(cloud.Requests == atSignIn && service.Collection.Values.SequenceEqual([group]) && service.Collection.HasPending,
            "Local Only Mode must leave local Favorites usable and the upload durable and unsent.");
        settings.LocalOnlyMode = false;
        await service.SyncOnceAsync();
        Check(!service.Collection.HasPending && cloud.Record(Alpha.UserId, group.Id) is { Value.Label: "Saved offline" },
            "Normal Mode must resume the same account's changes without losing the local copy.");
    }

    internal static async Task TransientForbiddenKeepsCache()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = Settings(fixture);
        var cloud = new EndpointFixture();
        using var http = cloud.Open();
        var service = new FavoritesService(settings, new FavoritesRepository(fixture), http);
        await Login(service, "alpha");
        var group = FavoriteValue.Group("Keep this");
        service.SetValue(service.OwnerKey, group);
        cloud.RejectThroughProxy = true;
        await ExpectAsync<HttpRequestException>(service.SyncOnceAsync, HttpStatusCode.Forbidden);
        Check(service.IsSignedIn && service.Collection.Values.SequenceEqual([group]) && service.Collection.HasPending,
            "A proxy or gateway denial must leave the verified cache and its pending edits usable.");
        cloud.RejectThroughProxy = false;
        await service.SyncOnceAsync();
        Check(!service.Collection.HasPending && cloud.Record(Alpha.UserId, group.Id) is { Value.Label: "Keep this" },
            "The same journal must recover when the service becomes available again.");
    }

    private static SettingsService Settings(SavedCredentialTests.Fixture fixture)
    {
        var settings = fixture.Load();
        settings.AuthServerBaseUrl = AccountBase;
        return settings;
    }

    private static async Task Login(FavoritesService service, string account)
    {
        var login = await service.VerifyLoginAsync(account, Password, CancellationToken.None).WaitAsync(Timeout);
        service.CompleteLogin(login, account, Password);
    }

    private static CallItem Card(Guid server, string receiver = "RX-1", string site = "10", string system = "11") => new()
    {
        ServerId = server, BackendId = "same-backend-id", CardMetadata = new CallCardMetadata
        {
            Talkgroup = "Fire Dispatch", TalkgroupId = "42", SystemId = system, SiteId = site, Receiver = receiver
        }
    };

    private static LivePlaybackScheduler.Candidate Candidate(long position, CallItem call) => new(position,
        TalkgroupKey.From(call.ServerId, call.CardMetadata), Identity: FavoriteTalkgroupIdentity.From(call.CardMetadata));

    private static FavoriteValue Value() => FavoriteValue.Talkgroup("d:72",
        FavoriteTalkgroupIdentity.From(Card(Guid.NewGuid()).CardMetadata)!, "Fire Dispatch");

    private static Task Reconcile(FavoritesSyncClient sync, FavoritesRepository db) =>
        sync.ReconcileAsync(db, Alpha, Endpoint, "alpha", Password, apply =>
        {
            apply();
            return true;
        }, CancellationToken.None);

    private static async Task ExpectAsync<T>(Func<Task> action, HttpStatusCode? status = null) where T : Exception
    {
        try { await action(); }
        catch (T ex) when (status == null || ex is HttpRequestException http && http.StatusCode == status) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " from the fixture.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class BlockingHandler(TaskCompletionSource started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The fixture should have been canceled.");
        }
    }

    // Controlled HTTP contract fixture. No WordPress service, network, scanner
    // credentials, entitlement, or production database is used by these cases.
    private sealed class EndpointFixture
    {
        private readonly Dictionary<(long, string), FavoriteRecord> _records = [];
        internal bool Offline;
        internal bool DropNextReply;
        internal bool RejectThroughProxy;
        internal int Requests;

        internal HttpClient Open() => new(Handler());
        internal HttpMessageHandler Handler() => new HandlerImpl(this);
        internal FavoriteRecord? Record(long owner, string id) => _records.GetValueOrDefault((owner, id));

        private sealed class HandlerImpl(EndpointFixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
            {
                var request = await message.Content!.ReadFromJsonAsync<FavoriteSyncRequest>(FavoritesJson.Options, token);
                lock (fixture._records)
                {
                    fixture.Requests++;
                    if (fixture.RejectThroughProxy)
                        return new(HttpStatusCode.Forbidden) { Content = new StringContent("Proxy unavailable") };
                    if (fixture.Offline)
                        throw new HttpRequestException("Fixture network unavailable.");
                    Check(message.RequestUri == Endpoint && message.Method == HttpMethod.Post, "Unexpected fixture endpoint.");
                    if (message.Headers.Authorization is not { Scheme: "JoesScannerAccount", Parameter: { } parameter })
                        return new(HttpStatusCode.Unauthorized);
                    var owner = Encoding.UTF8.GetString(Convert.FromBase64String(parameter)) switch
                    {
                        "alpha:" + Password => Alpha.UserId,
                        "beta:" + Password => Beta.UserId,
                        _ => 0
                    };
                    if (owner == 0)
                        return new(HttpStatusCode.Unauthorized);
                    if (request?.OwnerId is { } expected && expected != owner || request?.OwnerId == null && request!.Changes.Length > 0)
                        return new(HttpStatusCode.Forbidden);
                    var results = new List<FavoriteSyncResult>();
                    foreach (var change in request!.Changes)
                    {
                        var key = (owner, change.Value.Id);
                        var current = fixture._records.GetValueOrDefault(key);
                        if (current?.MutationId != change.MutationId && (current?.Revision ?? 0) == change.ExpectedRevision)
                        {
                            current = new(change.ExpectedRevision + 1, change.MutationId,
                                change.Deleted ? new FavoriteValue(change.Value.Id) : change.Value, change.Deleted);
                            fixture._records[key] = current;
                        }
                        results.Add(new(change.Value.Id, change.MutationId, current?.MutationId == change.MutationId, current));
                    }
                    if (fixture.DropNextReply)
                    {
                        fixture.DropNextReply = false;
                        throw new HttpRequestException("Simulated response loss after accepted writes.");
                    }

                    var page = fixture._records.Where(pair => pair.Key.Item1 == owner && string.CompareOrdinal(pair.Key.Item2, request.After) > 0)
                        .OrderBy(pair => pair.Key.Item2, StringComparer.Ordinal).Select(pair => pair.Value).Take(201).ToArray();
                    var next = page.Length == 201 ? page[199].Value.Id : null;
                    return new(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new FavoriteSyncResponse(owner, results.ToArray(), page.Take(200).ToArray(), next),
                            options: FavoritesJson.Options)
                    };
                }
            }
        }
    }
}
