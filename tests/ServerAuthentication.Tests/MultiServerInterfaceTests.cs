using System.Collections.ObjectModel;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;

internal static partial class MobileRecoveryTests
{
    internal static Task SavedConnectionEditing()
    {
        using var database = new IngestionDatabase();
        var paths = new ArrivalTestPaths(database.DbPath);
        var store = new ScannerConnectionStore(paths);
        var listed = store.Load(RecorderA, 7, "Listed recorder").Single();
        var second = store.Save(RecorderB, "Recorder B", null, null);
        var third = store.Save("https://third-recorder.test", "Recorder C", null, null);
        store.SetEnabled(second.Id, false);
        store.Move(third.Id, -1);
        store.Move(third.Id, -1);

        const string editedEndpoint = "https://edited-recorder.test/new";
        var edited = store.Save(editedEndpoint, "B renamed", null, second.Id);
        var reloaded = new ScannerConnectionStore(paths).Load(RecorderA);
        Check(reloaded.Select(row => row.Id).SequenceEqual(new[] { third.Id, listed.Id, second.Id }) &&
            reloaded[1].DirectoryId == 7 && reloaded[1].Managed &&
            edited.Id == second.Id && edited.FilterKey == second.FilterKey && !edited.Enabled &&
            reloaded.Last().Name == "B renamed" && reloaded.Last().Endpoint == editedEndpoint,
            "Names, order, identity, managed directory metadata and enablement must reload together.");
        Check(store.CredentialOrigins(edited).ToHashSet().SetEquals([RecorderB, "https://edited-recorder.test"]),
            "Removing an edited profile must be able to delete credentials at both saved origins.");

        try
        {
            store.Save("https://changed.test", listed.Name, listed.DirectoryId, listed.Id);
            throw new Exception("A managed endpoint must not be editable.");
        }
        catch (InvalidOperationException)
        {
            Check(store.Load(RecorderA)[1].Endpoint == RecorderA, "A rejected edit must preserve the managed address.");
        }

        store.Remove(listed.Id);
        Check(store.Load(RecorderA).SequenceEqual(new[] { third, edited }),
            "Removing one server must preserve the other saved profiles and their order.");
        store.Remove(third.Id);
        store.Remove(edited.Id);
        Check(store.Load(RecorderA).Count == 0, "Removing the last server must not reimport a retired legacy selection.");
        return Task.CompletedTask;
    }

    internal static async Task ReorderKeepsTransport()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        var clock = new ConnectionStatusClock();
        var manager = new ServerConnectionManager(clock);
        var a = new SavedScannerConnection(Guid.NewGuid(), RecorderA, "A", null, true);
        var b = new SavedScannerConnection(Guid.NewGuid(), RecorderB, "B", null, true);
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;

