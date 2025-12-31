using System.Globalization;
using Microsoft.Maui.Storage;

namespace JoesScanner.Services
{
    // Concrete implementation of ISettingsService backed by Preferences storage.
    // Provides typed getters and setters for all user-configurable settings.
    public class SettingsService : ISettingsService
    {
        // Preference keys.
        private const string AuthServerBaseUrlKey = "AuthServerBaseUrl";
        private const string ServerUrlKey = "ServerUrl";
        private const string BasicAuthUserKey = "BasicAuthUser";
        private const string BasicAuthPassKey = "BasicAuthPass";
        private const string AutoPlayKey = "AutoPlay";
        private const string ScrollDirectionKey = "ScrollDirection";
        private const string ReceiverFilterKey = "ReceiverFilter";
        private const string TalkgroupFilterKey = "TalkgroupFilter";
        private const string DescriptionFilterKey = "DescriptionFilter";
        private const string ThemeModeKey = "ThemeMode";

        private const string SubscriptionLastCheckKey = "SubscriptionLastCheckUtc";
        private const string SubscriptionLastStatusOkKey = "SubscriptionLastStatusOk";
        private const string SubscriptionLastLevelKey = "SubscriptionLastLevel";
        private const string SubscriptionExpiresUtcKey = "SubscriptionExpiresUtc";
        private const string SubscriptionRenewalUtcKey = "SubscriptionRenewalUtc";
        private const string SubscriptionLastMessageKey = "SubscriptionLastMessage";
        private const string SubscriptionPriceIdKey = "SubscriptionPriceId";

        // Device and session tracking keys.
        private const string DeviceInstallIdKey = "DeviceInstallId";
        private const string AuthSessionTokenKey = "AuthSessionToken";

        public string SubscriptionPriceId
        {
            get => Preferences.Get(SubscriptionPriceIdKey, string.Empty);
            set => Preferences.Set(SubscriptionPriceIdKey, (value ?? string.Empty).Trim());
        }

        public string AuthServerBaseUrl
        {
            get => Preferences.Get(AuthServerBaseUrlKey, string.Empty);
            set => Preferences.Set(AuthServerBaseUrlKey, (value ?? string.Empty).Trim());
        }

        public string ServerUrl
        {
            get => Preferences.Get(ServerUrlKey, string.Empty);
            set => Preferences.Set(ServerUrlKey, (value ?? string.Empty).Trim());
        }

        public string BasicAuthUsername
        {
            get => Preferences.Get(BasicAuthUserKey, string.Empty);
            set => Preferences.Set(BasicAuthUserKey, (value ?? string.Empty).Trim());
        }

        public string BasicAuthPassword
        {
            get => Preferences.Get(BasicAuthPassKey, string.Empty);
            set => Preferences.Set(BasicAuthPassKey, (value ?? string.Empty).Trim());
        }

        public bool AutoPlay
        {
            get => Preferences.Get(AutoPlayKey, true);
            set => Preferences.Set(AutoPlayKey, value);
        }

        public string ScrollDirection
        {
            get => Preferences.Get(ScrollDirectionKey, "Down");
            set => Preferences.Set(ScrollDirectionKey, (value ?? "Down").Trim());
        }

        public string ReceiverFilter
        {
            get => Preferences.Get(ReceiverFilterKey, string.Empty);
            set => Preferences.Set(ReceiverFilterKey, (value ?? string.Empty).Trim());
        }

        public string TalkgroupFilter
        {
            get => Preferences.Get(TalkgroupFilterKey, string.Empty);
            set => Preferences.Set(TalkgroupFilterKey, (value ?? string.Empty).Trim());
        }

        public string DescriptionFilter
        {
            get => Preferences.Get(DescriptionFilterKey, string.Empty);
            set => Preferences.Set(DescriptionFilterKey, (value ?? string.Empty).Trim());
        }

        public string ThemeMode
        {
            get => Preferences.Get(ThemeModeKey, "System");
            set => Preferences.Set(ThemeModeKey, (value ?? "System").Trim());
        }

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
                if (value.HasValue)
                    Preferences.Set(SubscriptionLastCheckKey, value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                else
                    Preferences.Remove(SubscriptionLastCheckKey);
            }
        }

        public bool SubscriptionLastStatusOk
        {
            get => Preferences.Get(SubscriptionLastStatusOkKey, false);
            set => Preferences.Set(SubscriptionLastStatusOkKey, value);
        }

        public string SubscriptionLastLevel
        {
            get => Preferences.Get(SubscriptionLastLevelKey, string.Empty);
            set => Preferences.Set(SubscriptionLastLevelKey, (value ?? string.Empty).Trim());
        }

        public DateTime? SubscriptionExpiresUtc
        {
            get
            {
                var raw = Preferences.Get(SubscriptionExpiresUtcKey, string.Empty);
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
                if (value.HasValue)
                    Preferences.Set(SubscriptionExpiresUtcKey, value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                else
                    Preferences.Remove(SubscriptionExpiresUtcKey);
            }
        }

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
                if (value.HasValue)
                    Preferences.Set(SubscriptionRenewalUtcKey, value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                else
                    Preferences.Remove(SubscriptionRenewalUtcKey);
            }
        }

        public string SubscriptionLastMessage
        {
            get => Preferences.Get(SubscriptionLastMessageKey, string.Empty);
            set => Preferences.Set(SubscriptionLastMessageKey, (value ?? string.Empty).Trim());
        }

        public string DeviceInstallId
        {
            get => Preferences.Get(DeviceInstallIdKey, string.Empty);
            set => Preferences.Set(DeviceInstallIdKey, (value ?? string.Empty).Trim());
        }

        public string AuthSessionToken
        {
            get => Preferences.Get(AuthSessionTokenKey, string.Empty);
            set => Preferences.Set(AuthSessionTokenKey, (value ?? string.Empty).Trim());
        }
    }
}
