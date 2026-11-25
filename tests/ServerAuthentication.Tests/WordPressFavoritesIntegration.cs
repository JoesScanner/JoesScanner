using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using JoesScanner.Services;

internal static class WordPressFavoritesIntegration
{
    private sealed record Account(string Username, string Email, string Password);
    private sealed record Fixture(string AccountUrl, string CaPath, string RecorderUrl, Account Alpha, Account Beta);

    internal static async Task RunAsync(string fixturePath)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath), FavoritesJson.Options) ??
            throw new InvalidDataException("The integration runner requires a generated fixture.");
        var accountUrl = Loopback(fixture.AccountUrl, Uri.UriSchemeHttps);
        var recorder = Loopback(fixture.RecorderUrl, Uri.UriSchemeHttp);
        using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(fixture.CaPath));
        using var a = new Device(accountUrl, ca);
        using var b = new Device(accountUrl, ca);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;

        var alpha = await a.LoginAsync(fixture.Alpha.Username, fixture.Alpha.Password, token);
        var group = FavoriteValue.Group("Integration dispatch");
        var favorite = FavoriteValue.Talkgroup("d:977", new("id:SYS", "id:SITE", "label:RX", "id:" + Guid.NewGuid().ToString("N")),
            "Integration dispatch 42", group.Id);
        a.Service.SetValue(a.Service.OwnerKey, group);
        a.Service.SetValue(a.Service.OwnerKey, favorite);
        await a.Service.SyncOnceAsync().WaitAsync(token);

        Check(alpha == await b.LoginAsync(fixture.Alpha.Email, fixture.Alpha.Password, token), "Username and email have different owners.");
        await b.Service.SyncOnceAsync().WaitAsync(token);
        Check(b.Value(group.Id) == group && b.Value(favorite.Id) == favorite &&
            !a.Settings.HasActiveHostedSubscription() && !b.Settings.HasActiveHostedSubscription(),
            "A free account's Favorite and named group must reach a second database.");

        var deviceEdit = favorite with { Label = "Edited on device B" };
        b.Service.SetValue(b.Service.OwnerKey, deviceEdit);
        await b.Service.SyncOnceAsync().WaitAsync(token);
        await a.Service.SyncOnceAsync().WaitAsync(token);
        Check(a.Value(favorite.Id) == deviceEdit, "Device A did not receive device B's edit.");
        Console.WriteLine("PASS Real WordPress sync shares Favorites, groups, and edits across two independent databases");

        a.Wire.Offline = b.Wire.Offline = true;
        var winning = group with { Label = "Device A offline edit" };
        var conflicting = group with { Label = "Device B offline edit" };
        a.Service.SetValue(a.Service.OwnerKey, winning);
        b.Service.SetValue(b.Service.OwnerKey, conflicting);
        await ExpectAsync<HttpRequestException>(() => a.Service.SyncOnceAsync().WaitAsync(token));
        await ExpectAsync<HttpRequestException>(() => b.Service.SyncOnceAsync().WaitAsync(token));
        Check(a.Service.Collection.HasPending && b.Service.Collection.HasPending, "Offline writes were lost.");
        a.Wire.Offline = b.Wire.Offline = false;
        await a.Service.SyncOnceAsync().WaitAsync(token);
        await b.Service.SyncOnceAsync().WaitAsync(token);
        Check(b.Value(group.Id) == winning && b.Service.Collection.Conflicts.Single(item => item.Value.Id == group.Id).Value == conflicting,
            "The competing offline edit must remain available for explicit resolution.");
        b.Service.Resolve(b.Service.OwnerKey, group.Id, useLocal: true);
        await b.Service.SyncOnceAsync().WaitAsync(token);
        await a.Service.SyncOnceAsync().WaitAsync(token);
        Check(a.Value(group.Id) == conflicting && b.Service.Collection.Conflicts.Length == 0,
            "Explicit resolution must reconcile against the current revision.");

        a.Service.SetValue(a.Service.OwnerKey, deviceEdit with { Label = "Stale edit" });
        b.Service.SetValue(b.Service.OwnerKey, deviceEdit, deleted: true);
        await b.Service.SyncOnceAsync().WaitAsync(token);
        await a.Service.SyncOnceAsync().WaitAsync(token);
        Check(a.Value(favorite.Id) == null && b.Value(favorite.Id) == null &&
            a.Service.Collection.Conflicts.Single(item => item.Value.Id == favorite.Id).Value.Label == "Stale edit",
            "A stale device must preserve its conflict without resurrecting the deletion.");
        a.Service.Resolve(a.Service.OwnerKey, favorite.Id, useLocal: false);
        await a.Service.SyncOnceAsync().WaitAsync(token);
        Console.WriteLine("PASS Real WordPress conflicts preserve both offline edits and stale-device deletions");

        var pending = FavoriteValue.Group("Private pending edit");
        b.Service.SetValue(b.Service.OwnerKey, pending);
        b.Service.SignOut();
        Check(b.Value(group.Id) == null && b.Value(pending.Id) == null, "Sign-out exposed an account snapshot.");
        var beta = await b.LoginAsync(fixture.Beta.Username, fixture.Beta.Password, token);
        await b.Service.SyncOnceAsync().WaitAsync(token);
        Check(beta != alpha && b.Value(group.Id) == null && b.Value(pending.Id) == null,
            "Account switching exposed or uploaded the previous account's Favorites.");
        await a.Service.SyncOnceAsync().WaitAsync(token);
        Check(a.Value(pending.Id) == null, "The account switch submitted a previously pending change.");

        await VerifyPrivacyAsync(a, b, fixture.Alpha, group.Id, recorder, token);
        var secrets = new[] { fixture.Alpha.Password, fixture.Beta.Password,
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(fixture.Alpha.Username + ":" + fixture.Alpha.Password)) };
        a.CheckStorage(secrets);
        b.CheckStorage(secrets);
        Console.WriteLine("PASS Account switches, Local Only request blocking, Normal Mode reconciliation, and credential isolation");

        var directory = new ServerDirectoryClient(a.Settings, a.Wire);
        var operation = directory.Begin();
        var site = await directory.GetSiteAsync(operation, token);
        const string pendingAddress = "https://dotnet-directory-fixture.example.net/recorder";
        Check(site.MyServers != null && await directory.SubmitAsync(operation, site,
            new("Managed client submission", pendingAddress, "Boise, ID", true, false), token) == DirectorySubmissionStatus.Pending,
            "The managed client must submit as its verified free account.");
        var directorySnapshot = await directory.FetchAsync(operation, token);
        Check(directorySnapshot.Suspended?.Contains(21) == true &&
            directorySnapshot.Entries.All(entry => entry.Url != pendingAddress), "The managed client must separate pending and suspended listings.");
        Console.WriteLine("PASS The managed directory client discovers My Servers and submits a pending listing against real WordPress");
    }

    private static async Task VerifyPrivacyAsync(Device a, Device b, Account alpha, string groupId, Uri recorder,
        CancellationToken token)
    {
        var policy = PrivacyNetworkPolicy.For(b.Settings);
        var before = b.Wire.Sent;
        b.Settings.LocalOnlyMode = true;
        var localEdit = FavoriteValue.Group("Saved in Local Only Mode");
        b.Service.SetValue(b.Service.OwnerKey, localEdit);
        await b.Service.SyncOnceAsync().WaitAsync(token);
        await ExpectAsync<HttpRequestException>(() => b.Service.VerifyLoginAsync(alpha.Email, alpha.Password, token));
        foreach (var route in new[] { "auth", "ping", "app-event", "servers", "communications", "lookups", "favorites/sync", "firewall-credentials" })
        {
            var uri = WordPressSession.Capture(b.Settings).Endpoint(route);
            await ExpectAsync<HttpRequestException>(() => b.Cloud.GetAsync(uri, token));
        }
        Check(b.Wire.Sent == before && b.Service.Collection.HasPending && b.Value(localEdit.Id) == localEdit,
            "Joe-managed requests must stop at the privacy policy while local edits stay available.");

        using var signedOut = new SavedCredentialTests.Fixture();
        var independentSettings = signedOut.Load();
        independentSettings.AuthServerBaseUrl = a.Settings.AuthServerBaseUrl;
        independentSettings.LocalOnlyMode = true;
        using var independent = ServerAuthenticationHandler.CreateClient(independentSettings,
            new LoopbackTransport(recorder, new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }),
            new(recorder, Guid.NewGuid()));
        using var response = await independent.GetAsync(new Uri(recorder, "calls"), token);
        Check(response.IsSuccessStatusCode &&
            await response.Content.ReadAsStringAsync(token) == "Synthetic recorder call",
            "A signed-out independent recorder must remain reachable in Local Only Mode.");

        b.Settings.LocalOnlyMode = false;
        await b.Service.SyncOnceAsync().WaitAsync(token);
        Check(b.Value(localEdit.Id) == localEdit && !b.Service.Collection.HasPending && b.Wire.Sent > before,
            "Returning to Normal Mode must reconcile the pending edit.");
        await a.Service.SyncOnceAsync().WaitAsync(token);
        Check(a.Value(localEdit.Id) == null && a.Value(groupId) != null,
            "Normal Mode reconciliation must not upload a pending edit into another account.");
    }

    private sealed class Device : IDisposable
    {
        private readonly SavedCredentialTests.Fixture _storage = new();
        internal SettingsService Settings { get; }
        internal LoopbackTransport Wire { get; }
        internal HttpClient Cloud { get; }
        internal FavoritesService Service { get; }

        internal Device(Uri accountUrl, X509Certificate2 ca)
        {
            Settings = _storage.Load();
            Settings.AuthServerBaseUrl = accountUrl.AbsoluteUri;
            Settings.LocalOnlyMode = false;
            var trust = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                CustomTrustStore = { ca }
            };
            Wire = new(accountUrl, new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                SslOptions = { CertificateChainPolicy = trust }
            });
            Cloud = PrivacyNetworkPolicy.For(Settings).CreateCloudClient(Wire);
            Service = new FavoritesService(Settings, new FavoritesRepository(_storage), Cloud);
        }

        internal FavoriteValue? Value(string id) => Service.Collection.Values.SingleOrDefault(value => value.Id == id);

        internal async Task<long> LoginAsync(string login, string password, CancellationToken token)
        {
            var verified = await Service.VerifyLoginAsync(login, password, token);
            Service.CompleteLogin(verified, login, password);
            Check(Settings.VerifiedFavoritesUserId == verified.FirstPage.UserId,
                "The client must store the authenticated WordPress ID.");
            return verified.FirstPage.UserId;
        }

        internal void CheckStorage(string[] secrets)
        {
            var favoritesPath = Path.Combine(Path.GetDirectoryName(_storage.DbPath)!, "favorites.db");
            var persisted = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(favoritesPath));
            Check(!_storage.HasPlaintextCredentialRows() && secrets.All(secret => !persisted.Contains(secret, StringComparison.Ordinal)),
                "Favorites data and ordinary settings must not contain account secrets or encoded authorization.");
        }

        public void Dispose()
        {
            Cloud.Dispose();
            _storage.Dispose();
        }
    }

    private sealed class LoopbackTransport(Uri origin, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        internal bool Offline { get; set; }
        private int _sent;
        internal int Sent => Volatile.Read(ref _sent);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri is not { } uri || uri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority))
                throw new InvalidOperationException("Unexpected request origin in the disposable integration fixture.");
            if (Offline)
                throw new HttpRequestException("The test device is offline.");
            Interlocked.Increment(ref _sent);
            return base.SendAsync(request, token);
        }
    }

    private static Uri Loopback(string address, string scheme)
    {
        var uri = new Uri(address, UriKind.Absolute);
        if (!uri.IsLoopback || uri.Scheme != scheme || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new InvalidDataException("The WordPress integration runner only accepts disposable loopback origins.");
        return uri;
    }

    private static async Task ExpectAsync<T>(Func<Task> call) where T : Exception
    {
        try { await call(); }
        catch (T) { return; }
        throw new InvalidOperationException("No expected failure was reported by the integration runner.");
    }

    private static void Check(bool valid, string reason)
    {
        if (!valid)
            throw new InvalidOperationException(reason);
    }
}
