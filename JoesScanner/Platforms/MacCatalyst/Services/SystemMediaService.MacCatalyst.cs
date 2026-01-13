#if MACCATALYST
namespace JoesScanner.Services
{
    // Minimal MacCatalyst implementation to satisfy the shared partial hooks.
    // If you later want Now Playing integration on macOS, we can add it here.
    public partial class SystemMediaService
    {
        private partial void PlatformSetHandlers(Func<Task> onPlay, Func<Task> onStop, Func<Task>? onNext, Func<Task>? onPrevious)
        {
        }

        private partial Task PlatformStartSessionAsync(bool audioEnabled)
        {
            return Task.CompletedTask;
        }

        private partial Task PlatformStopSessionAsync()
        {
            return Task.CompletedTask;
        }

        private partial void PlatformUpdateNowPlaying(string title, string subtitle, bool audioEnabled)
        {
        }

        private partial void PlatformClear()
        {
        }
    }
}
#endif
