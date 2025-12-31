namespace JoesScanner.Services
{
    public interface ISettingsService
    {
        // Base URL for the WordPress Auth API server (ex: https://joesscanner.com)
        // Leave blank to use the default.
        string AuthServerBaseUrl { get; set; }

        // Base URL for the scanner server.
        string ServerUrl { get; set; }

        string BasicAuthUsername { get; set; }
        string BasicAuthPassword { get; set; }

        bool AutoPlay { get; set; }

        string ScrollDirection { get; set; }

        string ReceiverFilter { get; set; }
        string TalkgroupFilter { get; set; }
        string DescriptionFilter { get; set; }

        string ThemeMode { get; set; }

        DateTime? SubscriptionLastCheckUtc { get; set; }
        bool SubscriptionLastStatusOk { get; set; }
        string SubscriptionLastLevel { get; set; }
        string SubscriptionPriceId { get; set; }

        DateTime? SubscriptionExpiresUtc { get; set; }
        DateTime? SubscriptionRenewalUtc { get; set; }
        string SubscriptionLastMessage { get; set; }

        string DeviceInstallId { get; set; }
        string AuthSessionToken { get; set; }
    }
}
