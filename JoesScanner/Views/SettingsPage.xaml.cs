using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace JoesScanner.Views
{
    // Settings page code behind.
    // Responsible for wiring up navigation and discarding unsaved
    // connection changes when the user leaves the page.
    // Also provides handlers for filter tap gestures.
    public partial class SettingsPage : ContentPage
    {
        // Default constructor used by XAML.
        // BindingContext is expected to be supplied by DI / Shell.
        public SettingsPage()
        {
            InitializeComponent();
            SetAppVersionText();
        }

        // Optional constructor if you ever want to inject the view model.
        public SettingsPage(SettingsViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
            SetAppVersionText();
        }

        private void SetAppVersionText()
        {
            // Displays: v1.2.3
            AppVersionLabel.Text = $"v{AppInfo.Current.VersionString}";
        }

        // Handler for the Close button in the header.
        // Discards unsaved connection changes and then navigates back.
        private async void OnBackClicked(object sender, EventArgs e)
        {
            if (BindingContext is SettingsViewModel vm)
            {
                if (vm.HasChanges)
                {
                    vm.DiscardConnectionChanges();
                }
            }

            if (Shell.Current != null)
            {
                try
                {
                    await Shell.Current.GoToAsync("..");
                }
                catch
                {
                    // Ignore navigation failures.
                }
            }
        }

        private async void OnLogClicked(object sender, EventArgs e)
        {
            var lines = AppLog.GetSnapshot(500);
            var bodyText = lines.Length == 0
                ? "No log entries yet."
                : string.Join(Environment.NewLine, lines);

            // We want server URL and username from the settings VM for the file header
            string serverUrl = "(unknown)";
            string username = "(none)";

            if (BindingContext is SettingsViewModel vm)
            {
                serverUrl = string.IsNullOrWhiteSpace(vm.ServerUrl)
                    ? "(not set)"
                    : vm.ServerUrl;

                username = string.IsNullOrWhiteSpace(vm.BasicAuthUsername)
                    ? "(none)"
                    : vm.BasicAuthUsername;
            }

            var viewer = new LogViewerPage(bodyText, async () =>
            {
                await SaveLogFileInteractiveAsync(bodyText, serverUrl, username);
            });

            await Navigation.PushModalAsync(new NavigationPage(viewer));
        }

        private async Task SaveLogFileInteractiveAsync(string bodyText, string serverUrl, string username)
        {
            try
            {
                var snapshotTime = DateTime.Now;
                var fileContent = BuildLogFileContent(snapshotTime, bodyText, serverUrl, username);

                // Build filename prefix with date and time (24 hour, no seconds)
                var stamp = snapshotTime.ToString("yyyy-MM-dd_HH-mm");
                var fileName = $"{stamp}_JoesScannerLog.txt";

                var result = await SaveTextFileWithUserPromptAsync(fileName, fileContent);
                if (!result.Ok)
                    return;

                await DisplayAlertAsync("Log saved", result.Message, "Close");
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync(
                    "Error saving log",
                    $"Could not save the log file:\n{ex.Message}",
                    "Close");
            }
        }

        private static string BuildLogFileContent(DateTime snapshotTime, string bodyText, string serverUrl, string username)
        {
            var platform = DeviceInfo.Current.Platform.ToString();
            var osVersion = DeviceInfo.Current.VersionString;

            var appVersion = AppInfo.Current.VersionString;
            var appBuild = AppInfo.Current.BuildString;
            var appId = AppInfo.Current.PackageName;

            var headerLines = new[]
            {
        $"Snapshot taken at: {snapshotTime:yyyy-MM-dd HH:mm} (local)",
        $"App: v{appVersion} ({appBuild})",
        $"Package: {appId}",
        $"Server URL: {serverUrl}",
        $"Username: {username}",
        $"Platform: {platform} {osVersion}",
        "Max log file size: 1 MB",
        string.Empty
    };

            var headerText = string.Join(Environment.NewLine, headerLines);

            // Compose full file content
            var fileContent = headerText + (bodyText ?? string.Empty);

            // Enforce a max file size of 1 MB (in UTF-8 bytes)
            const int maxBytes = 1024 * 1024;
            var bytes = Encoding.UTF8.GetBytes(fileContent);

            if (bytes.Length <= maxBytes)
                return fileContent;

            // If too large, keep the tail portion and mark as truncated
            const string marker = "[Log truncated to fit 1 MB limit]\r\n\r\n";

            // Rough estimate of how many characters to keep
            var ratio = (double)maxBytes / bytes.Length;
            var keepChars = Math.Max(0, (int)(fileContent.Length * ratio));

            // Keep the last 'keepChars' characters (most recent entries)
            var trimmed = fileContent.Length > keepChars
                ? fileContent.Substring(fileContent.Length - keepChars)
                : fileContent;

            return marker + trimmed;
        }

        private readonly struct SaveTextResult
        {
            public bool Ok { get; }
            public string Message { get; }

            public SaveTextResult(bool ok, string message)
            {
                Ok = ok;
                Message = message ?? string.Empty;
            }
        }

        private async Task<SaveTextResult> SaveTextFileWithUserPromptAsync(string suggestedFileName, string content)
        {
#if WINDOWS
            try
            {
                var savedPath = await PickSavePathAndWriteAsync(suggestedFileName, content);
                if (string.IsNullOrWhiteSpace(savedPath))
                    return new SaveTextResult(false, string.Empty);

                return new SaveTextResult(true, $"Log saved to:\n{savedPath}");
            }
            catch
            {
                // If the picker fails for any reason, fall back to MyDocuments.
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var fullPath = Path.Combine(folder, suggestedFileName);

                File.WriteAllText(fullPath, content, Encoding.UTF8);
                return new SaveTextResult(true, $"Save dialog failed, so the log was saved to:\n{fullPath}");
            }
#else
            // On mobile platforms, use the share sheet so the user can choose where to save or send the file.
            var path = Path.Combine(FileSystem.CacheDirectory, suggestedFileName);
            File.WriteAllText(path, content, Encoding.UTF8);

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Save log",
                File = new ShareFile(path)
            });

            return new SaveTextResult(true, "Export opened. Use the share dialog to choose where to save the log file.");
#endif
        }

