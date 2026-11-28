using JoesScanner.Services;

namespace JoesScanner.Models
{
    // Represents a single radio call as shown in the client UI.
    // All properties used in XAML bindings live here and are fed by the server API.
    public class CallItem : BindableObject
    {
        private DateTime _timestamp;
        private double _callDurationSeconds;
        private string _talkgroup = string.Empty;
        private string _source = string.Empty;
        private string _site = string.Empty;
        private string _voiceReceiver = string.Empty;
        private string _transcription = string.Empty;
        private string _audioUrl = string.Empty;
        private RecordingAudioAvailability _audioAvailability;
        private string _debugInfo = string.Empty;
        private bool _isHistory;
        private bool _isPlaying;
        private bool _isMutedByFilter;
        private bool _isToneHot;
        private string _backendId = string.Empty;
        internal LiveCallState? LiveState { get; init; }
        internal DateTimeOffset? RecordingTimeUtc { get; init; }
        internal int? HistoryArchiveOffset { get; init; }
        internal string? RecordingServer { get; init; }
        internal HistoryRecorderAccess? HistoryAccess { get; init; }
        internal RecordingAccessContext? RecordingAccess { get; set; }
        internal bool IsRecordingNotification { get; init; }
        internal long LivePlaybackOwner { get; set; }
        internal Guid? ServerId { get; set; }
        internal bool IsConnectionActionRequired { get; init; }
        internal long? LiveArrivalSequence { get; set; }
        internal long LiveAudioRevision { get; set; }
        internal bool IsOrderingUpdate { get; init; }
        internal bool IsCompletedReplay { get; set; }
        internal bool HasUnplayedLateAudio { get; private set; }
        internal long TranscriptionCorrectionVersion { get; private set; }

        internal bool ConsumeLateAudio()
        {
            var pending = HasUnplayedLateAudio;
            HasUnplayedLateAudio = false;
            return pending;
        }
        internal bool IsLiveUpdate { get; init; }
        internal string? RecoveryWindowId { get; set; }
        internal bool IsListeningBaseline { get; set; }
        internal bool IsCurrentTransmission { get; set; }
        internal bool WasReconciled { get; private set; }
        internal Action<bool>? LiveAdmission { private get; set; }

        internal void ConfirmLiveAdmission(bool accepted, bool conflictingRecordingTime = false)
        {
            WasReconciled = accepted || conflictingRecordingTime;
            var confirmation = LiveAdmission;
            LiveAdmission = null;
            confirmation?.Invoke(accepted);
        }
        private bool _isFavorite;
        public string FavoriteGlyph => _isFavorite ? "★" : "☆";
        public string FavoriteAction => _isFavorite ? "Remove Favorite" : "Add Favorite";
        public bool CanFavorite => ServerId.HasValue && FavoriteTalkgroupIdentity.From(CardMetadata) != null;

        internal void SetFavorite(bool favorite)
        {
            if (_isFavorite == favorite)
                return;
            _isFavorite = favorite;
            OnPropertyChanged(nameof(FavoriteGlyph));
            OnPropertyChanged(nameof(FavoriteAction));
        }

        private CallCardMetadata? _cardMetadata;

        public CallCardMetadata? CardMetadata
        {
            get => _cardMetadata;
            set
            {
                if (_cardMetadata == value)
                    return;

                _cardMetadata = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanFavorite));
            }
        }

        // Called on the UI thread for existing cards, including cards held in the pending queue.
        internal bool ApplyLiveUpdate()
        {
            if (LiveState is not { } state)
                return false;

            var snapshot = state.Read();
            CardMetadata = snapshot.Metadata;
            if (snapshot.Metadata.DurationSeconds is { } seconds)
                CallDurationSeconds = seconds;
            HasUnplayedLateAudio |= string.IsNullOrWhiteSpace(AudioUrl) && !string.IsNullOrWhiteSpace(snapshot.AudioUrl);
            ApplyRecordingAudio(snapshot);

            if (snapshot.Transcription is not { } transcript || Transcription == transcript)
                return false;

            var previous = Transcription;
            Transcription = transcript;
            if (Transcription == previous)
                return false;
            if (!IsMissingTranscription(previous) && !IsMissingTranscription(Transcription))
            {
                TranscriptionCorrectionVersion++;
                OnPropertyChanged(nameof(TranscriptionCorrectionVersion));
            }
            return true;
        }

        private string _detectedAddress = string.Empty;
                private int _addressDetectionAppliedHash;