        async Task Monitor(ServerConnectionManager.Connection connection)
        {
            Interlocked.Increment(ref starts);
            manager.Report(connection, ServerConnectionStatus.Monitoring);
            (connection.Profile.Id == a.Id ? startedA : startedB).SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, connection.Token);
        }

        manager.Synchronize([a, b], token, Monitor);
        await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(token);
        var originals = manager.Snapshot().ToDictionary(connection => connection.Profile.Id);
        manager.Synchronize([b, a], token, Monitor);
        Check(manager.Snapshot().All(connection => ReferenceEquals(originals[connection.Profile.Id], connection)) && starts == 2,
            "An order-only change must retain both sockets and their generation.");

        var first = originals[a.Id];
        var second = originals[b.Id];
        manager.Report(first, ServerConnectionStatus.Reconnecting);
        Check(ServerConnectionPresentation.Color(first.Status) == "#daa520" &&
            ServerConnectionPresentation.Text(second.Status) == "Connected" &&
            ServerConnectionPresentation.Color(second.Status) == "#15803d",
            "One degraded server must stay yellow while another stays green.");
        clock.Advance(TimeSpan.FromSeconds(119));
        Check(first.Status == ServerConnectionStatus.Reconnecting, "The unavailable threshold must not fire early.");
        manager.Report(first, ServerConnectionStatus.Monitoring);
        manager.Report(first, ServerConnectionStatus.Reconnecting);
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(first.Status == ServerConnectionStatus.Reconnecting, "Recovery must reset the continuous outage deadline.");
        clock.Advance(TimeSpan.FromSeconds(119));
        Check(first.Status == ServerConnectionStatus.Unavailable &&
            ServerConnectionPresentation.Color(first.Status) == "#ff0000", "Two continuous minutes must turn the failing server red.");
        manager.Report(first, ServerConnectionStatus.SetupRequired);
        Check(ServerConnectionPresentation.Text(first.Status) == "Setup Required" &&
            ServerConnectionPresentation.Color(first.Status) == "#ff0000" &&
            ServerConnectionPresentation.Color(ServerConnectionStatus.Disabled) == "#6b7280",
            "Setup Required and disabled must have separate labels and indicators.");

        manager.Synchronize([b, a with { Enabled = false }], token, Monitor);
        await first.Worker.WaitAsync(token);
        Check(manager.IsCurrent(second) && !second.Token.IsCancellationRequested,
            "Switching off one row must leave the other active.");
        manager.Retire(_ => true);
        await second.Worker.WaitAsync(token);
    }

    internal static Task LiveDisplayProjections()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var aCall = new CallItem { ServerId = a, BackendId = "same-id", Transcription = "A" };
        var bCall = new CallItem { ServerId = b, BackendId = "same-id", Transcription = "B" };
        ObservableCollection<CallItem> source = [bCall, aCall];
        var display = new LiveCallDisplay(source);
        display.SetServers([a, b, c]);
        display.Observe();

        Check(!display.Separate && ReferenceEquals(display.Combined, source) && display.Combined.Count == 2,
            "The default Combined view must use the canonical collection.");
        var aView = display.ForServer(a);
        var bView = display.ForServer(b);
        Check(ReferenceEquals(aView.Single(), aCall) && ReferenceEquals(bView.Single(), bCall),
            "Equal backend IDs must remain independent and projections must reuse the call objects.");

        display.SetSeparate(true);
        Check(display.VisibleServers(700, sideBySide: true).SequenceEqual(new[] { a, b }) &&
            display.VisibleServers(400, sideBySide: true).SequenceEqual(new[] { a }) &&
            display.VisibleServers(1_200, sideBySide: false).SequenceEqual(new[] { a }),
            "Windows panels should adapt to width and mobile should show only one server.");
        Check(display.MoveSelection(1) && display.VisibleServers(400, sideBySide: false).SequenceEqual(new[] { b }),
            "A horizontal move must show the next configured server.");
        display.SetServers([c, b]);
        Check(display.SelectedServer == b && display.Combined.Contains(aCall) &&
            !display.VisibleServers(900, sideBySide: true).Contains(a),
            "Disabling a server hides its panel while preserving its existing Combined cards.");

        var newB = new CallItem { ServerId = b, BackendId = "new" };
        source.Insert(0, newB);
        Check(bView.SequenceEqual(new[] { newB, bCall }) && aView.SequenceEqual(new[] { aCall }),
            "New observations must update only the matching projection in source order.");
        bCall.Transcription = "Revised";
        Check(bView.Last().Transcription == "Revised", "Late metadata must update the same display object.");
        source.Remove(aCall);
        Check(aView.Count == 0 && bView.Count == 2, "Eviction in one server cannot remove the same ID from another.");
        display.Detach();
        source.Clear();
        display.Observe();
        Check(bView.Count == 0, "Returning to Live must catch up the projection from the canonical list.");
        display.Detach();
        return Task.CompletedTask;
    }

    internal static async Task LiveDisplayRetention()
    {
        using var database = new IngestionDatabase();
        var queue = database.OpenQueue();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var run = new DurableLiveQueue.Run(RecorderA, 100, null, ServerId: a);
        await queue.StartListeningAsync(run, Now, default);
        await queue.ObserveAsync(run, "outside-window", Now, new(new(), RecorderA + "/clip.wav", 0), default);
        var waiting = new CallItem { ServerId = a, BackendId = "outside-window" };
        var playing = new CallItem { ServerId = b, BackendId = "playing" };
        ObservableCollection<CallItem> source = [playing, waiting];
        var display = new LiveCallDisplay(source, separate: true);
        var aCards = display.ForServer(a);
        var bCards = display.ForServer(b);
        display.Observe();

        for (var i = 0; i < 520; i++)
            LiveCallDisplay.InsertCard(source, new CallItem { ServerId = i % 2 == 0 ? a : b, BackendId = i.ToString() }, 0, playing);

        Check(source.Count == 500 && aCards.Count + bCards.Count == 500 &&
            source.Contains(playing) && !source.Contains(waiting) &&
            (await queue.NextAsync(run, 0, default))?.Id == waiting.BackendId,
            "Only display references may be evicted; preserve active audio and pending durable rows.");
        display.Detach();
    }

    internal static async Task LiveDisplaySwitchDuringPlayback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var ownership = new PlaybackOwnership();
        var lease = ownership.Live!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playing = ownership.RunNativeAsync(lease, token, cancellation =>
        {
            started.SetResult();
            return finish.Task.WaitAsync(cancellation);
        });
        await started.Task.WaitAsync(token);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var display = new LiveCallDisplay([]);
        display.SetServers([a, b]);
        display.SetSeparate(true);
        display.Select(b);
        display.SetSeparate(false);
        Check(ReferenceEquals(ownership.Live, lease) && ownership.CanPublishLivePlayback && !playing.IsCompleted,
            "Selecting a different pane must not cancel or replace the native playback lease.");

        finish.SetResult();
        await playing.WaitAsync(token);
    }
    private sealed class ConnectionStatusClock : TimeProvider
    {
        private long _timestamp;
        private ScheduledTimer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Check(period == Timeout.InfiniteTimeSpan, "The outage indicator uses a one-shot timer.");
            _timer = new ScheduledTimer(() => callback(state), _timestamp + dueTime.Ticks);
            return _timer;
        }

        internal void Advance(TimeSpan interval)
        {
            _timestamp += interval.Ticks;
            if (_timer is { } timer && timer.Deadline <= _timestamp)
                timer.Fire();
        }

        private sealed class ScheduledTimer(Action callback, long deadline) : ITimer
        {
            private bool _disposed;
            internal long Deadline { get; } = deadline;
            internal void Fire()
            {
                if (!_disposed)
                    callback();
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
