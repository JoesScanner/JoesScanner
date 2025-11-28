namespace JoesScanner.Services
{
    public interface ISettingsService
    {
        string ServerUrl { get; set; }

        // Optional basic auth credentials for the TR server
        string BasicAuthUsername { get; set; }
        string BasicAuthPassword { get; set; }

        bool AutoPlay { get; set; }

        int MaxCalls { get; set; }

        /// <summary>
        /// "Down" means newest at bottom
        /// "Up" means newest at top
        /// </summary>
        string ScrollDirection { get; set; }

        /// <summary>
        /// Optional comma-separated list of receiver names to filter on.
        /// Empty string means no receiver filtering.
        /// </summary>
        string ReceiverFilter { get; set; }

        string TalkgroupFilter { get; set; }

        /// <summary>
        /// "System" follow device theme
        /// "Light" force light mode
        /// "Dark" force dark mode
        /// </summary>
        string ThemeMode { get; set; }
    }
}
