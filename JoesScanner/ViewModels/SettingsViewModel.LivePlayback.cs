using System.Globalization;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class SettingsViewModel
{
    public int LivePlaybackSourceModeIndex
    {
        get => (int)_mainViewModel.LivePlaybackOptions.SourceMode;
        set
        {
            if (value is 0 or 1)
                SaveLivePlayback(_mainViewModel.LivePlaybackOptions with { SourceMode = (LiveAudioSourceMode)value },
                    nameof(LivePlaybackSourceModeIndex));
        }
    }

    public bool GroupLiveCalls
    {
        get => _mainViewModel.LivePlaybackOptions.GroupCalls;
        set => SaveLivePlayback(_mainViewModel.LivePlaybackOptions with { GroupCalls = value }, nameof(GroupLiveCalls));
    }

    public bool PrioritizeLiveFavorites
    {
        get => _mainViewModel.LivePlaybackOptions.PrioritizeFavorites;
        set => SaveLivePlayback(_mainViewModel.LivePlaybackOptions with { PrioritizeFavorites = value }, nameof(PrioritizeLiveFavorites));
    }

    public int ConsecutiveLiveCallLimitIndex
    {
        get => _mainViewModel.LivePlaybackOptions.ConsecutiveLimit - 1;
        set
        {
            if (value >= 0 && value < LivePlaybackPreferences.MaximumConsecutive)
                SaveLivePlayback(_mainViewModel.LivePlaybackOptions with { ConsecutiveLimit = value + 1 },
                    nameof(ConsecutiveLiveCallLimitIndex));
        }
    }

    public string LiveBacklogWarningThreshold
    {
        get => _mainViewModel.LivePlaybackOptions.WarningThreshold.ToString(CultureInfo.InvariantCulture);
        set => SavePlaybackThreshold(value, warning: true, nameof(LiveBacklogWarningThreshold));
    }

    public string LiveBacklogJumpThreshold
    {
        get => _mainViewModel.LivePlaybackOptions.JumpThreshold.ToString(CultureInfo.InvariantCulture);
        set => SavePlaybackThreshold(value, warning: false, nameof(LiveBacklogJumpThreshold));
    }

    private const int CustomLagOptionIndex = PlaybackLagRecoveryPreference.MaximumPreset + 2;

    public string[] VerifiedLagOptions { get; } =
        ["Immediately", "Never", .. Enumerable.Range(1, PlaybackLagRecoveryPreference.MaximumPreset)
            .Select(minutes => minutes == 1 ? "1 minute" : $"{minutes} minutes"), "Custom"];

    public int VerifiedLagOptionIndex
    {
        get => _mainViewModel.LivePlaybackOptions.VerifiedLagRecovery.Minutes switch
        {
            PlaybackLagRecoveryPreference.Immediately => 0,
            PlaybackLagRecoveryPreference.Never => 1,
            PlaybackLagRecoveryPreference.Custom => CustomLagOptionIndex,
            var preset => preset + 1
        };
        set
        {
            if (value is < 0 or > CustomLagOptionIndex)
                return;

            var options = _mainViewModel.LivePlaybackOptions;
            var minutes = value switch
            {
                0 => PlaybackLagRecoveryPreference.Immediately,
                1 => PlaybackLagRecoveryPreference.Never,
                CustomLagOptionIndex => PlaybackLagRecoveryPreference.Custom,
                _ => value - 1
            };
            SaveLivePlayback(options with { VerifiedLagRecovery = options.VerifiedLagRecovery with { Minutes = minutes } },
                nameof(VerifiedLagOptionIndex));
            OnPropertyChanged(nameof(ShowsCustomVerifiedLagMinutes));
            OnPropertyChanged(nameof(CustomVerifiedLagMinutes));
        }
    }

    public bool ShowsCustomVerifiedLagMinutes =>
        _mainViewModel.LivePlaybackOptions.VerifiedLagRecovery.Minutes == PlaybackLagRecoveryPreference.Custom;

    public string CustomVerifiedLagMinutes
    {
        get => _mainViewModel.LivePlaybackOptions.VerifiedLagRecovery.CustomMinutes.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
                !PlaybackLagRecoveryPreference.AllowsCustomMinutes(minutes))
            {
                SetLivePlaybackSettingsError("Enter a whole number of minutes from 1 to 1,440.");
                OnPropertyChanged();
                return;
            }

            var options = _mainViewModel.LivePlaybackOptions;
            SaveLivePlayback(options with { VerifiedLagRecovery = options.VerifiedLagRecovery with { CustomMinutes = minutes } },
                nameof(CustomVerifiedLagMinutes));
        }
    }

    public string LivePlaybackSettingsError { get; private set; } = string.Empty;
    public bool HasLivePlaybackSettingsError => LivePlaybackSettingsError.Length != 0;

    private void SavePlaybackThreshold(string value, bool warning, string property)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold) || threshold <= 0)
        {
            SetLivePlaybackSettingsError("Enter a positive whole number of ready recordings.");
            OnPropertyChanged(property);
            return;
        }

        var options = _mainViewModel.LivePlaybackOptions;
        SaveLivePlayback(warning ? options with { WarningThreshold = threshold } : options with { JumpThreshold = threshold }, property);
    }

    private void SaveLivePlayback(LivePlaybackPreferences options, string property)
    {
        try
        {
            if (options != _mainViewModel.LivePlaybackOptions)
                _mainViewModel.ApplyLivePlaybackOptions(options);
            SetLivePlaybackSettingsError(string.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Live playback preferences could not be saved ({ex.GetType().Name}).");
            SetLivePlaybackSettingsError("Could not save the playback setting. Try again.");
        }
        OnPropertyChanged(property);
    }

    private void SetLivePlaybackSettingsError(string value)
    {
        LivePlaybackSettingsError = value;
        OnPropertyChanged(nameof(LivePlaybackSettingsError));
        OnPropertyChanged(nameof(HasLivePlaybackSettingsError));
    }
}
