#if !ANDROID && !IOS && !MACCATALYST
namespace JoesScanner.Services
{
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
