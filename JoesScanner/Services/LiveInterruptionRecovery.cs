using JoesScanner.Models;

namespace JoesScanner.Services;

[Flags]
internal enum LiveInterruptionSource
{
    NativeAudio = 1,
    AndroidFocus = 2,
    Background = 4,
    Connection = 8
}

// Owned by one monitoring run and accessed on the UI dispatcher.
internal sealed class LiveInterruptionRecovery(TimeProvider clock, Action<LiveInterruptionRecovery.Checkpoint?> persist)
{
    internal sealed record Checkpoint(DateTimeOffset Since, bool RequiresPlay);

    private LiveInterruptionSource _blocked;
    private Checkpoint? _checkpoint;
    private long _started;
    private TimeSpan _elapsedBeforeStart;
    private long? _recoveredAt;

    internal DateTimeOffset? Since => _checkpoint?.Since;
    internal bool RequiresPlay => _checkpoint?.RequiresPlay == true;
    internal bool IsInterrupted => _blocked != 0 || RequiresPlay;
    internal bool IsBlockedBy(LiveInterruptionSource source) => (_blocked & source) != 0;

    internal void SetBlocked(LiveInterruptionSource source, bool blocked, bool requiresPlay = false, DateTimeOffset? beganAt = null)
    {
        if (!blocked)
        {
            _blocked &= ~source;
            RecordRecoveryBoundary();
            return;
        }

        _blocked |= source;
        _recoveredAt = null;
        if (_checkpoint == null || beganAt is { } earlier && earlier < _checkpoint.Since)
        {
            var since = beganAt ?? clock.GetUtcNow();
            _started = clock.GetTimestamp();
            _elapsedBeforeStart = ElapsedSince(since);
            Save(new(since, requiresPlay || RequiresPlay));
        }
        else if (requiresPlay && !RequiresPlay)
            Save(_checkpoint with { RequiresPlay = true });
    }

    internal void AllowExplicitPlay()
    {
        if (_checkpoint is { RequiresPlay: true } checkpoint)
            Save(checkpoint with { RequiresPlay = false });
        RecordRecoveryBoundary();
    }

    internal bool? ShouldJump(InterruptionRecoveryPreference preference) =>
        _checkpoint == null || _recoveredAt is not { } recovered ? null :
            preference.ShouldJump(_elapsedBeforeStart + clock.GetElapsedTime(_started, recovered));

    internal void Complete(bool persisted = false)
    {
        if (!IsInterrupted && _checkpoint != null)
        {
            _checkpoint = null;
            if (!persisted)
                persist(null);
        }
    }

    private void Save(Checkpoint? checkpoint)
    {
        _checkpoint = checkpoint;
        persist(checkpoint);
    }

    private void RecordRecoveryBoundary()
    {
        if (_checkpoint != null && !IsInterrupted)
            _recoveredAt ??= clock.GetTimestamp();
    }

    private TimeSpan ElapsedSince(DateTimeOffset since)
    {
        var elapsed = clock.GetUtcNow() - since;
        return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    }
}
