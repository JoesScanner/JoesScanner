namespace JoesScanner.Services
{
    // Concrete implementation of ISettingsService backed by Preferences storage.
    // Provides typed getters and setters for all user-configurable settings.
    public class SettingsService : ISettingsService
    {
        // Preference keys.
        private const string ServerUrlKey = "ServerUrl";
        private const string BasicAuthUserKey = "BasicAuthUser";
        private const string BasicAuthPassKey = "BasicAuthPass";
        private const string AutoPlayKey = "AutoPlay";
        private const string MaxCallsKey = "MaxCalls";
        private const string ScrollDirectionKey = "ScrollDirection";
        private const string ReceiverFilterKey = "ReceiverFilter";
        private const string TalkgroupFilterKey = "TalkgroupFilter";
        private const string ThemeModeKey = "ThemeMode";

        // Default values.
        private const string DefaultServerUrl = "https://app.joesscanner.com";

        // Base URL of the Trunking Recorder or audio server.
        public string ServerUrl
        {
            get => Preferences.Get(ServerUrlKey, DefaultServerUrl);
            set => Preferences.Set(ServerUrlKey, string.IsNullOrWhiteSpace(value) ? DefaultServerUrl : value);
        }

        // Optional basic auth username for the TR server.
        public string BasicAuthUsername
        {
            get => Preferences.Get(BasicAuthUserKey, string.Empty);
            set => Preferences.Set(BasicAuthUserKey, value ?? string.Empty);
        }

        // Optional basic auth password for the TR server.
        public string BasicAuthPassword
        {
            get => Preferences.Get(BasicAuthPassKey, string.Empty);
            set => Preferences.Set(BasicAuthPassKey, value ?? string.Empty);
        }

        // When true, calls auto-play as they arrive.
        public bool AutoPlay
        {
            get => Preferences.Get(AutoPlayKey, false);
            set => Preferences.Set(AutoPlayKey, value);
        }

        // Maximum number of calls to keep in the visible queue.
        // Value is clamped to a range of 10–50 before being stored.
        public int MaxCalls
        {
            get => Preferences.Get(MaxCallsKey, 20); // default 20
            set
            {
                var v = value;

                // Clamp to 10–50.
                if (v < 10) v = 10;
                if (v > 50) v = 50;

                Preferences.Set(MaxCallsKey, v);
            }
        }

        // Scroll behavior:
        //   "Down" = newest at bottom
        //   "Up"   = newest at top
        public string ScrollDirection
        {
            get => Preferences.Get(ScrollDirectionKey, "Down");
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? "Down" : value;
                Preferences.Set(ScrollDirectionKey, v);
            }
        }

        // Optional comma-separated list of receiver names to filter on.
        // Empty string means no receiver filtering.
        public string ReceiverFilter
        {
            get => Preferences.Get(ReceiverFilterKey, string.Empty);
            set
            {
                var v = value ?? string.Empty;
                Preferences.Set(ReceiverFilterKey, v);
            }
        }

        // Optional talkgroup filter string.
        public string TalkgroupFilter
        {
            get => Preferences.Get(TalkgroupFilterKey, string.Empty);
            set
            {
                var v = value ?? string.Empty;
                Preferences.Set(TalkgroupFilterKey, v);
            }
        }

        // Theme preference:
        //   "System" = follow device theme
        //   "Light"  = force light mode
        //   "Dark"   = force dark mode
        public string ThemeMode
        {
            get => Preferences.Get(ThemeModeKey, "System");
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? "System" : value;
                Preferences.Set(ThemeModeKey, v);
            }
        }
    }
}
