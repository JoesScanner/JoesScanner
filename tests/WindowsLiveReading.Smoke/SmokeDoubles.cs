namespace JoesScanner.Models
{
    internal sealed class CallItem(string transcript) : BindableObject
    {
        private string _transcription = transcript;
        internal Guid? ServerId { get; init; }
        public CallCardMetadata CardMetadata { get; } = new()
        {
            Timestamp = DateTime.Today.AddHours(18), System = "County", Talkgroup = "Dispatch",
            Site = "North", Radio = "Engine 12", DurationSeconds = 12.1
        };
        public bool CanFavorite => true;
        public string FavoriteGlyph => "★";
        public string FavoriteAction => "Remove Favorite";
        public bool IsMutedByFilter => false;
        public bool IsToneHot => false;
        public bool IsPlaying => false;
        public bool HasDetectedAddress { get; init; }
        public string DetectedAddress => "100 Main Street";
        public int DetectedAddressConfidencePercent => 92;
        public bool HasWhat3WordsAddress => false;
        public bool HasWhat3WordsCoordinates => false;
        public string What3WordsAddress => string.Empty;
        public string What3WordsCoordinatesText => string.Empty;
        public bool HasDebugInfo => false;
        public string DebugInfo => string.Empty;
        public string TranscriptionDisplay => Transcription;
        internal void SetFavorite(bool saved) { }
        public string Talkgroup { get; } = "Dispatch";
        public string TimeOnlyDisplay { get; } = "18:01:00";
        internal long TranscriptionCorrectionVersion { get; private set; }

        public string Transcription
        {
            get => _transcription;
            set
            {
                _transcription = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TranscriptionDisplay));
            }
        }

        internal void Correct(string text)
        {
            Transcription = text;
            TranscriptionCorrectionVersion++;
            OnPropertyChanged(nameof(TranscriptionCorrectionVersion));
        }
    }
}

namespace JoesScanner.Services
{
    internal static class AppLog
    {
        internal static void DebugWriteLine(Func<string> message) => Console.Error.WriteLine(message());
    }
}
