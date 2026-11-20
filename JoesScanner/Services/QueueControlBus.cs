namespace JoesScanner.Services
{
    // Minimal in-process event hub used to coordinate page level queue ownership.
    // Keep this focused on coarse signals only, no state is persisted here.
    public static class QueueControlBus
    {
        public static event Action? StopMainQueueRequested;

        // Legacy: stops the current main tab audio immediately.
        public static event Action? StopMainAudioRequested;

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

    }
}
