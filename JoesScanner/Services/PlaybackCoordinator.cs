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

            // Recorder scopes authorize this shared queue independently of the selected display recorder.
            LogDecision(normalizedReason, "allow", server, autoPlayEnabled, userInitiated, audioEnabled, isMainAudioSoftMuted, isAlreadyPlaying, isQueuePlaybackRunning, visibleQueueCount, isHosted);
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
