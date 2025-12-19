using System.Globalization;

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
        private const string AnnounceNewCallsKey = "AnnounceNewCalls";
        private const string MaxCallsKey = "MaxCalls";
        private const string AutoSpeedThresholdKey = "AutoSpeedThreshold";
        private const string ScrollDirectionKey = "ScrollDirection";
        private const string ReceiverFilterKey = "ReceiverFilter";
        private const string TalkgroupFilterKey = "TalkgroupFilter";
        private const string ThemeModeKey = "ThemeMode";

        // Subscription cache keys.
        private const string SubscriptionLastCheckKey = "SubscriptionLastCheckUtc";
        private const string SubscriptionLastStatusOkKey = "SubscriptionLastStatusOk";
        private const string SubscriptionLastLevelKey = "SubscriptionLastLevel";
        private const string SubscriptionLastMessageKey = "SubscriptionLastMessage";
        private const string SubscriptionPriceIdKey = "SubscriptionPriceId";
        private const string SubscriptionRenewalUtcKey = "SubscriptionRenewalUtc";

        // Device and session tracking keys.
        private const string DeviceInstallIdKey = "DeviceInstallId";
        private const string AuthSessionTokenKey = "AuthSessionToken";

        // ========= Subscription cache fields =========

        // UTC time of the last subscription check, or null if never.
        public DateTime? SubscriptionLastCheckUtc
        {
            get
            {
                var raw = Preferences.Get(SubscriptionLastCheckKey, string.Empty);
                return DateTime.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var dt)
                    ? dt
                    : null;
            }
            set
            {
                var raw = value?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;
                Preferences.Set(SubscriptionLastCheckKey, raw);
            }
        }

        // True if the last check reported subscription ok and can be used for grace.
        public bool SubscriptionLastStatusOk
        {
            get => Preferences.Get(SubscriptionLastStatusOkKey, false);
            set => Preferences.Set(SubscriptionLastStatusOkKey, value);
        }

        // Human-readable subscription level label, for example "Joe's Scanner Monthly".
        public string SubscriptionLastLevel
        {
            get => Preferences.Get(SubscriptionLastLevelKey, string.Empty);
            set => Preferences.Set(SubscriptionLastLevelKey, value ?? string.Empty);
        }

        // Raw subscription price id from the server (for mapping and diagnostics).
        public string SubscriptionPriceId
        {
            get => Preferences.Get(SubscriptionPriceIdKey, string.Empty);
            set => Preferences.Set(SubscriptionPriceIdKey, value ?? string.Empty);
        }

        // Next renewal date/time in UTC.
        public DateTime? SubscriptionRenewalUtc
        {
            get
            {
                var raw = Preferences.Get(SubscriptionRenewalUtcKey, string.Empty);
                return DateTime.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var dt)
                    ? dt
                    : null;
            }
            set
            {
                var raw = value?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;
                Preferences.Set(SubscriptionRenewalUtcKey, raw);
            }
        }

        // Last human readable message from the subscription check.
        public string SubscriptionLastMessage
        {
            get => Preferences.Get(SubscriptionLastMessageKey, string.Empty);
            set => Preferences.Set(SubscriptionLastMessageKey, value ?? string.Empty);
        }

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

        // When true, announce new calls via the platform screen reader.
        public bool AnnounceNewCalls
        {
            get => Preferences.Get(AnnounceNewCallsKey, true);
            set => Preferences.Set(AnnounceNewCallsKey, value);
        }


        // Maximum number of calls to keep in the visible queue.
        // Value is clamped to a range of 10–50 before being stored.
        public int MaxCalls
        {
            get => Preferences.Get(MaxCallsKey, 10); // default 10
            set
            {
                var v = value;

                // Clamp to 10–50.
                if (v < 10) v = 10;
                if (v > 50) v = 50;

                Preferences.Set(MaxCallsKey, v);
            }
        }

        // Autospeed threshold in calls waiting for automatic playback speed increases.
        // Value is clamped to 10–100 before being stored.
        public int AutoSpeedThreshold
        {
            get
            {
                var v = Preferences.Get(AutoSpeedThresholdKey, 10);
                if (v < 10) v = 10;
                if (v > 100) v = 100;
                return v;
            }
            set
            {
                var v = value;
                if (v < 10) v = 10;
                if (v > 100) v = 100;
                Preferences.Set(AutoSpeedThresholdKey, v);
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

        // Stable per-install identifier. Used by the auth API for session reporting.
        public string DeviceInstallId
        {
            get
            {
                var v = Preferences.Get(DeviceInstallIdKey, string.Empty);
                if (!string.IsNullOrWhiteSpace(v))
                    return v;

                v = Guid.NewGuid().ToString("N");
                Preferences.Set(DeviceInstallIdKey, v);
                return v;
            }
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
                Preferences.Set(DeviceInstallIdKey, v);
            }
        }

        // Optional session token returned by the auth API. Used for heartbeat pings.
        public string AuthSessionToken
        {
            get => Preferences.Get(AuthSessionTokenKey, string.Empty);
            set => Preferences.Set(AuthSessionTokenKey, value ?? string.Empty);
        }
    }
}
