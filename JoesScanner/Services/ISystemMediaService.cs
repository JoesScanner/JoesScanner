namespace JoesScanner.Services
{
    // System media integration surface (Bluetooth controls, lock screen controls, now playing info).
    // This is intentionally thin. Shared code provides handlers and metadata.
    public interface ISystemMediaService
    {
        // Called by the app to register the actions the OS can invoke.
        // Semantics for this app:
        // - Play = Connect
        // - Pause or Stop = Disconnect
        // - Next and Previous = queue navigation
        void SetHandlers(
            Func<Task> onPlay,
            Func<Task> onStop,
            Func<Task>? onNext = null,
            Func<Task>? onPrevious = null);

        // Start any platform specific session or foreground component that should exist while connected.
        Task StartSessionAsync(bool audioEnabled);

        // Stop and tear down platform specific session or foreground component.
        Task StopSessionAsync();

        // Update the OS visible now playing card.
        void UpdateNowPlaying(string title, string subtitle, bool audioEnabled);

        // Clear metadata and reset state.
        void Clear();
    }
}
