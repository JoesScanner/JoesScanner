namespace JoesScanner.Models
{
    /// <summary>
    /// Represents a single radio call as shown in the client UI.
    /// All properties used in XAML bindings live here and are fed by the server API.
    /// </summary>
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
        private bool _isPlaying;

        /// <summary>
        /// Timestamp of the call (local time). Comes from the server StartTime / StartTimeUTC.
        /// </summary>
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
            }
        }

        /// <summary>
        /// Duration in seconds as reported by the server (CallDuration).
        /// </summary>
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
            }
        }

        /// <summary>
        /// Talkgroup display string (for example "Nampa PD 1 (1234)").
        /// Comes from TargetLabel / TargetID.
        /// </summary>
        public string Talkgroup
        {
            get => _talkgroup;
            set
            {
                if (_talkgroup == value)
                    return;

                _talkgroup = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Source or radio ID / label for this call (SourceLabel / SourceID).
        /// </summary>
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
            }
        }

        /// <summary>
        /// Site or system name (SiteLabel / SiteID).
        /// </summary>
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
            }
        }

        /// <summary>
        /// Voice receiver name (VoiceReceiver column from the server).
        /// </summary>
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
            }
        }

        /// <summary>
        /// Transcription text as produced by the server for this call.
        /// </summary>
        public string Transcription
        {
            get => _transcription;
            set
            {
                if (_transcription == value)
                    return;

                _transcription = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Absolute or relative audio URL for this call.
        /// </summary>
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

        /// <summary>
        /// Debug or diagnostic information for this call, shown on a separate line.
        /// Used for things like "No transcription from server", "No audio URL", etc.
        /// </summary>
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

        /// <summary>
        /// True when there is any debug info to show.
        /// </summary>
        public bool HasDebugInfo => !string.IsNullOrWhiteSpace(DebugInfo);


        /// <summary>
        /// True while this call is being played back. Used for visual state only.
        /// </summary>
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying == value)
                    return;

                _isPlaying = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Time display used in the UI. Based on the Timestamp.
        /// </summary>
        public string TimeDisplay =>
            Timestamp == default
                ? string.Empty
                : Timestamp.ToString("h:mm:ss tt");

        /// <summary>
        /// Duration label used in the UI. Derived from CallDurationSeconds.
        /// </summary>
        public string DurationDisplay =>
            CallDurationSeconds <= 0
                ? string.Empty
                : $"{CallDurationSeconds:F1}s";

        /// <summary>
        /// Receiver name shown in the UI.
        /// Prefers VoiceReceiver, falls back to Source if receiver is not set.
        /// </summary>
        public string ReceiverName =>
            string.IsNullOrWhiteSpace(VoiceReceiver)
                ? Source
                : VoiceReceiver;

        /// <summary>
        /// System name shown in the UI. Uses the Site value.
        /// </summary>
        public string SystemName => Site;
    }
}
