using System.Net;
using JoesScanner.Services;

internal static class AudioCacheTests
{
    private const string Archive = "https://recorder.test/archive";
    private static readonly Guid Recorder = Guid.Parse("5c69ace1-6942-4b93-bebb-a886558942c3");
    private static readonly CancellationToken Token = CancellationToken.None;

    internal static async Task RunAsync()
    {
        await ReusesMediaAndSidecarsAsync();
        await LimitsEvictionAndRecoveryAsync();
        await SharedRequestsAsync();
        await ClearingLeasesAndDownloadsAsync();
        await PressureAndFallbackAsync();
        await InvalidAndFailedDownloadsAsync();
    }

    private static async Task ReusesMediaAndSidecarsAsync()
    {
        using var fixture = new Fixture();
        var key = Key("recording-with-signed-url");
        var authorized = new List<bool>();
        var fetches = 0;
        Task Authorize(bool hit, CancellationToken _) { authorized.Add(hit); return Task.CompletedTask; }
        Task<string> Download(PlaybackAudioCache.Transfer transfer, CancellationToken token)
        {
            fetches++;
            return StoreAsync(transfer, token);
        }

        string path;
        using (var fresh = (await fixture.Cache.AcquireAsync(key, Authorize, Download, Token))!)
        {
            path = PathOf(fresh);
            Check(!fresh.CacheHit && fixture.Cache.ReadAnalysis(path) == null, "The initial playback must use a fresh cache lease.");
            var before = fixture.Cache.UsageBytes;
            Check(fixture.Cache.StoreAnalysis(path, "[1,2,3]") && fixture.Cache.UsageBytes == before + 7,
                "Analysis must use the same byte budget as its leased media.");
        }
        Check(fixture.Cache.ReadAnalysis(path) == null, "Analysis access requires a playback lease.");
        using var cached = (await fixture.Cache.AcquireAsync(key, Authorize, Download, Token))!;
        Check(cached.CacheHit && PathOf(cached) == path && fixture.Cache.ReadAnalysis(path) == "[1,2,3]" &&
            fetches == 1 && authorized.SequenceEqual([false, true]),
            "A new authorized playback must reuse the media and sidecar without a second download.");
        using var restarted = await fixture.Reload().AcquireAsync(key, Authorize, Download, Token);
        Check(restarted?.CacheHit == true && fetches == 1 && Path.GetFileNameWithoutExtension(path) == key.Value,
            "Persistent identities may expose only a recorder-scoped digest.");
        Console.WriteLine("PASS Audio cache reuses authorized media and coordinates analysis sidecars across playback and restart");
    }

    private static async Task LimitsEvictionAndRecoveryAsync()
    {
        using var fixture = new Fixture();
        var cache = fixture.Cache;
        var prior = AppPaths.DatabaseDirectory;
        try
        {
            AppPaths.DatabaseDirectory = fixture.Root;
            Check(PlaybackAudioCachePreferences.Read() == 100 &&
                PlaybackAudioCache.LimitsMiB.SequenceEqual([25, 50, 100, 250, 500, 1024]), "Cache limits must match the approved picker.");
            foreach (var limit in PlaybackAudioCache.LimitsMiB)
            {
                PlaybackAudioCachePreferences.Save(limit);
                Check(PlaybackAudioCachePreferences.Read() == limit, "Every supported size must persist.");
                await cache.SetLimitAsync(limit);
                await ExpectFailureAsync<RecordingAudioUnavailableException>(cache.AcquireAsync(Key("oversized"), Allowed,
                    async (transfer, token) =>
                    {
                        using var response = WaveResponse(128);
                        response.Content.Headers.ContentLength = limit * PlaybackAudioCache.MiB + 1;
                        return await transfer.StoreAsync(response, ".wav", token);
                    }, Token, speculative: true));
            }
            PlaybackAudioCachePreferences.Save(-1);
            Check(PlaybackAudioCachePreferences.Read() == 100, "Invalid saved sizes must use the default.");
        }
        finally { AppPaths.DatabaseDirectory = prior; }

        await cache.SetLimitAsync(25);
        string a, b, c;
        using (var lease = await fixture.GetAsync("a", 9 << 20)) a = PathOf(lease);
        using (var lease = await fixture.GetAsync("b", 9 << 20)) b = PathOf(lease);
        using (var lease = await fixture.GetAsync("a")) Check(lease.CacheHit, "Touch A to make it the newest entry.");
        cache = fixture.Reload();
        using (var lease = await fixture.GetAsync("c", 9 << 20)) c = PathOf(lease);
        Check(File.Exists(a) && File.Exists(c) && !File.Exists(b) && cache.UsageBytes <= 25 * PlaybackAudioCache.MiB,
            "Durable access order must evict B first under the 25 MB limit.");

        var orphan = Path.Combine(fixture.Media, Key("orphan").Value + ".mp3");
        var orphanAnalysis = orphan + ".analysis.json";
        var zip = Path.Combine(fixture.Media, Key("protected-user-export").Value + ".zip");
        var incoming = Path.Combine(fixture.Media, "incoming", Guid.NewGuid().ToString("N") + ".part");
        var oldDownload = Path.Combine(fixture.Legacy, "jaas_" + Guid.NewGuid().ToString("N") + ".wav.tmp");
        var userSaved = Path.Combine(fixture.Legacy, "saved-user-recording.wav");
        foreach (var path in new[] { orphan, orphanAnalysis, zip, incoming, oldDownload, userSaved })
            File.WriteAllText(path, "synthetic");
        File.Delete(c);
        await fixture.Reload().InitializeAsync();
        Check(!File.Exists(orphan) && !File.Exists(orphanAnalysis) && !File.Exists(incoming) &&
            !File.Exists(oldDownload) && File.Exists(zip) && File.Exists(userSaved) && File.Exists(a) &&
            !File.Exists(Path.ChangeExtension(c, ".entry")), "Recovery must clean recognized orphans and leave user files alone.");
        Console.WriteLine("PASS All six cache sizes persist, LRU survives restart, and recovery removes only managed orphan files");
    }