private int _detectedAddressConfidencePercent;
        private string _detectedAddressCandidates = string.Empty;

        private string _what3WordsAddress = string.Empty;
        private int _what3WordsAppliedHash;
        private string _what3WordsNormalized = string.Empty;
        private double? _what3WordsLatitude;
        private double? _what3WordsLongitude;

        // Stable identifier for this call from the server (for example DT_RowId).
        public string BackendId
        {
            get => _backendId;
            set
            {
                if (_backendId == value)
                    return;

                _backendId = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
            }
        }

        // True when this item represents an update to an existing call
        // (used for refreshing transcription without inserting a new row).

        // Address detection: best match (if any) extracted from the transcription.
        public string DetectedAddress
        {
            get => _detectedAddress;
            set
            {
                var newValue = value ?? string.Empty;
                if (_detectedAddress == newValue)
                    return;

                _detectedAddress = newValue;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDetectedAddress));
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
            }
        }

        // Address detection: confidence score for DetectedAddress (0-100).
        public int DetectedAddressConfidencePercent
        {
            get => _detectedAddressConfidencePercent;
            set
            {
                if (_detectedAddressConfidencePercent == value)
                    return;

                _detectedAddressConfidencePercent = value;
                OnPropertyChanged();
            }
        }

        // Address detection: a simple, display-friendly list of other candidates.
        // Stored as a newline-delimited string to keep XAML binding simple.
        public string DetectedAddressCandidates
        {
            get => _detectedAddressCandidates;
            set
            {
                var newValue = value ?? string.Empty;
                if (_detectedAddressCandidates == newValue)
                    return;

                _detectedAddressCandidates = newValue;
                OnPropertyChanged();
            }
        }


        // Address detection: internal hash used to avoid rescanning the same transcription repeatedly.
        // This is not a UI-bound property.
        public int AddressDetectionAppliedHash
        {
            get => _addressDetectionAppliedHash;
            set => _addressDetectionAppliedHash = value;
        }

        public bool HasDetectedAddress => !string.IsNullOrWhiteSpace(_detectedAddress);

// what3words: first detected address in transcription, formatted as ///word.word.word
public string What3WordsAddress
{
    get => _what3WordsAddress;
    set
    {
        var newValue = value ?? string.Empty;
        if (_what3WordsAddress == newValue)
            return;

        _what3WordsAddress = newValue;

        // Reset resolved coordinates whenever the words change.
        _what3WordsNormalized = string.Empty;
        _what3WordsLatitude = null;
        _what3WordsLongitude = null;

        OnPropertyChanged();
        OnPropertyChanged(nameof(HasWhat3WordsAddress));
        OnPropertyChanged(nameof(HasWhat3WordsCoordinates));
        OnPropertyChanged(nameof(What3WordsCoordinatesText));
        OnPropertyChanged(nameof(AccessibilitySummary));
        OnPropertyChanged(nameof(AccessibilityAnnouncement));
    }
}

// what3words: internal hash used to avoid rescanning the same transcription repeatedly.
// This is not a UI-bound property.
public int What3WordsAppliedHash
{
    get => _what3WordsAppliedHash;
    set => _what3WordsAppliedHash = value;
}

public bool HasWhat3WordsAddress => !string.IsNullOrWhiteSpace(_what3WordsAddress);

// Internal: normalized words without leading ///
public string What3WordsNormalized
{
    get => _what3WordsNormalized;
    set
    {
        var newValue = value ?? string.Empty;
        if (_what3WordsNormalized == newValue)
            return;
        _what3WordsNormalized = newValue;
        OnPropertyChanged();
    }
}

