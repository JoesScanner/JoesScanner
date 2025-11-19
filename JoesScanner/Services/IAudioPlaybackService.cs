using System.Threading;
using System.Threading.Tasks;

namespace JoesScanner.Services
{
    public interface IAudioPlaybackService
    {
        // Existing style call sites (no speed control)
        Task PlayAsync(string audioUrl, CancellationToken cancellationToken = default);

        // New overload with playback speed
        Task PlayAsync(string audioUrl, double playbackRate, CancellationToken cancellationToken = default);

        Task StopAsync();
    }
}
