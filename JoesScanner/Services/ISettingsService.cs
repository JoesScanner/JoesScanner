namespace JoesScanner.Services
{
    public interface ISettingsService
    {
        // Base URL of the Trunking Recorder or audio server.
        string ServerUrl { get; set; }

        // Optional basic auth credentials for the TR server.
        string BasicAuthUsername { get; set; }
        string BasicAuthPassword { get; set; }

        // When true, calls auto-play as they arrive.
        bool AutoPlay { get; set; }

        // Maximum number of calls to keep in the visible queue.
        int MaxCalls { get; set; }

        // Scroll behavior:
        //   "Down" = newest at bottom
        //   "Up"   = newest at top
        string ScrollDirection { get; set; }

        // Optional comma-separated list of receiver names to filter on.
        // Empty string means no receiver filtering.
        string ReceiverFilter { get; set; }

        // Optional talkgroup filter string.
        string TalkgroupFilter { get; set; }

        // Theme preference:
        //   "System" = follow device theme
        //   "Light"  = force light mode
        //   "Dark"   = force dark mode
        string ThemeMode { get; set; }
    }
}
