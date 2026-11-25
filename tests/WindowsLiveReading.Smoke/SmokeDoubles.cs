namespace JoesScanner.Models
{
    internal sealed class CallItem(string transcript) : BindableObject
    {
        private string _transcription = transcript;

        public string Transcription
        {
            get => _transcription;
            set
            {
                _transcription = value;
                OnPropertyChanged();
            }
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
