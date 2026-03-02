namespace JoesScanner.Services
{
    public interface IToneAlertService
    {
        // Raised when the audio pipeline detects tones for a call.
        // View models can map the audioUrl back to a CallItem and mark its talkgroup hot.
        event Action<string>? ToneDetected;

        void NotifyToneDetected(string audioUrl);

        void SetTalkgroupHot(string hotKey, TimeSpan duration);

        bool IsTalkgroupHot(string hotKey);

        event Action? HotTalkgroupsChanged;
    }
}
