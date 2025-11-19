using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using JoesScanner.Services;
using Microsoft.Maui.Controls;

namespace JoesScanner.ViewModels;

public class CallItemViewModel : BaseViewModel
{
    private readonly IAudioCacheService _audioCacheService;
    private readonly IAudioPlaybackService _audioPlaybackService;

    public CallItemViewModel(
        IAudioCacheService audioCacheService,
        IAudioPlaybackService audioPlaybackService,
        DateTime timestamp,
        string talkgroup,
        string source,
        string transcription,
        string audioUrl)
    {
        _audioCacheService = audioCacheService;
        _audioPlaybackService = audioPlaybackService;

        Timestamp = timestamp;
        Talkgroup = talkgroup;
        Source = source;
        Transcription = transcription;
        AudioUrl = audioUrl;

        PlayCommand = new Command(async () => await PlayAsync());
    }

    public DateTime Timestamp { get; }
    public string Talkgroup { get; }
    public string Source { get; }
    public string Transcription { get; }
    public string AudioUrl { get; }

    private string? _localAudioPath;
    public string? LocalAudioPath
    {
        get => _localAudioPath;
        set => SetProperty(ref _localAudioPath, value);
    }

    public string HeaderLine => $"{Timestamp:HH:mm:ss}  {Talkgroup}";
    public string SubHeaderLine => Source;

    public ICommand PlayCommand { get; }

    private async Task PlayAsync()
    {
        if (string.IsNullOrWhiteSpace(AudioUrl))
            return;

        if (string.IsNullOrWhiteSpace(LocalAudioPath))
            LocalAudioPath = await _audioCacheService.CacheAsync(AudioUrl, Timestamp, CancellationToken.None);

        if (!string.IsNullOrWhiteSpace(LocalAudioPath))
            await _audioPlaybackService.PlayAsync(LocalAudioPath, CancellationToken.None);
    }
}
