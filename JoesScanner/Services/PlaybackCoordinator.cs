using System;

namespace JoesScanner.Services
{
    public sealed class PlaybackCoordinator : IPlaybackCoordinator
    {
        private readonly ISettingsService _settings;

        public PlaybackCoordinator(ISettingsService settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public bool CanStartQueuePlayback(
            string reason,
            string? serverUrl,
            bool userInitiated,
            bool isRunning,
            bool audioEnabled,
            bool isMainAudioSoftMuted,
            bool isAlreadyPlaying,
            bool isQueuePlaybackRunning,
            int visibleQueueCount)
        {
            var normalizedReason = (reason ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedReason))
                normalizedReason = "unknown";

            var server = (serverUrl ?? string.Empty).Trim();
            Uri? serverUri = null;
            var hasServerUrl = !string.IsNullOrWhiteSpace(server) && Uri.TryCreate(server, UriKind.Absolute, out serverUri);
            var isHosted = hasServerUrl && serverUri != null && string.Equals(serverUri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);

            var autoPlayEnabled = _settings.AutoPlay;

            // Basic invariant checks.
            if (!isRunning)
                return Deny(normalizedReason, "not_running", server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);

            if (!audioEnabled)
                return Deny(normalizedReason, "audio_disabled", server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);

            if (isMainAudioSoftMuted)
                return Deny(normalizedReason, "soft_muted", server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);

            if (!hasServerUrl)
                return Deny(normalizedReason, "missing_or_invalid_server_url", server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);

            // Queue playback is never started implicitly unless AutoPlay is enabled.
            if (!userInitiated && !autoPlayEnabled)
                return Deny(normalizedReason, "autoplay_off", server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);

            if (visibleQueueCount <= 0)
                return Deny(normalizedReason, "queue_empty", server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);

            // Hosted Joe's server additional policy: only allow queue playback if the account is verified and active.
            if (isHosted && !IsHostedAccountVerifiedActive(server))
            {
                return Deny(normalizedReason, "hosted_not_verified_active", server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);
            }

            // Allowed.
            LogDecision(normalizedReason, "allow", server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);
            return true;
        }

        private bool IsHostedAccountVerifiedActive(string serverUrl)
        {
            // Tier 0 is no-access for Joe's hosted server.
            if (_settings.SubscriptionTierLevel < 1)
                return false;

            var user = (_settings.BasicAuthUsername ?? string.Empty).Trim();
            var pass = (_settings.BasicAuthPassword ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                return false;

            // Must match credentials that were stored after a successful Validate.
            if (!_settings.TryGetServerCredentials(serverUrl, out var cachedUser, out var cachedPass))
                return false;

            if (!string.Equals(user, cachedUser, StringComparison.Ordinal) ||
                !string.Equals(pass, cachedPass, StringComparison.Ordinal))
                return false;

            // Must have a successful subscription check and not be expired.
            if (!_settings.SubscriptionLastStatusOk)
                return false;

            var expiresUtc = _settings.SubscriptionExpiresUtc;
            if (expiresUtc.HasValue && expiresUtc.Value <= DateTime.UtcNow)
                return false;

            return true;
        }

        private static bool Deny(
            string reason,
            string denyReason,
            string server,
            bool autoPlayEnabled,
            bool userInitiated,
            bool audioEnabled,
            bool isMainAudioSoftMuted,
            bool isAlreadyPlaying,
            bool isQueuePlaybackRunning,
            int visibleQueueCount,
            bool isHosted)
        {
            LogDecision(reason, "deny:" + denyReason, server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);
            return false;
        }

        private static void LogDecision(
            string reason,
            string decision,
            string server,
            bool autoPlayEnabled,
            bool userInitiated,
            bool audioEnabled,
            bool isMainAudioSoftMuted,
            bool isAlreadyPlaying,
            bool isQueuePlaybackRunning,
            int visibleQueueCount,
            bool isHosted)
        {
            try
            {
                AppLog.Add(() => "PlaybackDecision: " +
                    $"reason={reason} decision={decision} " +
                    $"server='{server}' hosted={isHosted} " +
                    $"autoplay={autoPlayEnabled} audio={audioEnabled} " +
                    $"softMuted={isMainAudioSoftMuted} " +
                    $"alreadyPlaying={isAlreadyPlaying} queueRunning={isQueuePlaybackRunning} " +
                    $"queueCount={visibleQueueCount} userInitiated={userInitiated}");
            }
            catch
            {
            }
        }
    }
}