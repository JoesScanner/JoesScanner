namespace JoesScanner.Services
{
    public interface IPlaybackCoordinator
    {
        // Central policy evaluation for whether queue playback should be started or resumed.
        // This method is intentionally side-effect free other than logging.
        bool CanStartQueuePlayback(
            string reason,
            string? serverUrl,
            bool userInitiated,
            bool isRunning,
            bool audioEnabled,
            bool isMainAudioSoftMuted,
            bool isAlreadyPlaying,
            bool isQueuePlaybackRunning,
            int visibleQueueCount);
    }
}
