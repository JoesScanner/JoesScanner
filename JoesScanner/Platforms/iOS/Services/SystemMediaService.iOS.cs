#if IOS
using Foundation;
using JoesScanner.Models;
using JoesScanner.Platforms.iOS.Services;
using MediaPlayer;
using UIKit;

namespace JoesScanner.Services;

public partial class SystemMediaService
{
    private IDisposable? _iosMonitoringWatch;
    private readonly List<(MPRemoteCommand Command, NSObject Target)> _iosRemoteTargets = [];
    private NowPlayingMetadata _iosMetadata = new();
    private bool _iosPublished;
    private readonly MonitoringTransportOwnership _iosTransport = new();
    private long _iosMonitoringRunId;
    private MPMediaItemArtwork? _iosArtwork;

    private partial void PlatformSetHandlers(Func<Task> onPlay, Func<Task> onStop, Func<Task>? onNext, Func<Task>? onPrevious)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            var center = MPRemoteCommandCenter.Shared;
            foreach (var (command, target) in _iosRemoteTargets)
            {
                command.RemoveTarget(target);
                target.Dispose();
            }
            _iosRemoteTargets.Clear();

            Register(center.PlayCommand, InvokePlayAsync);
            Register(center.PauseCommand, InvokeStopAsync, stopsMonitoring: true);
            Register(center.StopCommand, InvokeStopAsync, stopsMonitoring: true);
            if (onNext != null)
                Register(center.NextTrackCommand, InvokeNextAsync);
            if (onPrevious != null)
                Register(center.PreviousTrackCommand, InvokePreviousAsync);
            SetRemoteCommands(false);
        });

    private void Register(MPRemoteCommand command, Func<Task> handler, bool stopsMonitoring = false)
    {
        var target = command.AddTarget(_ =>
        {
            var publication = _iosTransport.Capture();
            if (publication == null || publication.RunId == 0)
                return MPRemoteCommandHandlerStatus.NoActionableNowPlayingItem;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (_iosTransport.CanDispatch(publication, _iosMonitoringRunId, stopsMonitoring))
                    await handler();
            });
            return MPRemoteCommandHandlerStatus.Success;
        });
        if (target != null)
            _iosRemoteTargets.Add((command, target));
    }

    private partial Task PlatformStartSessionAsync(bool audioEnabled, long monitoringRunId) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            _ = _settings.MobileAudio;
            _iosMonitoringWatch ??= IosAudioEnvironment.Watch(_settings, monitoring: true);
            _iosMonitoringRunId = monitoringRunId;
        });

    private partial Task PlatformStopSessionAsync(long monitoringRunId) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (_iosMonitoringRunId != monitoringRunId)
            return;
        _iosMonitoringRunId = 0;
        _iosMonitoringWatch?.Dispose();
        _iosMonitoringWatch = null;
        _iosMetadata = new();
        ClearIosNowPlaying();
    });

    // Settings are snapshotted at the next activation boundary.
    private partial Task PlatformRefreshAudioSessionAsync(bool audioEnabled, string reason) => Task.CompletedTask;

    private partial void PlatformUpdateNowPlaying(NowPlayingMetadata metadata, bool audioEnabled)
        => MainThread.BeginInvokeOnMainThread(() => _iosMetadata = metadata);

    private partial Task PlatformSetClipPlaybackStateAsync(bool isPlaying, bool audioEnabled, string reason)
        => MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (!isPlaying || IosAudioEnvironment.ActiveMode != MobileAudioMode.Exclusive)
            {
                ClearIosNowPlaying();
                return;
            }

            if (_iosArtwork == null && UIImage.FromBundle("logo_button") is { } image)
                _iosArtwork = new MPMediaItemArtwork(image.Size, _ => image);

            var center = MPNowPlayingInfoCenter.DefaultCenter;
            center.NowPlaying = new MPNowPlayingInfo
            {
                Title = string.IsNullOrWhiteSpace(_iosMetadata.Title) ? "Joe's Scanner" : _iosMetadata.Title,
                Artist = _iosMetadata.Artist,
                AlbumTitle = _iosMetadata.Album,
                Composer = _iosMetadata.Composer,
                Genre = _iosMetadata.Genre,
                Artwork = _iosArtwork,
                PlaybackRate = 1,
                IsLiveStream = false
            };
            _iosPublished = true;
            _iosTransport.Publish(_iosMonitoringRunId);
            SetRemoteCommands(_iosMonitoringWatch != null);
        });

    private void SetRemoteCommands(bool enabled)
    {
        foreach (var (command, _) in _iosRemoteTargets)
            command.Enabled = enabled;
    }

    private void ClearIosNowPlaying()
    {
        if (!_iosPublished)
            return;

        _iosPublished = false;
        _iosTransport.Clear();
        SetRemoteCommands(false);
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null!; // MediaPlayer clears metadata with nil.
    }

    private partial void PlatformClear() => MainThread.BeginInvokeOnMainThread(() =>
    {
        _iosMetadata = new();
        ClearIosNowPlaying();
    });
}
#endif
