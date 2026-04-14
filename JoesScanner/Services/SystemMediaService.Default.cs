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

        private partial void PlatformUpdateNowPlaying(NowPlayingMetadata metadata, bool audioEnabled)
        {
        }

        private partial Task PlatformRefreshAudioSessionAsync(bool audioEnabled, string reason)
        {
            return Task.CompletedTask;
        }

        private partial Task PlatformSetClipPlaybackStateAsync(bool isPlaying, bool audioEnabled, string reason)
        {
            return Task.CompletedTask;
        }

        private partial void PlatformClear()
        {
        }
    }
}
#endif
