#if IOS
using AVFoundation;
using CoreFoundation;
using CoreMedia;
using Foundation;
using JoesScanner.Platforms.iOS.Services;

namespace JoesScanner.Services;

public partial class AudioPlaybackService
{
    private readonly SemaphoreSlim _iosClipGate = new(1);

    private async Task PlayOnIosAsync(PreparedAudio prepared, double rate, CancellationToken token)
    {
        // A local source lets the player prepare and seek without holding audio
        // priority during network acquisition or AVPlayer stream buffering.
        string? downloaded = null;
        try
        {
            var url = new Uri(prepared.Url, UriKind.Absolute);
            if (url.Scheme is "http" or "https")
            {
                using var download = CancellationTokenSource.CreateLinkedTokenSource(token);
                download.CancelAfter(TimeSpan.FromSeconds(90));
                try
                {
                    downloaded = await DownloadToTempFileAsync(url.AbsoluteUri, download.Token).ConfigureAwait(false);
                    url = new Uri(downloaded);
                }
                catch (OperationCanceledException ex) when (download.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    throw new TimeoutException("iOS audio download timed out.", ex);
                }
            }

            await _iosClipGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await MainThread.InvokeOnMainThreadAsync(() => PlayIosClipOnMainThreadAsync(prepared, url, rate, token));
            }
            finally
            {
                _iosClipGate.Release();
            }
        }
        finally
        {
            if (downloaded != null)
                DeleteTemporaryAudio(downloaded);
        }
    }

    private async Task PlayIosClipOnMainThreadAsync(PreparedAudio prepared, Uri url, double rate, CancellationToken token)
    {
        using var source = url.IsFile ? NSUrl.FromFilename(url.LocalPath) : NSUrl.FromString(url.AbsoluteUri);
        if (source == null)
            throw new InvalidOperationException("iOS could not open the recording source.");

        var savedPosition = CMTime.Zero;
        while (true)
        {
            await WaitForIosAudioAsync(token);
            var generation = IosAudioEnvironment.MediaGeneration;
            using var item = new AVPlayerItem(source);
            using var player = new AVPlayer(item);
            using var observers = new IosPlayerObservers(item);
            if (!await PrepareIosPlayerAsync(observers, generation, token))
                continue;

            if (savedPosition.Seconds > 0)
            {
                try
                {
                    if (!await player.SeekAsync(savedPosition).WaitAsync(TimeSpan.FromSeconds(10), token))
                    {
                        savedPosition = CMTime.Zero;
                        continue;
                    }
                }
                catch (TimeoutException)
                {
                    savedPosition = CMTime.Zero;
                    continue;
                }
            }

            while (generation == IosAudioEnvironment.MediaGeneration)
            {
                await WaitForIosAudioAsync(token);
                if (generation != IosAudioEnvironment.MediaGeneration)
                    break;
                if (observers.ProcessFailure())
                    break;
                if (observers.Finished.IsCompletedSuccessfully)
                    return;

                var lease = IosAudioEnvironment.StartClip(_settings.MobileAudio.Mode);
                if (lease == 0)
                    continue;

                if (await PlayIosSegmentAsync(prepared, player, observers, rate, generation, lease, token))
                    return;

                if (generation == IosAudioEnvironment.MediaGeneration)
                    savedPosition = GetIosPosition(player);
            }
        }
    }

    private static async Task WaitForIosAudioAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (IosAudioEnvironment.IsBlocked)
            await IosAudioEnvironment.Changed.WaitAsync(token);
    }

    private static async Task<bool> PrepareIosPlayerAsync(IosPlayerObservers observers, int generation, CancellationToken token)
    {
        using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        readyTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            while (!observers.Ready.IsCompleted && !observers.Failure.IsCompleted &&
                   generation == IosAudioEnvironment.MediaGeneration)
            {
                await Task.WhenAny(observers.Ready, observers.Failure, IosAudioEnvironment.Changed)
                    .WaitAsync(readyTimeout.Token);
            }
        }
        catch (OperationCanceledException ex) when (readyTimeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException("iOS audio preparation timed out.", ex);
        }

        return generation == IosAudioEnvironment.MediaGeneration && !observers.ProcessFailure();
    }

    private async Task<bool> PlayIosSegmentAsync(PreparedAudio prepared, AVPlayer player, IosPlayerObservers observers,
        double rate, int generation, long lease, CancellationToken token)
    {
        var route = IosAudioEnvironment.RouteGeneration;
        var suspended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var volumeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? volume = null;
        bool stopped = false;
        bool wasPlaying = false;

        void StopSegment()
        {
            if (stopped)
                return;
            stopped = true;
            volumeCts.Cancel();
            try
            {
                if (generation == IosAudioEnvironment.MediaGeneration)
                    player.Pause();
            }
            finally { IosAudioEnvironment.ReleaseClip(lease); }
        }

        void OnAudioStateChanged()
        {
            if (IosAudioEnvironment.IsBlocked || generation != IosAudioEnvironment.MediaGeneration ||
                route != IosAudioEnvironment.RouteGeneration)
            {
                try { StopSegment(); }
                finally { suspended.TrySetResult(); }
            }
        }

        IosAudioEnvironment.PlaybackStateChanged += OnAudioStateChanged;
        try
        {
            token.ThrowIfCancellationRequested();
            if (IosAudioEnvironment.IsBlocked)
                return false;

            using var rateObserver = player.AddObserver("timeControlStatus", NSKeyValueObservingOptions.New, _ =>
                DispatchQueue.MainQueue.DispatchAsync(() =>
                {
                    if (stopped || observers.Finished.IsCompleted || observers.Failure.IsCompleted)
                        return;
                    if (player.TimeControlStatus == AVPlayerTimeControlStatus.Playing)
                    {
                        wasPlaying = true;
                    }
                    else if (wasPlaying && player.TimeControlStatus == AVPlayerTimeControlStatus.Paused)
                    {
                        // AVPlayer can pause before iOS posts an interruption or
                        // route event. Let that event resolve the pause if it arrives.
                        var durationSeconds = player.CurrentItem?.Duration.Seconds ?? 0;
                        var positionSeconds = player.CurrentTime.Seconds;
                        if (double.IsFinite(durationSeconds) && durationSeconds > 0 &&
                            double.IsFinite(positionSeconds) && durationSeconds - positionSeconds > 0.1)
                            IosAudioEnvironment.HandleUnreportedPause();
                    }
                }));
            player.PlayImmediatelyAtRate((float)rate);
            wasPlaying |= player.TimeControlStatus == AVPlayerTimeControlStatus.Playing;
            await _systemMediaService.SetClipPlaybackStateAsync(true, true, "IosClipStarted");
            if (prepared.StaticFilterEnabled || (prepared.ToneFilterEnabled && prepared.ToneDuckSegments.Count > 0))
                volume = ApplyDynamicVolumeAppleAsync(prepared, player, volumeCts.Token);

            var duration = TryGetAppleDurationMs(player) / 1000d;
            var limit = TimeSpan.FromSeconds(Math.Clamp((duration / rate * 2) + 120, 180, 10800));
            await Task.WhenAny(observers.Finished, observers.Failure, suspended.Task).WaitAsync(limit, token);
            token.ThrowIfCancellationRequested();

            // Only AVPlayer's end-of-item notification advances the queue. An
            // interruption, route change or device reset leaves this item selected.
            return generation == IosAudioEnvironment.MediaGeneration && !observers.ProcessFailure() &&
                observers.Finished.IsCompletedSuccessfully;
        }
        finally
        {
            IosAudioEnvironment.PlaybackStateChanged -= OnAudioStateChanged;
            try { StopSegment(); }
            finally
            {
                await _systemMediaService.SetClipPlaybackStateAsync(false, true, "IosClipIdle");
                if (volume != null)
                    await volume;
            }
        }
    }

    private static CMTime GetIosPosition(AVPlayer player)
    {
        var time = player.CurrentTime;
        return double.IsFinite(time.Seconds) && time.Seconds > 0 ? time : CMTime.Zero;
    }

    private sealed class IosPlayerObservers : IDisposable
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<NSError?> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _status;
        private readonly NSObject _end;
        private readonly NSObject _failed;
        internal Task Ready => _ready.Task;
        internal Task Finished => _finished.Task;
        internal Task<NSError?> Failure => _failure.Task;

        internal IosPlayerObservers(AVPlayerItem item)
        {
            var center = NSNotificationCenter.DefaultCenter;
            _end = center.AddObserver(AVPlayerItem.DidPlayToEndTimeNotification, _ => _finished.TrySetResult(), item);
            _failed = center.AddObserver(AVPlayerItem.ItemFailedToPlayToEndTimeNotification, e =>
                _failure.TrySetResult(e.UserInfo?[AVPlayerItem.ItemFailedToPlayToEndTimeErrorKey] as NSError ?? item.Error), item);
            _status = item.AddObserver("status", NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New, _ =>
            {
                if (item.Status == AVPlayerItemStatus.ReadyToPlay)
                    _ready.TrySetResult();
                else if (item.Status == AVPlayerItemStatus.Failed)
                    _failure.TrySetResult(item.Error);
            });
        }

        internal bool ProcessFailure()
        {
            if (!Failure.IsCompletedSuccessfully)
                return false;

            var error = Failure.Result;
            if (error?.Code == (nint)(long)AVError.MediaServicesWereReset)
            {
                IosAudioEnvironment.HandlePlayerMediaServicesLoss();
                return true;
            }
            throw new InvalidOperationException($"iOS audio playback failed ({error?.Code}).");
        }

        public void Dispose()
        {
            _status.Dispose();
            NSNotificationCenter.DefaultCenter.RemoveObserver(_end);
            NSNotificationCenter.DefaultCenter.RemoveObserver(_failed);
            _end.Dispose();
            _failed.Dispose();
        }
    }
}
#endif
