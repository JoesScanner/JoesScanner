using System.Windows.Input;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class SettingsViewModel
{
    private PlaybackAudioCache _audioCache = null!;
    private int _audioCacheLimitIndex;

    public int AudioCacheLimitIndex
    {
        get => _audioCacheLimitIndex;
        set
        {
            if (value >= 0 && value < PlaybackAudioCache.LimitsMiB.Count && value != _audioCacheLimitIndex)
                _ = ChangeAudioCacheLimitAsync(value);
        }
    }

    public string AudioCacheUsage => $"{_audioCache.UsageBytes / (double)PlaybackAudioCache.MiB:0.##} MB";
    public ICommand ClearAudioCacheCommand { get; private set; } = null!;
    public string AudioCacheError { get; private set; } = string.Empty;
    public bool HasAudioCacheError => AudioCacheError.Length != 0;

    private void InitializeAudioCache(PlaybackAudioCache cache)
    {
        _audioCache = cache;
        _audioCacheLimitIndex = Array.IndexOf(PlaybackAudioCache.LimitsMiB.ToArray(), PlaybackAudioCachePreferences.Read());
        ClearAudioCacheCommand = new Command(async () =>
        {
            try
            {
                await _audioCache.ClearAsync();
                SetAudioCacheError(string.Empty);
            }
            catch (Exception ex)
            {
                ReportAudioCacheError(ex, "Could not clear the audio cache. Try again.");
            }
        });
        _audioCache.UsageChanged += () => MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(AudioCacheUsage)));
    }

    private async Task ChangeAudioCacheLimitAsync(int index)
    {
        try
        {
            var limit = PlaybackAudioCache.LimitsMiB[index];
            PlaybackAudioCachePreferences.Save(limit);
            _audioCacheLimitIndex = index;
            OnPropertyChanged(nameof(AudioCacheLimitIndex));
            await _audioCache.SetLimitAsync(limit);
            SetAudioCacheError(string.Empty);
        }
        catch (Exception ex)
        {
            ReportAudioCacheError(ex, "Could not save the audio cache size. Try again.");
            OnPropertyChanged(nameof(AudioCacheLimitIndex));
        }
    }

    private void ReportAudioCacheError(Exception exception, string message)
    {
        AppLog.Add(() => $"Audio cache setting failed ({exception.GetType().Name}).");
        SetAudioCacheError(message);
    }

    private void SetAudioCacheError(string message)
    {
        AudioCacheError = message;
        OnPropertyChanged(nameof(AudioCacheError));
        OnPropertyChanged(nameof(HasAudioCacheError));
    }
}
