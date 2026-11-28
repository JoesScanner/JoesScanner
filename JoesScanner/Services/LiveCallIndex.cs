using JoesScanner.Models;

namespace JoesScanner.Services;

// Per stream. Keep seen IDs for deduplication, and let the visible and pending cards own
// their update state. Periodically remove weak references to cards that left both queues.
internal sealed class LiveCallIndex
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _provisional = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeakReference<LiveCallState>> _calls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LiveCallState> _notified = new();
    private int _addedSinceCleanup;
    private DateTimeOffset _nextTranscriptionCatchupUtc = DateTimeOffset.UtcNow.AddSeconds(2);

    public bool MarkSeen(string id) => _seen.Add(id);

    public bool NeedsAdmission(string id) => _provisional.Contains(id);

    public void ConfirmAdmission(string id, bool accepted)
    {
        if (accepted)
            _provisional.Remove(id);
        else
        {
            _provisional.Add(id);
            // A failed admission, including a timestamp collision with the journal, cannot bind this ID
            // to the rejected state when a later row describes its original recording.
            _calls.Remove(id);
        }
    }

    public string OriginalId(string id) => _seen.TryGetValue(id, out var original) ? original : id;

    public void Seed(IEnumerable<CallItem> cards, string serverUrl)
    {
        foreach (var card in cards)
        {
            if (card.LiveState is { } state && state.ServerUrl == serverUrl && MarkSeen(state.Id))
            {
                ConfirmAdmission(state.Id, card.LiveArrivalSequence.HasValue);
                Track(state);
            }
        }
    }

    public void Track(LiveCallState state)
    {
        _calls[state.Id] = new(state);
        if (++_addedSinceCleanup < 128)
            return;

        _addedSinceCleanup = 0;
        foreach (var entry in _calls)
        {
            if (!entry.Value.TryGetTarget(out _))
                _calls.Remove(entry.Key);
        }
    }

    public LiveCallState? Find(string id) =>
        _calls.TryGetValue(id, out var weak) && weak.TryGetTarget(out var state) ? state : null;

    public void QueueNotification(LiveCallState state, DateTimeOffset now)
    {
        if (state.TryQueueNotification(now))
            _notified.Add(state);
    }

    // null means no request. An empty list means only the existing latest-page transcript catch-up.
    public LiveCallState[]? BeginCatchup(DateTimeOffset now)
    {
        if (_notified.Count > 0)
        {
            var targets = new List<LiveCallState>(_notified.Count);
            foreach (var state in _notified)
            {
                if (state.NeedsNotificationLookup())
                    targets.Add(state);
                else
                    state.CompleteNotification();
            }
            _notified.Clear();
            if (targets.Count > 0)
            {
                _nextTranscriptionCatchupUtc = now.AddSeconds(2);
                return targets.ToArray();
            }
        }

        if (now < _nextTranscriptionCatchupUtc ||
            !_calls.Values.Any(weak => weak.TryGetTarget(out var state) && state.Read().Transcription == null))
            return null;

        _nextTranscriptionCatchupUtc = now.AddSeconds(2);
        return [];
    }

    public void ClearNotifications()
    {
        foreach (var state in _notified)
            state.CompleteNotification();
        _notified.Clear();
    }
}
