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

        // When enabled on mobile platforms, Joe's Scanner will request audio playback that can
        // continue while other apps are also playing audio.
        bool MobileMixAudioWithOtherApps { get; set; }

        // what3words
        bool What3WordsLinksEnabled { get; set; }

        string What3WordsApiKey { get; set; }

        // Address detection
        bool AddressDetectionEnabled { get; set; }

        bool AddressDetectionOpenMapsOnTap { get; set; }

        // Address detection tunings
        // NOTE: These are tuning knobs only. Address detection feature wiring is handled elsewhere.
        // Values are persisted locally and can be adjusted without server validation.
        int AddressDetectionMinConfidencePercent { get; set; }     // 0 to 100
        int AddressDetectionMinAddressChars { get; set; }          // 0 to 200
        int AddressDetectionMaxCandidatesPerCall { get; set; }     // 1 to 10


        // Audio filters (Phase 1 settings only)
        bool AudioStaticFilterEnabled { get; set; }
        int AudioStaticAttenuatorVolume { get; set; }       // 0 to 100 (0 disables attenuation)

        bool AudioToneFilterEnabled { get; set; }
        int AudioToneStrength { get; set; }        // 0 to 100

        // Tone detection sensitivity. Higher values detect tones more easily.
        // Range: 0 to 100. Default: 50.
        int AudioToneSensitivity { get; set; }

        // How long (in minutes) a talkgroup stays highlighted after a tone-only call is detected.
        // Range: 1 to 30. Default: 5.
        int AudioToneHighlightMinutes { get; set; }


        // Telemetry
        // When false (and a custom server is in use), the app should not send any
        // phone-home telemetry such as pings, heartbeats, or queued telemetry events.
        // This setting is ignored for built-in default servers.
        bool TelemetryEnabled { get; set; }


        // Server directory cache (fetched from the WordPress Auth plugin).
        // Used for server selection UI and per-server map anchor resolution.
        System.Collections.Generic.IReadOnlyList<JoesScanner.Models.ServerDirectoryEntry> GetCachedDirectoryServers();
        void UpsertCachedDirectoryServers(System.Collections.Generic.IEnumerable<JoesScanner.Models.ServerDirectoryEntry> servers);
        bool TryGetMapAnchorForServerUrl(string serverUrl, out string mapAnchor);


    }
}
