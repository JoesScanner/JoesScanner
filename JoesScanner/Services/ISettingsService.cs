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

        // When true, announce new calls via the platform screen reader.

        // Maximum number of calls to keep in the visible queue.

        // Threshold (in calls waiting) where automatic playback speed increases begin.
        // When the number of waiting calls reaches this value or higher, the app may
        // temporarily increase playback speed to help clear the backlog.

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

        // ========= Subscription cache fields =========

        // UTC time of the last subscription check, or null if never.
        DateTime? SubscriptionLastCheckUtc { get; set; }

        // True if the last check reported subscription ok and can be used for grace.
        bool SubscriptionLastStatusOk { get; set; }

        // Human-readable subscription level label, for example "Joe's Scanner Monthly".
        string SubscriptionLastLevel { get; set; }

        // Raw subscription price id from the server (for mapping and diagnostics).
        string SubscriptionPriceId { get; set; }

        // Next renewal date in UTC (used for UI and grace-period enforcement).
        DateTime? SubscriptionRenewalUtc { get; set; }

        // Last human readable message from the subscription check.
        string SubscriptionLastMessage { get; set; }

        // ========= Device and session tracking =========

        // Stable per-install identifier. Used by the auth API for session reporting.
        string DeviceInstallId { get; set; }

        // Optional session token returned by the auth API. Used for heartbeat pings.
        string AuthSessionToken { get; set; }
    }
}