public double? What3WordsLatitude
{
    get => _what3WordsLatitude;
    set
    {
        if (_what3WordsLatitude == value)
            return;
        _what3WordsLatitude = value;
        OnPropertyChanged();
        OnPropertyChanged(nameof(HasWhat3WordsCoordinates));
        OnPropertyChanged(nameof(What3WordsCoordinatesText));
    }
}

public double? What3WordsLongitude
{
    get => _what3WordsLongitude;
    set
    {
        if (_what3WordsLongitude == value)
            return;
        _what3WordsLongitude = value;
        OnPropertyChanged();
        OnPropertyChanged(nameof(HasWhat3WordsCoordinates));
        OnPropertyChanged(nameof(What3WordsCoordinatesText));
    }
}

public bool HasWhat3WordsCoordinates => _what3WordsLatitude.HasValue && _what3WordsLongitude.HasValue;

public string What3WordsCoordinatesText
    {
        get
        {
            var lat = _what3WordsLatitude;
            var lon = _what3WordsLongitude;

            if (!lat.HasValue || !lon.HasValue)
                return string.Empty;

            return $"{lat.Value:F6}, {lon.Value:F6}";
        }
    }


        // Timestamp of the call (local time). Comes from the server StartTime / StartTimeUTC.
        public DateTime Timestamp
        {
            get => _timestamp;
            set
            {
                if (_timestamp == value)
                    return;

                _timestamp = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TimeDisplay));
                OnPropertyChanged(nameof(TimeOnlyDisplay));
                OnPropertyChanged(nameof(DateDisplay));
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
                OnPropertyChanged(nameof(HeaderLine));
            }
        }

        // Duration in seconds as reported by the server (CallDuration).
        public double CallDurationSeconds
        {
            get => _callDurationSeconds;
            set
            {
                if (Math.Abs(_callDurationSeconds - value) < 0.001)
                    return;

                _callDurationSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationDisplay));
                OnPropertyChanged(nameof(DurationMmSsDisplay));
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
            }
        }

        // Talkgroup display string (for example "Nampa PD 1 (1234)").
        // Comes from TargetLabel / TargetID.
        public string Talkgroup
        {
            get => _talkgroup;
            set
            {
                if (_talkgroup == value)
                    return;

                _talkgroup = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ToneHotKey));
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
                OnPropertyChanged(nameof(HeaderLine));
            }
        }

        // Source or radio ID / label for this call (SourceLabel / SourceID).
        public string Source
        {
            get => _source;
            set
            {
                if (_source == value)
                    return;

                _source = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReceiverName));
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
                OnPropertyChanged(nameof(HeaderLine));
            }
        }

        // Site or system name (SiteLabel / SiteID).
        public string Site
        {
            get => _site;
            set
            {
                if (_site == value)
                    return;

                _site = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SystemName));
                OnPropertyChanged(nameof(ToneHotKey));
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
                OnPropertyChanged(nameof(HeaderLine));
            }
        }

        // Tone alerts use a stable per talkgroup key for a 5 minute highlight window.
        public string ToneHotKey => $"{SystemName}|{Talkgroup}";

        public bool IsToneHot
        {
            get => _isToneHot;
            set
            {
                if (_isToneHot == value)
                    return;

                _isToneHot = value;
                OnPropertyChanged();
            }
        }

        // Voice receiver name (VoiceReceiver column from the server).
        public string VoiceReceiver
        {
            get => _voiceReceiver;
            set
            {
                if (_voiceReceiver == value)
                    return;

                _voiceReceiver = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReceiverName));
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
                OnPropertyChanged(nameof(HeaderLine));
            }
        }

        // Transcription text for this call, with normalized whitespace.
        public string Transcription
        {
            get => _transcription;
            set
            {
                var normalized = NormalizeTranscriptionForUi(value);

                if (_transcription == normalized)
                    return;

                _transcription = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TranscriptionDisplay));
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
            }
        }

        internal const string MissingTranscriptionMessage = "No transcription from server";

        internal static bool IsMissingTranscription(string? text) =>
            string.IsNullOrWhiteSpace(text) ||
            string.Equals(text, MissingTranscriptionMessage, StringComparison.OrdinalIgnoreCase);

        public string TranscriptionDisplay =>
            IsMissingTranscription(Transcription) ? MissingTranscriptionMessage : Transcription;

        // Absolute or relative audio URL for this call.
        public string AudioUrl
        {
            get => _audioUrl;
            set
            {
                SetRecordingAudio(value, string.IsNullOrWhiteSpace(value)
                    ? RecordingAudioAvailability.Unknown : RecordingAudioAvailability.Available);
            }
        }

        internal void ApplyRecordingAudio(LiveCallSnapshot snapshot) =>
            SetRecordingAudio(snapshot.AudioUrl, snapshot.EffectiveAvailability);

        private void SetRecordingAudio(string? url, RecordingAudioAvailability availability)
        {
            url ??= string.Empty;
            var previousDiagnostic = DebugInfo;
            var urlChanged = _audioUrl != url;
            _audioAvailability = availability;
            _audioUrl = url;
            if (urlChanged)
                OnPropertyChanged(nameof(AudioUrl));
            NotifyDiagnosticChange(previousDiagnostic);
        }

        // Lookups commit to the ordered recorder state. A UI projection has no
        // independent authority to accept or reject that observation.
        internal string? ApplyRefreshedAudioUrl(string? refreshed, LiveCallSnapshot? beforeLookup, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (LiveState is not { } state)
                return refreshed;
            var snapshot = state.Read();
            ApplyRecordingAudio(snapshot);
            var recoveredSinceLookup = (snapshot.AudioOrder?.LocalChange ?? 0) > (beforeLookup?.AudioOrder?.LocalChange ?? 0);
            return refreshed != null || recoveredSinceLookup ? snapshot.AudioUrl : null;
        }

        public string DebugInfo
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_debugInfo))
                    return _debugInfo;
                if (LiveState == null || _audioAvailability != RecordingAudioAvailability.Missing)
                    return string.Empty;
                return "No audio URL from server";
            }
            set
            {
                var previousDiagnostic = DebugInfo;
                _debugInfo = value ?? string.Empty;
                NotifyDiagnosticChange(previousDiagnostic);
            }
        }

        private void NotifyDiagnosticChange(string previous)
        {
            if (previous == DebugInfo)
                return;
            OnPropertyChanged(nameof(DebugInfo));
            OnPropertyChanged(nameof(HasDebugInfo));
        }

        // True when there is any debug info to show.
        public bool HasDebugInfo => !string.IsNullOrWhiteSpace(DebugInfo);

        // True for calls that have already been played and are now in history.
        // Used to visually de emphasize older calls in the UI.
        public bool IsHistory
        {
            get => _isHistory;
            set
            {
                if (_isHistory == value)
                    return;

                _isHistory = value;
                OnPropertyChanged();
            }
        }

        // True while this call is being played back. Used for visual state only.
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying == value)
                    return;

                _isPlaying = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
            }
        }

        // True when the call is muted by an active filter rule. The call remains visible. Queue autoplay will skip it, but the user can still tap to play.
        public bool IsMutedByFilter
        {
            get => _isMutedByFilter;
            set
            {
                if (_isMutedByFilter == value)
                    return;

                _isMutedByFilter = value;
                OnPropertyChanged();
            }
        }

        // Time display used in the UI, based on the Timestamp.
        public string TimeDisplay =>
            Timestamp == default
                ? string.Empty
                : Timestamp.ToString("M/d/yyyy HH:mm:ss");

        // Time-only display (used in History cards so the header doesn't get too wide).
        public string TimeOnlyDisplay =>
            Timestamp == default
                ? string.Empty
                : Timestamp.ToString("HH:mm:ss");

        // Date display used in the call card header area.
        public string DateDisplay =>
            Timestamp == default
                ? string.Empty
                : Timestamp.ToString("M/d/yyyy");

        // Single-line header used in call cards (avoids FormattedString spans in item templates).
        public string HeaderLine
        {
            get
            {
                var time = TimeOnlyDisplay;
                var receiver = ReceiverName;
                var system = SystemName;
                var tg = Talkgroup;

                if (string.IsNullOrWhiteSpace(time))
                    return string.Empty;

                return $"{time} > {receiver} > {system} > {tg}";
            }
        }

        // Duration label used in the UI, derived from CallDurationSeconds.
        public string DurationDisplay =>
            CallCardMetadata.FormatDuration(CallDurationSeconds, compact: false) ?? string.Empty;

        // Compact duration for UI where we want the call length in minutes.
        // Examples: 0:12, 1:05, 12:34, 1:02:03
        public string DurationMmSsDisplay =>
            CallCardMetadata.FormatDuration(CallDurationSeconds, compact: true) ?? string.Empty;

        // Receiver name shown in the UI.
        // Prefers VoiceReceiver, falls back to Source if receiver is not set.
        public string ReceiverName =>
            string.IsNullOrWhiteSpace(VoiceReceiver)
                ? Source
                : VoiceReceiver;

        // System name shown in the UI. Uses the Site value.
        public string SystemName => Site;

        // One-line summary used by screen readers.
        public string AccessibilitySummary
        {
            get
            {
                var time = TimeDisplay;
                var site = SystemName;
                var tg = string.IsNullOrWhiteSpace(Talkgroup) ? "Unknown talkgroup" : Talkgroup;
                var rx = string.IsNullOrWhiteSpace(ReceiverName) ? "Unknown receiver" : ReceiverName;
                var dur = DurationDisplay;

                var parts = new System.Collections.Generic.List<string>();

                if (!string.IsNullOrWhiteSpace(time)) parts.Add(time);
                parts.Add(tg);
                if (!string.IsNullOrWhiteSpace(site)) parts.Add(site);
                if (!string.IsNullOrWhiteSpace(rx)) parts.Add(rx);
                if (!string.IsNullOrWhiteSpace(dur)) parts.Add(dur);

                // Include transcription if present, but keep it short.
                if (!string.IsNullOrWhiteSpace(Transcription))
                {
                    parts.Add(TruncateForAccessibility(Transcription, 140));
                }

                // Add a short playing cue if applicable.
                if (IsPlaying)
                {
                    parts.Add("Playing");
                }

                return string.Join(". ", parts);
            }
        }

        // Shorter text used when announcing a brand-new incoming call.
        public string AccessibilityAnnouncement
        {
            get
            {
                var tg = string.IsNullOrWhiteSpace(Talkgroup) ? "Unknown talkgroup" : Talkgroup;
                var site = string.IsNullOrWhiteSpace(SystemName) ? string.Empty : SystemName;

                if (string.IsNullOrWhiteSpace(site))
                    return $"New call. {tg}.";

                return $"New call. {tg}. {site}.";
            }
        }

        // Match the live transcription sanitizer's incoming limit for callers that bypass it.
        private const int MaxIncomingTranscriptionChars = 20000;

        private static string NormalizeTranscriptionForUi(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var input = text.AsSpan(0, Math.Min(text.Length, MaxIncomingTranscriptionChars)).Trim();
            if (input.IsEmpty)
                return string.Empty;

            // The live stream already normalizes whitespace. Reuse its string when possible.
            for (var i = 1; i < input.Length; i++)
            {
                var ch = input[i];
                if (!char.IsWhiteSpace(ch) || (ch == ' ' && input[i - 1] != ' '))
                    continue;

                var normalized = new System.Text.StringBuilder(input.Length).Append(input[..i]);
                var lastWasSpace = input[i - 1] == ' ';
                foreach (var c in input[i..])
                {
                    if (char.IsWhiteSpace(c))
                    {
                        if (!lastWasSpace)
                            normalized.Append(' ');
                        lastWasSpace = true;
                    }
                    else
                    {
                        normalized.Append(c);
                        lastWasSpace = false;
                    }
                }

                return normalized.ToString();
            }

            return input.Length == text.Length ? text : input.ToString();
        }

        private static string TruncateForAccessibility(string text, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var t = text.Trim();
            if (t.Length <= maxLen)
                return t;

            return t.Substring(0, maxLen).TrimEnd() + "...";
        }
    }
}
