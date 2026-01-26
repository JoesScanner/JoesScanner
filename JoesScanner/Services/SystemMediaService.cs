namespace JoesScanner.Services
{
    // Cross platform entry point. Platform partial implementations do the real work.
    public partial class SystemMediaService : ISystemMediaService
    {
        private Func<Task>? _onPlay;
        private Func<Task>? _onStop;
        private Func<Task>? _onNext;
        private Func<Task>? _onPrevious;

        public void SetHandlers(Func<Task> onPlay, Func<Task> onStop, Func<Task>? onNext = null, Func<Task>? onPrevious = null)
        {
            _onPlay = onPlay;
            _onStop = onStop;
            _onNext = onNext;
            _onPrevious = onPrevious;

            PlatformSetHandlers(onPlay, onStop, onNext, onPrevious);
        }

        public Task StartSessionAsync(bool audioEnabled)
        {
            return PlatformStartSessionAsync(audioEnabled);
        }

        public Task StopSessionAsync()
        {
            return PlatformStopSessionAsync();
        }

        public void UpdateNowPlaying(string title, string subtitle, bool audioEnabled)
        {
            PlatformUpdateNowPlaying(title, subtitle, audioEnabled);
        }

        public void Clear()
        {
            PlatformClear();
        }

        internal async Task InvokePlayAsync()
        {
            var handler = _onPlay;
            if (handler == null)
                return;

            try
            {
                await handler();
            }
            catch
            {
            }
        }

        internal async Task InvokeStopAsync()
        {
            var handler = _onStop;
            if (handler == null)
                return;

            try
            {
                await handler();
            }
            catch
            {
            }
        }

        internal async Task InvokeNextAsync()
        {
            var handler = _onNext;
            if (handler == null)
                return;

            try
            {
                await handler();
            }
            catch
            {
            }
        }

        internal async Task InvokePreviousAsync()
        {
            var handler = _onPrevious;
            if (handler == null)
                return;

            try
            {
                await handler();
            }
            catch
            {
            }
        }

        // Platform hooks
        private partial void PlatformSetHandlers(Func<Task> onPlay, Func<Task> onStop, Func<Task>? onNext, Func<Task>? onPrevious);
        private partial Task PlatformStartSessionAsync(bool audioEnabled);
        private partial Task PlatformStopSessionAsync();
        private partial void PlatformUpdateNowPlaying(string title, string subtitle, bool audioEnabled);
        private partial void PlatformClear();
    }
}
