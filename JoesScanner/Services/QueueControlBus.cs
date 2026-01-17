namespace JoesScanner.Services
{
    // Minimal in-process event hub used to coordinate page level queue ownership.
    // Keep this focused on coarse signals only, no state is persisted here.
    public static class QueueControlBus
    {
        public static event Action? StopMainQueueRequested;

        // Legacy: stops the current main tab audio immediately.
        public static event Action? StopMainAudioRequested;

        // New: soft mutes the main tab audio without stopping the live queue.
        // When muted, the main queue continues to run and consumes calls silently.
        public static event Action<bool>? MainAudioMuteRequested;

        public static void RequestStopMainQueue()
        {
            try
            {
                StopMainQueueRequested?.Invoke();
            }
            catch
            {
            }
        }

        public static void RequestStopMainAudio()
        {
            try
            {
                StopMainAudioRequested?.Invoke();
            }
            catch
            {
            }
        }

        public static void RequestSetMainAudioMuted(bool isMuted)
        {
            try
            {
                MainAudioMuteRequested?.Invoke(isMuted);
            }
            catch
            {
            }
        }
    }
}
