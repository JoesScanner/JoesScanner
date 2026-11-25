using System.Collections.Concurrent;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Data.Sqlite;

internal static class AudioPreloadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Guid A = Guid.Parse("cfe5993b-0296-4999-888d-aebfe87d1c52");
    private static readonly Guid B = Guid.Parse("2ecfe0e7-7012-4624-8351-e7b2cbf6eb62");

    internal static async Task RunAsync()
    {
        await LimitsAndWakeAsync();
        var directory = Directory.CreateTempSubdirectory("joesscanner-preloads-");
        var prior = AppPaths.DatabaseDirectory;
        try
        {
            AppPaths.DatabaseDirectory = directory.FullName;
            await VerifyRetirementAsync(Path.Combine(directory.FullName, "source.db"), "Source");
            await VerifyRetirementAsync(Path.Combine(directory.FullName, "stop.db"), "Stop");
            await VerifyRetirementAsync(Path.Combine(directory.FullName, "history.db"), "History");
            await VerifyRetirementAsync(Path.Combine(directory.FullName, "disabled.db"), "Audio Off");
        }
        finally
        {
            AppPaths.DatabaseDirectory = prior;
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task LimitsAndWakeAsync()
    {
        var preloader = new LiveAudioPreloader();
        var empty = AudioCacheTests.Signal();
        var both = AudioCacheTests.Signal();
        var finish = AudioCacheTests.Signal();
        var started = new ConcurrentQueue<long>();
        var ready = 0;
        var pending = 0;
        var worker = preloader.Start(_ =>
        {
            if (Volatile.Read(ref ready) == 0)
            {
                empty.TrySetResult();
                return Task.FromResult<IReadOnlyList<LiveAudioPreloader.Candidate>>([]);
            }
            return Task.FromResult<IReadOnlyList<LiveAudioPreloader.Candidate>>([Load(1), Load(2), Load(3)]);
        }, default);
        await empty.Task.WaitAsync(Timeout);
        Volatile.Write(ref ready, 1);
        preloader.Wake();
        await both.Task.WaitAsync(Timeout);
        finish.SetResult();
        await worker.WaitAsync(Timeout);
        AudioCacheTests.Check(started.SequenceEqual([1L, 2L]), "Only two eligible recordings may be speculated, even after a failed preload.");
        Console.WriteLine("PASS Preloading wakes for later arrivals, caps speculative work at two and absorbs download failures");

        LiveAudioPreloader.Candidate Load(long id) => new(id, async token =>
        {
            started.Enqueue(id);
            if (Interlocked.Increment(ref pending) == 2)
                both.SetResult();
            await finish.Task.WaitAsync(token);
            if (id == 1)
                throw new IOException("Synthetic speculative failure.");
        });
    }

    private static async Task VerifyRetirementAsync(string path, string transition)
    {
        var ownership = new PlaybackOwnership();
        var player = new HandoffAudio();
        var live = new MainViewModel(ownership, player, path);
        live.ConfigureAudioSourceRecordersForTest(A, B);
        live.PlaybackOptionsForTest = new(GroupCalls: false, SourceMode: LiveAudioSourceMode.SelectedOnly);
        await live.StartForTestAsync();
        var began = AudioCacheTests.Signal();
        var retired = AudioCacheTests.Signal();
        var nativeEnd = AudioCacheTests.Signal();
        var forecastCount = 0;
        player.Playing = async (id, token) =>
        {
            if (id == "active")
                await nativeEnd.Task.WaitAsync(token);
        };
        live.ForecastForTest = _ =>
        {
            Interlocked.Increment(ref forecastCount);
            return Task.FromResult<IReadOnlyList<LiveAudioPreloader.Candidate>>([new(2, async token =>
            {
                try
                {
                    began.TrySetResult();
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                }
                finally { retired.TrySetResult(); }
            })]);
        };
        await live.AddForTestAsync("active", serverId: A);
        var queue = live.StartQueueForTestAsync();
        await began.Task.WaitAsync(Timeout);
        switch (transition)
        {
            case "Source":
                live.ChooseLiveAudioSource(LiveAudioSource.Recorder(B));
                await retired.Task.WaitAsync(Timeout);
                AudioCacheTests.Check(!player.Completed.Contains("active") && player.Started.SequenceEqual(["active"]),
                    "A source switch must retire preloads while the native clip finishes normally.");
                nativeEnd.SetResult();
                await live.WaitForSourceForTestAsync().WaitAsync(Timeout);
                break;
            case "Stop":
                await live.StopForTestAsync();
                break;
            case "History":
                var history = new HistoryViewModel(ownership, player);
                history.OpenForTest();
                await retired.Task.WaitAsync(Timeout);
                await history.OnPageClosedAsync();
                break;
            case "Audio Off":
                await live.SetAudioForTestAsync(false);
                await retired.Task.WaitAsync(Timeout);
                await live.AddForTestAsync("manual", serverId: A);
                await live.SelectForTestAsync("manual").WaitAsync(Timeout);
                AudioCacheTests.Check(player.Completed.Contains("manual"),
                    "Manual playback must still be available with Audio Off.");
                break;
        }
        await retired.Task.WaitAsync(Timeout);
        await queue.WaitAsync(Timeout);
        await live.StopForTestAsync();
        AudioCacheTests.Check(forecastCount == 1, $"A retired {transition} prediction must not start another batch.");
        Console.WriteLine($"PASS {transition} retires speculative Live downloads at the playback boundary");
    }
}
