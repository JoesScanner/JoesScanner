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
        private string _debugInfo = string.Empty;
        private bool _isHistory;
        private bool _isPlaying;
        private string _backendId = string.Empty;
        private bool _isTranscriptionUpdate;

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
        public bool IsTranscriptionUpdate
        {
            get => _isTranscriptionUpdate;
            set
            {
                if (_isTranscriptionUpdate == value)
                    return;

                _isTranscriptionUpdate = value;
                OnPropertyChanged();
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
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
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
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
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
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
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
            }
        }

        // Transcription text as produced by the server for this call.
        public string Transcription
        {
            get => _transcription;
            set
            {
                if (_transcription == value)
                    return;

                _transcription = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AccessibilitySummary));
                OnPropertyChanged(nameof(AccessibilityAnnouncement));
            }
        }

        // Absolute or relative audio URL for this call.
        public string AudioUrl
        {
            get => _audioUrl;
            set
            {
                if (_audioUrl == value)
                    return;

                _audioUrl = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        // Debug or diagnostic information for this call, shown on a separate line.
        // Used for things like "No transcription from server", "No audio URL", etc.
        public string DebugInfo
        {
            get => _debugInfo;
            set
            {
                if (_debugInfo == value)
                    return;

                _debugInfo = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDebugInfo));
            }
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

        // Time display used in the UI, based on the Timestamp.
        public string TimeDisplay =>
            Timestamp == default
                ? string.Empty
                : Timestamp.ToString("h:mm:ss tt");

        // Duration label used in the UI, derived from CallDurationSeconds.
        public string DurationDisplay =>
            CallDurationSeconds <= 0
                ? string.Empty
                : $"{CallDurationSeconds:F1}s";

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
