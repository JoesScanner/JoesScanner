using System.IO;
using JoesScanner.Services;
using Microsoft.Maui.Dispatching;

namespace JoesScanner.Views
{
    public partial class LogPage : ContentPage
    {
        private const int MaxLines = 500;

        private static readonly IReadOnlyList<(string Label, TimeSpan Interval)> AutoRefreshIntervals =
        [
            ("1s", TimeSpan.FromSeconds(1)),
            ("3s", TimeSpan.FromSeconds(3)),
            ("5s", TimeSpan.FromSeconds(5)),
            ("10s", TimeSpan.FromSeconds(10)),
            ("30s", TimeSpan.FromSeconds(30))
        ];

        private bool _autoRefreshEnabled;
        private IDispatcherTimer? _autoRefreshTimer;
        private int _autoRefreshIntervalIndex = 2;

        public LogPage()
        {
            InitializeComponent();

            var enabled = AppLog.IsEnabled;
            LoggingSwitch.IsToggled = enabled;
            SyncLoggingState(enabled);

            SyncIntervalLabel();
            UpdateAutoRefreshButtonVisual();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();

            var enabled = AppLog.IsEnabled;
            LoggingSwitch.IsToggled = enabled;
            SyncLoggingState(enabled);
            RefreshLog();

            if (_autoRefreshEnabled)
            {
                StartAutoRefreshTimer();
            }
        }

        protected override void OnDisappearing()
        {
            StopAutoRefreshTimer();
            base.OnDisappearing();
        }

        private void RefreshLog()
        {
            try
            {
                if (!AppLog.IsEnabled)
                {
                    LogEditor.Text = "Logging is disabled.";
                    return;
                }

                var lines = AppLog.GetSnapshot(MaxLines);
                LogEditor.Text = string.Join(Environment.NewLine, lines);

                // Make it obvious the refresh occurred by keeping the view anchored at the bottom.
                // This also helps on iOS where the Editor may keep the previous scroll position.
                var textLen = LogEditor.Text?.Length ?? 0;
                if (textLen > 0)
                {
                    LogEditor.CursorPosition = textLen;
                    LogEditor.SelectionLength = 0;
                }
            }
            catch
            {
                LogEditor.Text = "Unable to load log.";
            }
        }

        // Back-compat: older XAML used OnRefreshClicked.
        private void OnRefreshClicked(object sender, EventArgs e)
        {
            OnLoadClicked(sender, e);
        }

        private void OnLoadClicked(object sender, EventArgs e)
        {
            RefreshLog();

            // If auto-refresh is enabled, restart so the next tick is aligned to "now".
            if (_autoRefreshEnabled)
            {
                StartAutoRefreshTimer();
            }
        }

        private async void OnExportClicked(object sender, EventArgs e)
        {
            try
            {
                if (!AppLog.IsEnabled)
                {
                    await DisplayAlertAsync("Logs", "Logging is disabled.", "OK");
                    return;
                }

                var zipPath = await AppLog.ExportLogsAsync(maxLogFiles: 3, snapshotMaxLines: MaxLines);
                if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                {
                    await DisplayAlertAsync("Logs", "Unable to export logs.", "OK");
                    return;
                }

                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = "Share logs",
                    File = new ShareFile(zipPath)
                });
            }
            catch
            {
                try
                {
                    await DisplayAlertAsync("Logs", "Unable to export logs.", "OK");
                }
                catch
                {
                }
            }
        }


        private void OnAutoRefreshClicked(object sender, EventArgs e)
        {
            _autoRefreshEnabled = !_autoRefreshEnabled;
            UpdateAutoRefreshButtonVisual();

            if (_autoRefreshEnabled)
            {
                StartAutoRefreshTimer();
            }
            else
            {
                StopAutoRefreshTimer();
            }
        }

        private void OnAutoRefreshIntervalTapped(object sender, TappedEventArgs e)
        {
            CycleAutoRefreshInterval();
        }

        private void StartAutoRefreshTimer()
        {
            StopAutoRefreshTimer();

            var interval = GetSelectedAutoRefreshInterval();
            _autoRefreshTimer = Dispatcher.CreateTimer();
            _autoRefreshTimer.Interval = interval;
            _autoRefreshTimer.Tick += OnAutoRefreshTick;
            _autoRefreshTimer.Start();
        }

        private void StopAutoRefreshTimer()
        {
            if (_autoRefreshTimer == null)
            {
                return;
            }

            try
            {
                if (!AppLog.IsEnabled)
                {
                    LogEditor.Text = "Logging is disabled.";
                    return;
                }

                _autoRefreshTimer.Tick -= OnAutoRefreshTick;
                _autoRefreshTimer.Stop();
            }
            catch
            {
                // Ignore.
            }
            finally
            {
                _autoRefreshTimer = null;
            }
        }

        private void OnAutoRefreshTick(object? sender, EventArgs e)
        {
            RefreshLog();
        }

        private TimeSpan GetSelectedAutoRefreshInterval()
        {
            if (_autoRefreshIntervalIndex < 0 || _autoRefreshIntervalIndex >= AutoRefreshIntervals.Count)
            {
                return TimeSpan.FromSeconds(3);
            }

            return AutoRefreshIntervals[_autoRefreshIntervalIndex].Interval;
        }

        private void CycleAutoRefreshInterval()
        {
            if (AutoRefreshIntervals.Count == 0)
            {
                return;
            }

            _autoRefreshIntervalIndex++;
            if (_autoRefreshIntervalIndex >= AutoRefreshIntervals.Count)
            {
                _autoRefreshIntervalIndex = 0;
            }

            SyncIntervalLabel();

            if (_autoRefreshEnabled)
            {
                StartAutoRefreshTimer();
            }
        }

        private void SyncIntervalLabel()
        {
            var text = "5s";

            if (_autoRefreshIntervalIndex >= 0 && _autoRefreshIntervalIndex < AutoRefreshIntervals.Count)
            {
                text = AutoRefreshIntervals[_autoRefreshIntervalIndex].Label;
            }

            AutoRefreshIntervalLabel.Text = text;
        }

        private void UpdateAutoRefreshButtonVisual()
        {
            AutoRefreshButton.Text = "Refresh";
            AutoRefreshButton.TextColor = Colors.White;
            AutoRefreshButton.BackgroundColor = _autoRefreshEnabled ? Color.FromArgb("#16A34A") : Color.FromArgb("#64748B");
        }

        private void OnLoggingToggled(object sender, ToggledEventArgs e)
        {
            AppLog.SetEnabled(e.Value);
            SyncLoggingState(e.Value);

            if (!e.Value)
            {
                _autoRefreshEnabled = false;
                UpdateAutoRefreshButtonVisual();
                StopAutoRefreshTimer();
            }

            RefreshLog();
        }

        private void SyncLoggingState(bool? enabled = null)
        {
            var resolvedEnabled = enabled ?? AppLog.IsEnabled;

            // The Switch provides the visual state; this label provides explicit text.
            LoggingStateLabel.Text = resolvedEnabled ? "Enabled" : "Disabled";
        }

        private async void OnCopyClicked(object sender, EventArgs e)
        {
            try
            {
                if (!AppLog.IsEnabled)
                {
                    LogEditor.Text = "Logging is disabled.";
                    return;
                }

                var text = LogEditor.Text ?? string.Empty;
                await AppClipboard.SetTextAsync(text);
                await UiDialogs.AlertAsync("Copied", "Log copied to clipboard.", "OK");
            }
            catch
            {
                await UiDialogs.AlertAsync("Error", "Unable to copy log.", "OK");
            }
        }
    }
}
