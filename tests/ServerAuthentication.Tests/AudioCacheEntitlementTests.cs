using System.Net;
using JoesScanner.Helpers;
using JoesScanner.Services;

internal static class AudioCacheEntitlementTests
{
    private const string Hosted = HostedServerRules.DefaultServerUrl;
    private const string Independent = "https://independent.test/archive";
    private static readonly Guid Recorder = Guid.Parse("f29aab15-07fe-4308-b589-4037939e0b98");
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 19, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedTime(Now);

    internal static async Task IdentityAsync()
    {
        using var settingsFixture = new SavedCredentialTests.Fixture();
        var settings = settingsFixture.Load();
        settings.SetRecorderCredentials(Recorder, Independent, "User", "fixture-password");
        var accountOne = RecordingPlaybackAccess.RecorderAccountKey(settings, Recorder, Independent);
        settings.SetRecorderCredentials(Recorder, Independent, "user", "fixture-password");
        var accountTwo = RecordingPlaybackAccess.RecorderAccountKey(settings, Recorder, Independent);
        using var fixture = new CacheFixture();
        var variants = new[]
        {
            Key(Recorder, Independent, accountOne),
            Key(Recorder, Independent, accountTwo),
            Key(Guid.NewGuid(), Independent, accountOne),
            Key(Recorder, Independent + "/different", accountOne),
            Key(Recorder, Independent, accountOne, "https://auth-other.test")
        };
        Check(accountOne != null && accountTwo != null && variants.Distinct().Count() == variants.Length,
            "A case-sensitive recorder login, archive edit, or account authority must not collide.");
        using var http = new HttpClient(new StubHandler(request =>
        {
            Check(request.RequestUri?.UserInfo.Length == 0 && request.RequestUri.Query.Contains("fixture-signature"),
                "URL credentials must be dropped without breaking the signed request.");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent("ID3fixture-mp3-bytes"u8.ToArray()) };
        }));
        foreach (var key in variants)
        {
            using var lease = await fixture.Cache.AcquireAsync(key, (_, _) => Task.CompletedTask,
                async (transfer, token) => (await RecordingAudioRecovery.DownloadAsync(http,
                    "https://fixture-user:fixture-password@independent.test/secret-name.mp3?signature=fixture-signature",
                    transfer, token))!, default);
            Check(lease?.CacheHit == false, "Each distinct cache scope must download its own media.");
        }
        var filenames = Directory.GetFiles(fixture.Media).Select(Path.GetFileName).ToArray();
        Check(filenames.Length == variants.Length * 2 &&
            filenames.All(name => name!.Split('.')[0] is { Length: 64 } digest && digest.All(char.IsAsciiHexDigit)) &&
            Directory.GetFiles(fixture.Media, "*.entry").All(marker => File.ReadAllText(marker).All(c => char.IsAsciiDigit(c) || c == ' ')),
            "Disk bookkeeping must contain only digest filenames, lengths and timestamps.");
    }

    internal static async Task AuthorizationAsync()
    {
        using var settingsFixture = new SavedCredentialTests.Fixture();
        var settings = settingsFixture.Load();
        settings.ServerUrl = Hosted;
        settings.SetServerCredentials(Hosted, "cache-customer", "fixture-password");
        var account = RecordingPlaybackAccess.AccountKey(settings, Hosted, independent: true);
        var hosted = Key(Recorder, Hosted, account, settings.ReadAccountSession().BaseUrl);
        var independent = Key(Guid.NewGuid(), Independent, null);
        using var fixture = new CacheFixture();
        var checkedAt = Now.UtcDateTime.AddHours(-2);
        var grant = new HostedSubscriptionSnapshot(true, true, 1, checkedAt, Now.UtcDateTime.AddHours(2),
            null, "Standard", "", "Verified");
        settings.SaveHostedSubscription(grant);
        var downloads = 0;
        Task<PlaybackAudioLease?> Open(RecordingCacheKey key, string endpoint, DateTimeOffset recordedAt) =>
            fixture.Cache.AcquireAsync(key, (hit, _) =>
            {
                if (hit && endpoint == Hosted)
                    RecordingPlaybackAccess.RequireCachedEntitlement(settings, endpoint, account, recordedAt,
                        currentLive: false, independent: true, Clock);
                if (!hit)
                    RecordingPlaybackAccess.RequireRecorderNetwork(settings, endpoint);
                return Task.CompletedTask;
            }, async (transfer, token) =>
            {
                ++downloads;
                using var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("ID3fixture-mp3-bytes"u8.ToArray())
                };
                return await transfer.StoreAsync(response, ".mp3", token);
            }, default);

        using (var lease = await Open(hosted, Hosted, Now.AddHours(-1))) Check(lease?.CacheHit == false, "Expected a fresh download.");
        settings.LocalOnlyMode = true;
        using (var lease = await Open(hosted, Hosted, Now.AddHours(-1)))
            Check(lease?.CacheHit == true && downloads == 1, "An authorized recent cached clip may play without contacting any service.");
        await DeniedAsync(Open(hosted, Hosted, Now.AddHours(-25)));
        await DeniedAsync(Open(Key(Recorder, Hosted, account, "another-authority"), Hosted, Now));
        using (var lease = await Open(independent, Independent, Now.AddDays(-6)))
            Check(lease?.CacheHit == false && downloads == 2, "Independent recorder misses must still download in Local Only Mode.");

        settings.SaveHostedSubscription(grant with { VerifiedOnline = false, Tier = 2 });
        using (var lease = await Open(hosted, Hosted, Now.AddHours(-25)))
            Check(lease?.CacheHit == true, "Premium may play older cached History under a verified outage grant.");
        settings.SaveHostedSubscription(grant with { CheckedUtc = Now.UtcDateTime.AddHours(-25) });
        await DeniedAsync(Open(hosted, Hosted, Now));
        settings.SaveHostedSubscription(grant with { ExpiresUtc = Now.UtcDateTime.AddMinutes(-1) });
        await DeniedAsync(Open(hosted, Hosted, Now));
        settings.SaveHostedSubscription(grant with { IsAllowed = false });
        await DeniedAsync(Open(hosted, Hosted, Now));
        settings.ClearServerCredentials(Hosted);
        await DeniedAsync(Open(hosted, Hosted, Now));
        Check(downloads == 2, "Authorization denial must never fall through to cache playback or network refresh.");
    }

    private static RecordingCacheKey Key(Guid id, string endpoint, string? account, string? authority = null) =>
        RecordingCacheKey.Create(id, endpoint, "shared-recording-id", account, authority)!.Value;

    private static async Task DeniedAsync(Task<PlaybackAudioLease?> attempt)
    {
        try { using var unexpected = await attempt; }
        catch (RecordingAccessException) { return; }
        throw new InvalidOperationException("The protected cache acquisition should have been denied.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CacheFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("joesscanner-auth-cache-");
        internal string Media => Path.Combine(_root.FullName, "cache");
        internal PlaybackAudioCache Cache => _cache ??= new(Media, Path.Combine(_root.FullName, "fallback"), 25, _ => null);
        private PlaybackAudioCache? _cache;
        public void Dispose() => _root.Delete(recursive: true);
    }
}
