namespace JoesScanner.Models
{
    internal sealed class CallItem(string transcript) : BindableObject
    {
        private string _transcription = transcript;
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
