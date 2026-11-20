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
    private enum IosSegmentResult { Completed, Suspended, Stalled }

    private async Task PlayOnIosAsync(PreparedAudio prepared, double rate, CancellationToken token, PlaybackCompletion completion)
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
                await MainThread.InvokeOnMainThreadAsync(() => PlayIosClipOnMainThreadAsync(prepared, url, rate, token, completion));
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

    private async Task PlayIosClipOnMainThreadAsync(PreparedAudio prepared, Uri url, double rate, CancellationToken token,
        PlaybackCompletion completion)
    {
        using var source = url.IsFile ? NSUrl.FromFilename(url.LocalPath) : NSUrl.FromString(url.AbsoluteUri);
        if (source == null)
            throw new InvalidOperationException("iOS could not open the recording source.");

        var recording = new IosRecordingState();
        while (!completion.Confirmed)
        {
            await WaitForIosAudioAsync(token);
            var generation = IosAudioEnvironment.MediaGeneration;
            using var item = new AVPlayerItem(source);
            using var player = new AVPlayer(item);
            using var observers = new IosPlayerObservers(item, completion, token);
            if (!await PrepareIosPlayerAsync(observers, generation, token))
                continue;

            while (generation == IosAudioEnvironment.MediaGeneration)
            {
                await IosAudioEnvironment.WaitForReleaseAsync(token);
                await WaitForIosAudioAsync(token, observers.Finished);
                if (observers.Finished.IsCompletedSuccessfully)
                    return;
                if (generation != IosAudioEnvironment.MediaGeneration)
                    break;
                if (observers.ProcessFailure())
                    break;

                if (recording.SeekTarget(player.CurrentTime.Seconds) is { } seconds)
                {
                    var sought = await SeekForIosRecoveryAsync(player, seconds, token);
                    if (generation != IosAudioEnvironment.MediaGeneration)
                        break;
                    if (!sought || recording.SeekTarget(player.CurrentTime.Seconds) != null)
                    {
                        // A fresh item starts at zero even if the old player
                        // could not seek or report a useful position.
                        recording.Restart();
                        break;
                    }
                    if (IosAudioEnvironment.IsBlocked)
                        continue;
                }

                var mode = recording.ModeForActivation(_settings.MobileAudio.Mode);
                var lease = IosAudioEnvironment.StartClip(mode);
                if (lease == 0)
                    continue;
                recording.Activated(mode);

                var result = await PlayIosSegmentAsync(prepared, player, observers, recording, rate, generation, lease, token);
                if (result == IosSegmentResult.Completed)
                    return;
                if (result == IosSegmentResult.Stalled)
                {
                    if (!recording.TryRecoverFromStall())
                        throw new TimeoutException("iOS audio stalled. Press Play to retry this recording.");
                    // Rebuild the player and seek from the last audible position.
                    // No session priority is held during the retry delay.
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    break;
                }
            }
        }
    }

    private static async Task<bool> SeekForIosRecoveryAsync(AVPlayer player, double seconds, CancellationToken token)
    {
        try
        {
            return await player.SeekAsync(CMTime.FromSeconds(seconds, 600)).WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task WaitForIosAudioAsync(CancellationToken token, Task? finished = null)
    {
        token.ThrowIfCancellationRequested();
        while (IosAudioEnvironment.IsBlocked && finished?.IsCompleted != true)
        {
            var changed = IosAudioEnvironment.Changed;
            await (finished == null ? changed : Task.WhenAny(changed, finished)).WaitAsync(token);
        }
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

    private async Task<IosSegmentResult> PlayIosSegmentAsync(PreparedAudio prepared, AVPlayer player, IosPlayerObservers observers,
        IosRecordingState recording, double rate, int generation, long lease, CancellationToken token)
    {
        var route = IosAudioEnvironment.RouteGeneration;
        var suspended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var volumeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? volume = null;
        NSObject? timeObserver = null;
        bool stopped = false;
        bool playIssued = false;

        void StopSegment()
        {
            if (stopped)
                return;
            stopped = true;
            volumeCts.Cancel();
            try
            {
                if (generation == IosAudioEnvironment.MediaGeneration)
                {
                    if (playIssued)
                        recording.Observe(player.CurrentTime.Seconds);
                    player.Pause();
                }
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

        void InspectNativePlayer()
        {
            if (!playIssued || stopped || token.IsCancellationRequested ||
                observers.Finished.IsCompleted || observers.Failure.IsCompleted)
                return;

            var seconds = player.CurrentTime.Seconds;
            recording.Observe(seconds);
            if (player.TimeControlStatus != AVPlayerTimeControlStatus.Paused)
                return;
            var duration = player.CurrentItem?.Duration.Seconds ?? 0;
            var atEnd = double.IsFinite(duration) && duration > 0 &&
                double.IsFinite(seconds) && duration - seconds <= 0.1;
            if (!atEnd)
                IosAudioEnvironment.HandleUnreportedPause(lease);
        }

        IosAudioEnvironment.PlaybackStateChanged += OnAudioStateChanged;
        IosAudioEnvironment.ReconcilePlayerState += InspectNativePlayer;
        try
        {
            token.ThrowIfCancellationRequested();
            if (observers.Finished.IsCompletedSuccessfully)
                return IosSegmentResult.Completed;
            if (IosAudioEnvironment.IsBlocked)
                return IosSegmentResult.Suspended;

            using var rateObserver = player.AddObserver("timeControlStatus", NSKeyValueObservingOptions.New, _ =>
                DispatchQueue.MainQueue.DispatchAsync(InspectNativePlayer));
            timeObserver = player.AddPeriodicTimeObserver(CMTime.FromSeconds(0.25, 600), DispatchQueue.MainQueue, time =>
            {
                if (!stopped && !token.IsCancellationRequested && generation == IosAudioEnvironment.MediaGeneration)
                    recording.Observe(time.Seconds);
            });
            var progress = new IosPlaybackProgress(player.CurrentTime.Seconds);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            playIssued = true;
            observers.Started();
            player.PlayImmediatelyAtRate((float)rate);
            await _systemMediaService.SetClipPlaybackStateAsync(true, true, "IosClipStarted");
            if (prepared.StaticFilterEnabled || (prepared.ToneFilterEnabled && prepared.ToneDuckSegments.Count > 0))
                volume = ApplyDynamicVolumeAppleAsync(prepared, player, volumeCts.Token);

            var duration = TryGetAppleDurationMs(player) / 1000d;
            var limit = TimeSpan.FromSeconds(Math.Clamp((duration / rate * 2) + 120, 180, 10800));
            var terminal = Task.WhenAny(observers.Finished, observers.Failure, suspended.Task);
            while (!terminal.IsCompleted)
            {
                await Task.WhenAny(terminal, Task.Delay(250, volumeCts.Token));
                token.ThrowIfCancellationRequested();
                if (terminal.IsCompleted)
                    break;

                var seconds = player.CurrentTime.Seconds;
                recording.Observe(seconds);
                var waiting = player.TimeControlStatus == AVPlayerTimeControlStatus.WaitingToPlayAtSpecifiedRate;
                if (progress.IsStalled(seconds, waiting, elapsed.Elapsed) && observers.TryRetireStalledItem())
                    return IosSegmentResult.Stalled;
                if (elapsed.Elapsed >= limit)
                    throw new TimeoutException("iOS audio playback timed out.");
            }
            token.ThrowIfCancellationRequested();

            // Only AVPlayer's end-of-item notification advances the queue. An
            // interruption, route change or device reset leaves this item selected.
            if (observers.Finished.IsCompletedSuccessfully)
                return IosSegmentResult.Completed;
            if (generation == IosAudioEnvironment.MediaGeneration)
                observers.ProcessFailure();
            return IosSegmentResult.Suspended;
        }
        finally
        {
            IosAudioEnvironment.PlaybackStateChanged -= OnAudioStateChanged;
            IosAudioEnvironment.ReconcilePlayerState -= InspectNativePlayer;
            try { StopSegment(); }
            finally
            {
                if (timeObserver != null)
                {
                    player.RemoveTimeObserver(timeObserver);
                    timeObserver.Dispose();
                }
                await _systemMediaService.SetClipPlaybackStateAsync(false, true, "IosClipIdle");
                if (volume != null)
                    await volume;
            }
        }
    }

    private sealed class IosPlayerObservers : IDisposable
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<NSError?> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PlaybackCompletion _itemCompletion = new();
        private int _started;
        private readonly IDisposable _status;
        private readonly NSObject _end;
        private readonly NSObject _failed;
        internal Task Ready => _ready.Task;
        internal Task Finished => _finished.Task;
        internal Task<NSError?> Failure => _failure.Task;

        internal IosPlayerObservers(AVPlayerItem item, PlaybackCompletion completion, CancellationToken token)
        {
            var center = NSNotificationCenter.DefaultCenter;
            _end = center.AddObserver(AVPlayerItem.DidPlayToEndTimeNotification, _ =>
            {
                if (Volatile.Read(ref _started) != 0 && _itemCompletion.Confirm(token) && completion.Confirm(token))
                    _finished.TrySetResult();
            }, item);
            _failed = center.AddObserver(AVPlayerItem.ItemFailedToPlayToEndTimeNotification, e =>
                Fail(e.UserInfo?[AVPlayerItem.ItemFailedToPlayToEndTimeErrorKey] as NSError ?? item.Error), item);
            _status = item.AddObserver("status", NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New, _ =>
            {
                if (item.Status == AVPlayerItemStatus.ReadyToPlay)
                    _ready.TrySetResult();
                else if (item.Status == AVPlayerItemStatus.Failed)
                    Fail(item.Error);
            });
        }

        internal void Started() => Volatile.Write(ref _started, 1);

        internal bool TryRetireStalledItem() => _itemCompletion.Reject();

        private void Fail(NSError? error)
        {
            if (_itemCompletion.Reject())
                _failure.TrySetResult(error);
        }

        internal bool ProcessFailure()
        {
            if (!Failure.IsCompletedSuccessfully)
                return false;

            var error = Failure.Result;
            if (error?.Code == (nint)(long)AVError.MediaServicesWereReset)
            {
                IosAudioEnvironment.HandleMediaServicesReset();
                return true;
            }
            throw new InvalidOperationException($"iOS audio playback failed ({error?.Code}).");
        }

        public void Dispose()
        {
            _itemCompletion.Reject();
            _status.Dispose();
            NSNotificationCenter.DefaultCenter.RemoveObserver(_end);
            NSNotificationCenter.DefaultCenter.RemoveObserver(_failed);
            _end.Dispose();
            _failed.Dispose();
        }
    }
}
#endif
