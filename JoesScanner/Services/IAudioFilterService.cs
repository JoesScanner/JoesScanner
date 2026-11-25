namespace JoesScanner.Services
{
    public interface IAudioFilterService
    {
        // Analyze local playback media using the current filter settings.
        Task<PreparedAudio> PrepareForPlaybackAsync(string audioUrl, CancellationToken cancellationToken = default);
    }
}
