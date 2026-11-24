namespace JoesScanner.Models;

[Flags]
internal enum CallCardIdFallback
{
    None = 0,
    System = 1,
    Talkgroup = 2,
    Site = 4,
    Radio = 8
}

internal sealed record LiveCallSnapshot(CallCardMetadata Metadata, string? Transcription,
    CallCardIdFallback IdFallbacks, string? AudioUrl = null);

// A card owns this state through visible/pending transitions. The stream and HTTP lookups can
// observe it on worker threads, while the UI reads an immutable snapshot to update its bindings.
internal sealed class LiveCallState(string serverUrl, string id)
{
    private static long _nextVersion;
    private readonly object _gate = new();
    private const int SystemIdField = (int)CallCardField.Lcn + 1;
    private const int TalkgroupIdField = SystemIdField + 1;
    private const int SiteIdField = TalkgroupIdField + 1;
    private readonly long[] _fieldVersions = new long[SiteIdField + 1];
    private long _transcriptionVersion;
    private long _audioVersion;
    private LiveCallSnapshot _snapshot = new(new(), null, CallCardIdFallback.None);
    private bool _notificationPending;
    private DateTimeOffset _nextNotificationUtc;

    public string ServerUrl { get; } = serverUrl;
    public string Id { get; } = id;
    public static long NextVersion() => Interlocked.Increment(ref _nextVersion);

    public LiveCallSnapshot Read()
    {
        lock (_gate)
            return _snapshot;
    }

    // HTTP observations are stamped before the request, so a late response can fill gaps but
    // cannot replace a field supplied by a later observation. Missing values never erase data.
    public bool Observe(LiveCallSnapshot update, long version) => Observe(update, version, out _);

    public bool Observe(LiveCallSnapshot update, long version, out string? changedTranscription) =>
        Observe(update, version, out changedTranscription, recoveringAudio: false, priorUrl: null);

    internal void ObserveRecoveredAudio(LiveCallSnapshot update, long version, string? priorUrl) =>
        Observe(update, version, out _, recoveringAudio: true, priorUrl);

    private bool Observe(LiveCallSnapshot update, long version, out string? changedTranscription, bool recoveringAudio, string? priorUrl)
    {
        lock (_gate)
        {
            var current = _snapshot.Metadata;
            var incoming = update.Metadata;
            var fallbacks = _snapshot.IdFallbacks;
            var metadata = new CallCardMetadata
            {
                Timestamp = Newer(CallCardField.Time, current.Timestamp, incoming.Timestamp),
                SystemId = Text((CallCardField)SystemIdField, current.SystemId, incoming.SystemId),
                TalkgroupId = Text((CallCardField)TalkgroupIdField, current.TalkgroupId, incoming.TalkgroupId),
                SiteId = Text((CallCardField)SiteIdField, current.SiteId, incoming.SiteId),
                System = PreferLabel(CallCardField.System, CallCardIdFallback.System, current.System, incoming.System),
                Talkgroup = PreferLabel(CallCardField.Talkgroup, CallCardIdFallback.Talkgroup, current.Talkgroup, incoming.Talkgroup),
                Site = PreferLabel(CallCardField.Site, CallCardIdFallback.Site, current.Site, incoming.Site),
                Radio = PreferLabel(CallCardField.Radio, CallCardIdFallback.Radio, current.Radio, incoming.Radio),
                Receiver = Text(CallCardField.Receiver, current.Receiver, incoming.Receiver),
                DurationSeconds = Newer(CallCardField.Duration, current.DurationSeconds,
                    CallCardMetadata.IsValidDuration(incoming.DurationSeconds) ? incoming.DurationSeconds : null),
                Frequency = Text(CallCardField.Frequency, current.Frequency, incoming.Frequency),
                TalkgroupTag = Text(CallCardField.TalkgroupTag, current.TalkgroupTag, incoming.TalkgroupTag),
                RadioTag = Text(CallCardField.RadioTag, current.RadioTag, incoming.RadioTag),
                SystemType = Text(CallCardField.SystemType, current.SystemType, incoming.SystemType),
                CallType = Text(CallCardField.CallType, current.CallType, incoming.CallType),
                VoiceType = Text(CallCardField.VoiceType, current.VoiceType, incoming.VoiceType),
                Lcn = Text(CallCardField.Lcn, current.Lcn, incoming.Lcn)
            };

            var transcript = _snapshot.Transcription;
            if (!CallItem.IsMissingTranscription(update.Transcription) && version >= _transcriptionVersion)
            {
                _transcriptionVersion = version;
                transcript = update.Transcription;
            }

            changedTranscription = transcript != _snapshot.Transcription ? transcript : null;
            var audioUrl = _snapshot.AudioUrl;
            // A duplicate notification carrying the same stale URL does not
            // supersede a fresh lookup. A genuinely different later URL does.
            if (!string.IsNullOrWhiteSpace(update.AudioUrl) &&
                (version >= _audioVersion || recoveringAudio && audioUrl == priorUrl))
            {
                _audioVersion = Math.Max(_audioVersion, version);
                audioUrl = update.AudioUrl;
            }
            var changed = current != metadata || changedTranscription != null || audioUrl != _snapshot.AudioUrl;
            if (changed || fallbacks != _snapshot.IdFallbacks)
                _snapshot = new(metadata, transcript, fallbacks, audioUrl);
            return changed;

            T Newer<T>(CallCardField field, T previous, T candidate)
            {
                var index = (int)field;
                if (candidate is null || version < _fieldVersions[index])
                    return previous;

                _fieldVersions[index] = version;
                return candidate;
            }

            string? Text(CallCardField field, string? previous, string? candidate) =>
                Newer(field, previous, string.IsNullOrWhiteSpace(candidate) ? null : candidate);

            string? PreferLabel(CallCardField field, CallCardIdFallback flag, string? previous, string? candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate) || version < _fieldVersions[(int)field])
                    return previous;

                if (update.IdFallbacks.HasFlag(flag))
                {
                    if (previous != null && !fallbacks.HasFlag(flag))
                        return previous;
                    fallbacks |= flag;
                }
                else
                {
                    fallbacks &= ~flag;
                }
                return Newer(field, previous, candidate);
            }
        }
    }

    public bool TryQueueNotification(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_notificationPending || now < _nextNotificationUtc ||
                !NeedsLookup(_snapshot))
                return false;

            _notificationPending = true;
            _nextNotificationUtc = now.AddSeconds(2);
            return true;
        }
    }

    public void CompleteNotification()
    {
        lock (_gate)
            _notificationPending = false;
    }

    public bool NeedsNotificationLookup() => NeedsLookup(Read());

    private static bool NeedsLookup(LiveCallSnapshot snapshot) =>
        snapshot.Transcription == null || snapshot.Metadata.DurationSeconds == null || snapshot.AudioUrl == null;

    public CallItem? FindCard(IEnumerable<CallItem> visible, List<CallItem> pending, object pendingGate)
    {
        var card = visible.FirstOrDefault(call => ReferenceEquals(call.LiveState, this));
        if (card != null)
            return card;

        lock (pendingGate)
            return pending.Find(call => ReferenceEquals(call.LiveState, this));
    }
}
