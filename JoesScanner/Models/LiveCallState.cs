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

internal enum RecordingAudioAvailability { Unknown, Available, Missing }

// Sequence numbers are local to a running process. A journal entry from an earlier
// launch can seed a card, but cannot outrank an observation from this launch.
internal readonly record struct AudioObservationOrder(Guid Session, long Sequence, long LastChange)
{
    internal static readonly Guid CurrentSession = Guid.NewGuid();
    internal long LocalSequence => Session == CurrentSession ? Sequence : 0;
    internal long LocalChange => Session == CurrentSession ? LastChange : 0;
    internal static AudioObservationOrder At(long sequence, long lastChange) => new(CurrentSession, sequence, lastChange);

    internal bool Supersedes(AudioObservationOrder? previous) =>
        previous == null || LocalChange > previous.Value.LocalChange ||
        LocalChange == previous.Value.LocalChange && LocalSequence >= previous.Value.LocalSequence;
}

// The indexes are the persisted CallCardField values followed by the three recorder IDs
// and transcription. Older payloads have no watermarks and seed fields at version zero.
internal sealed record RecordingFieldOrder(Guid Session, System.Collections.Immutable.ImmutableArray<long> Versions)
{
    internal long LocalVersion(int index) =>
        Session == AudioObservationOrder.CurrentSession && !Versions.IsDefault && index < Versions.Length ? Versions[index] : 0;

    internal bool SameVersions(RecordingFieldOrder? other) =>
        other != null && Session == other.Session && Versions.AsSpan().SequenceEqual(other.Versions.AsSpan());
}

internal sealed record LiveCallSnapshot(CallCardMetadata Metadata, string? Transcription,
    CallCardIdFallback IdFallbacks, string? AudioUrl = null)
{
    public long? RecordingUnixSeconds { get; init; }
    public RecordingAudioAvailability Availability { get; init; }
    public AudioObservationOrder? AudioOrder { get; init; }
    public RecordingFieldOrder? FieldOrder { get; init; }
    public string? SignedReferenceTag { get; init; }
    internal RecordingAudioAvailability EffectiveAvailability =>
        string.IsNullOrWhiteSpace(AudioUrl) ? Availability : RecordingAudioAvailability.Available;
}

// A card owns this state through visible/pending transitions. The stream and HTTP lookups can
// observe it on worker threads, while the UI reads an immutable snapshot to update its bindings.
internal sealed class LiveCallState(string serverUrl, string id)
{
    private static long _nextVersion;
    private readonly object _gate = new();
    private const int SystemIdField = (int)CallCardField.Lcn + 1;
    private const int TalkgroupIdField = SystemIdField + 1;
    private const int SiteIdField = TalkgroupIdField + 1;
    private const int TranscriptionField = SiteIdField + 1;
    private readonly long[] _fieldVersions = new long[TranscriptionField + 1];
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

    internal void RestoreSnapshot(LiveCallSnapshot snapshot) =>
        Observe(snapshot with { AudioOrder = snapshot.AudioOrder ?? default(AudioObservationOrder) }, 0, out _);

    internal void MergeRecorderState(LiveCallState source) => RestoreSnapshot(source.Read());

    private bool Observe(LiveCallSnapshot update, long version, out string? changedTranscription, bool recoveringAudio,
        string? priorUrl)
    {
        lock (_gate)
        {
            changedTranscription = null;
            if (_snapshot.RecordingUnixSeconds is { } known && update.RecordingUnixSeconds is { } incomingTime && known != incomingTime)
                return false;
            var recordingTime = _snapshot.RecordingUnixSeconds ?? update.RecordingUnixSeconds;
            var current = _snapshot.Metadata;
            var incoming = update.Metadata;
            var fallbacks = _snapshot.IdFallbacks;
            var fieldOrderChanged = false;
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
            if (!CallItem.IsMissingTranscription(update.Transcription) && AcceptField(TranscriptionField))
                transcript = update.Transcription;

            changedTranscription = transcript != _snapshot.Transcription ? transcript : null;
            var audioUrl = _snapshot.AudioUrl;
            var availability = _snapshot.Availability;
            var order = _snapshot.AudioOrder;
            var signedTag = _snapshot.SignedReferenceTag;
            var incomingAudio = update.EffectiveAvailability;
            var observedAt = update.AudioOrder?.LocalSequence ?? version;
            var lastSeen = order?.LocalSequence ?? 0;
            var takeAudio = update.AudioOrder is { } sourceOrder ? sourceOrder.Supersedes(order) :
                observedAt >= lastSeen || recoveringAudio && observedAt >= (order?.LocalChange ?? 0) && audioUrl == priorUrl;
            if (incomingAudio != RecordingAudioAvailability.Unknown && takeAudio)
            {
                // A sanitized journal reference says audio exists. It cannot replace a
                // transient URL from the very same observation with an empty string.
                if (incomingAudio != RecordingAudioAvailability.Available || audioUrl == null || update.AudioUrl != null ||
                    (update.AudioOrder?.LocalChange ?? observedAt) > (order?.LocalChange ?? 0))
                {
                    audioUrl = update.AudioUrl;
                    signedTag = update.SignedReferenceTag;
                }
                availability = incomingAudio;
                var changedAt = order?.LocalChange ?? 0;
                if (update.AudioOrder is { } supplied)
                    changedAt = Math.Max(changedAt, supplied.LocalChange);
                else if (recoveringAudio || availability != _snapshot.Availability || audioUrl != _snapshot.AudioUrl)
                    changedAt = observedAt;
                order = AudioObservationOrder.At(Math.Max(lastSeen, observedAt), changedAt);
            }
            var changed = current != metadata || changedTranscription != null ||
                audioUrl != _snapshot.AudioUrl || availability != _snapshot.Availability;
            if (changed || recordingTime != _snapshot.RecordingUnixSeconds || fallbacks != _snapshot.IdFallbacks || order != _snapshot.AudioOrder || fieldOrderChanged ||
                signedTag != _snapshot.SignedReferenceTag)
                _snapshot = new(metadata, transcript, fallbacks, audioUrl)
                {
                    RecordingUnixSeconds = recordingTime, Availability = availability, AudioOrder = order, SignedReferenceTag = signedTag,
                    FieldOrder = fieldOrderChanged ? new(AudioObservationOrder.CurrentSession, [.. _fieldVersions]) : _snapshot.FieldOrder
                };
            return changed;

            bool AcceptField(int index)
            {
                var observedVersion = update.FieldOrder?.LocalVersion(index) ?? version;
                if (observedVersion < _fieldVersions[index])
                    return false;
                if (observedVersion > _fieldVersions[index])
                {
                    _fieldVersions[index] = observedVersion;
                    fieldOrderChanged = true;
                }
                return true;
            }

            T Newer<T>(CallCardField field, T previous, T candidate) =>
                candidate is not null && AcceptField((int)field) ? candidate : previous;

            string? Text(CallCardField field, string? previous, string? candidate) =>
                Newer(field, previous, string.IsNullOrWhiteSpace(candidate) ? null : candidate);

            string? PreferLabel(CallCardField field, CallCardIdFallback flag, string? previous, string? candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate) ||
                    update.IdFallbacks.HasFlag(flag) && previous != null && !fallbacks.HasFlag(flag) || !AcceptField((int)field))
                    return previous;

                if (update.IdFallbacks.HasFlag(flag))
                    fallbacks |= flag;
                else
                    fallbacks &= ~flag;
                return candidate;
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
