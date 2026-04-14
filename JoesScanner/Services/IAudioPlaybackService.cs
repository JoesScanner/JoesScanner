namespace JoesScanner.Services
{
    public interface IAudioPlaybackService
    {
        // Plays the specified audio URL at normal speed (1.0x).
        Task PlayAsync(string audioUrl, CancellationToken cancellationToken = default);

        // Plays the specified audio URL at the given playbackRate (for example 0.75x, 1.0x, 1.5x).
        Task PlayAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken = default);

        // Plays the specified audio URL at the given playbackRate and can optionally preserve Android
        // audio focus briefly after playback completes so an immediate follow-on clip can start without
        // a focus abandon/reacquire handoff gap. Other platforms ignore the preserve flag.
        Task PlayAsync(string audioUrl, double playbackRate, bool preserveAudioFocusForImmediateNextPlayback, CancellationToken cancellationToken = default);

        // Stops any active playback and releases underlying audio resources.
        Task StopAsync();
    }
}