#if WINDOWS
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private static Task<string?> PickSavePathAndWriteAsync(string suggestedFileName, string content)
        {
            // FileSavePicker is sensitive to threading and HWND initialization.
            // Always run it on the UI thread and ensure InitializeWithWindow is called with a valid HWND.
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName)
                };

                picker.FileTypeChoices.Add("Text file", new System.Collections.Generic.List<string> { ".txt" });

                var hwnd = GetActiveWindow();
                if (hwnd == IntPtr.Zero)
                    hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    hwnd = GetWindowHandle();

                if (hwnd == IntPtr.Zero)
                    throw new InvalidOperationException("Could not resolve a window handle for FileSavePicker.");

                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await picker.PickSaveFileAsync();
                if (file == null)
                    return (string?)null;

                await Windows.Storage.FileIO.WriteTextAsync(file, content);
                return file.Path;
            });
        }

        private static IntPtr GetWindowHandle()
        {
            try
            {
                var window = Application.Current?.Windows?.Count > 0
                    ? Application.Current.Windows[0]
                    : null;

                if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window platformWindow)
                {
                    return WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
                }
            }
            catch
            {
                // Ignore and return zero.
            }

            return IntPtr.Zero;
        }
#endif

        private sealed class LogViewerPage : ContentPage
        {
            private readonly Func<Task> _onDownload;
            private readonly Editor _editor;

            public LogViewerPage(string text, Func<Task> onDownload)
            {
                Title = "Log";

                _onDownload = onDownload ?? (() => Task.CompletedTask);

                _editor = new Editor
                {
                    Text = text ?? string.Empty,
                    IsReadOnly = true,
                    AutoSize = EditorAutoSizeOption.Disabled,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill
                };

                var copyButton = new Button { Text = "Copy" };
                copyButton.SetDynamicResource(VisualElement.StyleProperty, "SecondaryPillButtonStyle");
                copyButton.Clicked += async (_, __) =>
                {
                    await Clipboard.Default.SetTextAsync(_editor.Text ?? string.Empty);
                    await DisplayAlertAsync("Copied", "Log copied to clipboard.", "Close");
                };

                var downloadButton = new Button { Text = "Download" };
                downloadButton.SetDynamicResource(VisualElement.StyleProperty, "PillButtonStyle");
                downloadButton.Clicked += async (_, __) =>
                {
                    downloadButton.IsEnabled = false;
                    try
                    {
                        await _onDownload();
                    }
                    finally
                    {
                        downloadButton.IsEnabled = true;
                    }
                };

                var closeButton = new Button { Text = "Close" };
                closeButton.SetDynamicResource(VisualElement.StyleProperty, "SecondaryPillButtonStyle");
                closeButton.Clicked += async (_, __) => await Navigation.PopModalAsync();

                var buttonRow = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Star },
                        new ColumnDefinition { Width = GridLength.Star },
                        new ColumnDefinition { Width = GridLength.Star }
                    },
                    ColumnSpacing = 10
                };

                buttonRow.Add(copyButton);
                buttonRow.Add(downloadButton);
                buttonRow.Add(closeButton);

                Grid.SetColumn(copyButton, 0);
                Grid.SetColumn(downloadButton, 1);
                Grid.SetColumn(closeButton, 2);

                Content = new Grid
                {
                    Padding = new Thickness(12),
                    RowDefinitions =
                    {
                        new RowDefinition { Height = GridLength.Star },
                        new RowDefinition { Height = GridLength.Auto }
                    },
                    RowSpacing = 10,
                    Children =
                    {
                        _editor,
                        buttonRow
                    }
                };

                Grid.SetRow(_editor, 0);
                Grid.SetRow(buttonRow, 1);
            }
        }

        // If the user leaves the page (back, tab change, etc.)
        // discard any unsaved connection changes to avoid
        // half applied states.
        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            if (BindingContext is SettingsViewModel vm)
            {
                if (vm.HasChanges)
                {
                    vm.DiscardConnectionChanges();
                }
            }
        }

        // Tap handler for the Mute (M) cell in the Filters grid.
        // Invokes SettingsViewModel.ToggleMuteFilterCommand with the FilterRule.
        private void OnMuteFilterTapped(object sender, TappedEventArgs e)
        {
            var rule = e.Parameter as FilterRule ?? (sender as BindableObject)?.BindingContext as FilterRule;
            if (rule == null)
                return;

            if (BindingContext is SettingsViewModel vm &&
                vm.ToggleMuteFilterCommand != null &&
                vm.ToggleMuteFilterCommand.CanExecute(rule))
            {
                vm.ToggleMuteFilterCommand.Execute(rule);
            }
        }

        // Tap handler for the Disable (X) cell in the Filters grid.
        // Invokes SettingsViewModel.ToggleDisableFilterCommand with the FilterRule.
        private void OnDisableFilterTapped(object sender, TappedEventArgs e)
        {
            var rule = e.Parameter as FilterRule ?? (sender as BindableObject)?.BindingContext as FilterRule;
            if (rule == null)
                return;

            if (BindingContext is SettingsViewModel vm &&
                vm.ToggleDisableFilterCommand != null &&
                vm.ToggleDisableFilterCommand.CanExecute(rule))
            {
                vm.ToggleDisableFilterCommand.Execute(rule);
            }
        }

        // Tap handler for the Clear (Clr) cell in the Filters grid.
        // Invokes SettingsViewModel.ClearFilterCommand with the FilterRule.
        private void OnClearFilterTapped(object sender, TappedEventArgs e)
        {
            var rule = e.Parameter as FilterRule ?? (sender as BindableObject)?.BindingContext as FilterRule;
            if (rule == null)
                return;

            if (BindingContext is SettingsViewModel vm &&
                vm.ClearFilterCommand != null &&
                vm.ClearFilterCommand.CanExecute(rule))
            {
                vm.ClearFilterCommand.Execute(rule);
            }
        }
    }
}
