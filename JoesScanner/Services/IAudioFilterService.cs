namespace JoesScanner.Services
{
    public interface IAudioFilterService
    {
        // Phase 2: playback preparation only.
        // If audio filters are disabled, returns the original audioUrl.
        // If enabled, ensures the audio is available as a local file and returns a file:// URL.
        Task<PreparedAudio> PrepareForPlaybackAsync(string audioUrl, CancellationToken cancellationToken = default);
    }
}