    private static async Task SharedRequestsAsync()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var started = Signal();
        var finish = Signal();
        Task<PlaybackAudioLease?> Request(CancellationToken token) => fixture.Cache.AcquireAsync(Key("concurrent"), Allowed,
            async (transfer, cancellation) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await finish.Task.WaitAsync(cancellation);
                return await StoreAsync(transfer, cancellation);
            }, token);

        var first = Request(Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        var abandoned = Request(cancel.Token);
        var second = Request(Token);
        cancel.Cancel();
        await ExpectFailureAsync<OperationCanceledException>(abandoned);
        finish.SetResult();
        using var one = await first;
        using var two = await second;
        Check(one?.CacheHit == false && two?.CacheHit == true && one.Url == two.Url && calls == 1,
            "Concurrent callers must share a single download and one waiter can retire independently.");
        Console.WriteLine("PASS Concurrent audio acquisition uses a single download and keeps independent waiter cancellation");
    }

    private static async Task ClearingLeasesAndDownloadsAsync()
    {
        using var fixture = new Fixture();
        var cached = await fixture.GetAsync("active");
        var path = PathOf(cached);
        using (var idle = await fixture.GetAsync("idle")) { }
        var began = Signal();
        var finish = Signal();
        var pending = fixture.Cache.AcquireAsync(Key("pending"), Allowed, async (transfer, token) =>
        {
            began.SetResult();
            await finish.Task.WaitAsync(token);
            return await StoreAsync(transfer, token);
        }, Token);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Cache.ClearAsync();
        Check(File.Exists(path) && fixture.Cache.UsageBytes > 0 && Directory.GetFiles(fixture.Media, "*.wav").Length == 1,
            "Clear must remove idle entries and leave active files available.");
        cached.Dispose();
        finish.SetResult();
        using (var completing = (await pending)!)
            Check(File.Exists(PathOf(completing)) && !File.Exists(path) && fixture.Cache.UsageBytes == 0,
                "An acquisition predating Clear may finish as transient playback without restoring the cache.");
        Check(Directory.GetFiles(fixture.Media, "*.wav").Length == 0, "Released files must be removed.");
        Console.WriteLine("PASS Clearing cache protects playing/preparing files and retires fills queued before the clear");
    }

    private static async Task PressureAndFallbackAsync()
    {
        long available = 120 * PlaybackAudioCache.MiB;
        using var fixture = new Fixture(_ => available);
        var active = await fixture.GetAsync("pressed");
        var path = PathOf(active);
        string idle;
        using (var clip = await fixture.GetAsync("idle")) idle = PathOf(clip);
        available = 4 * PlaybackAudioCache.MiB;
        Check(!await fixture.Cache.CanPreloadAsync(Token) && File.Exists(path) && !File.Exists(idle),
            "Low storage must suspend speculative work and evict idle files.");
        var called = false;
        using var speculative = await fixture.Cache.AcquireAsync(Key("other"), Allowed, (_, _) =>
        {
            called = true;
            throw new InvalidOperationException();
        }, Token, speculative: true);
        Check(speculative == null && !called, "Speculation must use no network under pressure.");
        string transient;
        using (var playback = await fixture.GetAsync("other"))
        {
            transient = PathOf(playback);
            Check(File.Exists(transient) && Directory.GetFiles(fixture.Media, "*.wav").Length == 1,
                "A foreground clip may use a transient file under low storage.");
        }
        Check(!File.Exists(transient), "Transient playback must retire after use.");
        active.Dispose();
        available = 120 * PlaybackAudioCache.MiB;
        Check(await fixture.Cache.CanPreloadAsync(Token), "Preloading must resume after storage recovers.");

        var blocked = Path.Combine(fixture.Root, "blocked");
        File.WriteAllText(blocked, "synthetic");
        var fallback = new PlaybackAudioCache(blocked, fixture.Fallback, 25, _ => null);
        using var lease = (await fallback.AcquireAsync(Key("read-only"), Allowed, (transfer, token) => StoreAsync(transfer, token), Token))!;
        Check(File.Exists(PathOf(lease)) && fallback.UsageBytes == 0 &&
            Path.GetDirectoryName(PathOf(lease)) == fixture.Fallback, "Playback must fall back when the cache directory cannot be written.");
        Console.WriteLine("PASS Low storage evicts idle files, suspends speculative work, and falls back safely for foreground playback");
    }

    private static async Task InvalidAndFailedDownloadsAsync()
    {
        using var fixture = new Fixture();
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[64], RecorderWave.Create(128)[..64] })
            await ExpectFailureAsync<RecordingAudioUnavailableException>(fixture.Cache.AcquireAsync(Key("bad"), Allowed,
                async (transfer, token) =>
                {
                    using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                    return await transfer.StoreAsync(response, ".wav", token);
                }, Token));
        await ExpectFailureAsync<HttpRequestException>(fixture.Cache.AcquireAsync(Key("bad"), Allowed,
            (_, _) => Task.FromException<string>(new HttpRequestException("Synthetic network failure.")), Token));
        Check(fixture.Cache.UsageBytes == 0 && Directory.GetFiles(Path.Combine(fixture.Media, "incoming")).Length == 0,
            "Failed or incomplete transfers must not publish entries or leave incomplete files.");
        string path;
        using (var lease = await fixture.GetAsync("bad")) path = PathOf(lease);
        File.WriteAllBytes(path, [0]);
        using var repaired = await fixture.GetAsync("bad");
        Check(!repaired.CacheHit && new FileInfo(PathOf(repaired)).Length == 256,
            "A corrupt cache file must be replaced by newly downloaded audio.");
        Console.WriteLine("PASS Empty, corrupt, incomplete, and failed media clean up and allow a fresh download");
    }

    internal static HttpResponseMessage WaveResponse(int length) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(RecorderWave.Create(length)) };

    private static async Task<string> StoreAsync(PlaybackAudioCache.Transfer transfer, CancellationToken token, int length = 256)
    {
        using var response = WaveResponse(length);
        return await transfer.StoreAsync(response, ".wav", token);
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Allowed(bool cached, CancellationToken token) => Task.CompletedTask;
    private static string PathOf(PlaybackAudioLease lease) => new Uri(lease.Url).LocalPath;
    private static RecordingCacheKey Key(string id) => RecordingCacheKey.Create(Recorder, Archive, id, null, null)!.Value;
    internal static void Check(bool condition, string message) => LiveHistoryHandoffTests.Check(condition, message);

    private static async Task ExpectFailureAsync<T>(Task<PlaybackAudioLease?> attempt) where T : Exception
    {
        try
        {
            using var unexpected = await attempt;
        }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("joesscanner-cache-");
        private readonly Func<string, long?> _free;
        internal string Root => _root.FullName;
        internal string Media => Path.Combine(Root, "media");
        internal string Fallback => Path.Combine(Root, "fallback");
        internal string Legacy => Path.Combine(Root, "legacy");
        internal PlaybackAudioCache Cache { get; private set; }

        internal Fixture(Func<string, long?>? free = null)
        {
            _free = free ?? (_ => null);
            Directory.CreateDirectory(Legacy);
            Cache = NewCache();
        }

        private PlaybackAudioCache NewCache() => new(Media, Fallback, 25, _free, Legacy);
        internal PlaybackAudioCache Reload() => Cache = NewCache();

        internal async Task<PlaybackAudioLease> GetAsync(string id, int size = 256) =>
            (await Cache.AcquireAsync(Key(id), Allowed, (transfer, token) => StoreAsync(transfer, token, size), Token))!;

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            _root.Delete(recursive: true);
        }
    }
}
