namespace JoesScanner.Services
{
    public class SettingsService : ISettingsService
    {
        private const string ServerUrlKey = "ServerUrl";
        private const string AutoPlayKey = "AutoPlay";
        private const string MaxCallsKey = "MaxCalls";
        private const string ScrollDirectionKey = "ScrollDirection";
        private const string ReceiverFilterKey = "ReceiverFilter";
        private const string TalkgroupFilterKey = "TalkgroupFilter";
        private const string ThemeModeKey = "ThemeMode";

        private const string DefaultServerUrl = "https://app.joesscanner.com";

        public string ServerUrl
        {
            get => Preferences.Get(ServerUrlKey, DefaultServerUrl);
            set => Preferences.Set(ServerUrlKey, string.IsNullOrWhiteSpace(value) ? DefaultServerUrl : value);
        }

        public bool AutoPlay
        {
            get => Preferences.Get(AutoPlayKey, false);
            set => Preferences.Set(AutoPlayKey, value);
        }

        public int MaxCalls
        {
            get => Preferences.Get(MaxCallsKey, 20);
            set
            {
                var v = value;
                if (v < 10) v = 10;
                if (v > 50) v = 50;
                Preferences.Set(MaxCallsKey, v);
            }
        }

        public string ScrollDirection
        {
            get => Preferences.Get(ScrollDirectionKey, "Down");
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? "Down" : value;
                Preferences.Set(ScrollDirectionKey, v);
            }
        }

        public string ReceiverFilter
        {
            get => Preferences.Get(ReceiverFilterKey, string.Empty);
            set
            {
                var v = value ?? string.Empty;
                Preferences.Set(ReceiverFilterKey, v);
            }
        }

        public string TalkgroupFilter
        {
            get => Preferences.Get(TalkgroupFilterKey, string.Empty);
            set
            {
                var v = value ?? string.Empty;
                Preferences.Set(TalkgroupFilterKey, v);
            }
        }

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
