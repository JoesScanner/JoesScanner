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


        // Per-server stored credentials (used for custom servers and Joe's hosted server account creds).
        // Validate is responsible for persisting credentials. Play/connect should read from storage only.
        bool TryGetServerCredentials(string serverUrl, out string username, out string password);
        void SetServerCredentials(string serverUrl, string username, string password);
        void ClearServerCredentials(string serverUrl);

        // Last non-empty username used for Auth API checks. Helps features that need to know
        // which account was used even when the scanner stream itself uses a service account.
        string LastAuthUsername { get; set; }

        bool AutoPlay { get; set; }

        // Windows-only behavior: when enabled, the Windows app will automatically connect
        // and start monitoring on app launch (if the server URL is valid and credentials
        // are present for Joe's hosted server).
        bool WindowsAutoConnectOnStart { get; set; }



        // Mobile and Mac behavior: when enabled, the app will automatically connect
        // and start monitoring on app launch (if the server URL is valid and credentials
        // are present for Joe's hosted server).
        bool MobileAutoConnectOnStart { get; set; }
// Windows-only behavior: when enabled, the Windows app will start automatically
        // when the user signs in to Windows.
        bool WindowsStartWithWindows { get; set; }

        string ScrollDirection { get; set; }

        string ReceiverFilter { get; set; }
        string TalkgroupFilter { get; set; }
        string DescriptionFilter { get; set; }

        string ThemeMode { get; set; }

        DateTime? SubscriptionLastCheckUtc { get; set; }
        bool SubscriptionLastStatusOk { get; set; }
        // True only when the app successfully contacted the Auth API and received an OK response.
        // This is used for strict feature gating where offline-cached access is not sufficient.
        bool SubscriptionLastValidatedOnline { get; set; }
        string SubscriptionLastLevel { get; set; }
        string SubscriptionPriceId { get; set; }

        // Subscription tier level returned by the Auth API.
        // Convention:
        // 0 = no access
        // 1 = standard
        // 2 = premium
        int SubscriptionTierLevel { get; set; }

        DateTime? SubscriptionExpiresUtc { get; set; }
        DateTime? SubscriptionRenewalUtc { get; set; }
        string SubscriptionLastMessage { get; set; }

        string DeviceInstallId { get; set; }
        string AuthSessionToken { get; set; }

        // Bluetooth and lock screen label mapping
        // Values are tokens: AppName, Transcription, Talkgroup, Site, Receiver
        string BluetoothLabelArtist { get; set; }
        string BluetoothLabelTitle { get; set; }
        string BluetoothLabelAlbum { get; set; }
        string BluetoothLabelComposer { get; set; }
        string BluetoothLabelGenre { get; set; }
    }
}
