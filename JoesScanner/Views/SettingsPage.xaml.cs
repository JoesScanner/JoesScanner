using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace JoesScanner.Views
{
    // Settings page code behind.
    // Responsible for wiring up navigation and discarding unsaved
    // connection changes when the user leaves the page.
    // Also provides handlers for filter tap gestures.
    public partial class SettingsPage : ContentPage, ITabHidingAware
    {
        private readonly IAppUpdateService? _appUpdateService;

        // Profile UI is managed via a simple Picker plus a Manage menu.

        // Default constructor used by XAML.
        // BindingContext is expected to be supplied by DI / Shell.
        public SettingsPage()
        {
            SafeInitializeComponent();
            _appUpdateService = ResolveAppUpdateService();
            SetAppVersionTextSafe();
            SetAboutInfoTextSafe();
            // No per page edit mode state.
        }

        // Optional constructor if you ever want to inject the view model.
        public SettingsPage(SettingsViewModel viewModel, IAppUpdateService appUpdateService)
        {
            SafeInitializeComponent();
            BindingContext = viewModel;
            _appUpdateService = appUpdateService;
            SetAppVersionTextSafe();
            SetAboutInfoTextSafe();
            // No per page edit mode state.
        }


        private void SafeInitializeComponent()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                // If XAML load fails (often a WinRT/WinUI COMException when running unpackaged),
                // don't let it abort navigation. Render a simple fallback view and log details.
                try { AppLog.DebugWriteLine(() => $"SettingsPage.InitializeComponent failed: {ex}"); } catch { }

                Content = new ScrollView
                {
                    Content = new VerticalStackLayout
                    {
                        Padding = new Thickness(16),
                        Spacing = 12,
                        Children =
                        {
                            new Label { Text = "Settings failed to load", FontSize = 20, FontAttributes = FontAttributes.Bold },
                            new Label { Text = "A platform component failed while loading the Settings UI.", FontSize = 14 },
                            new Label { Text = ex.ToString(), FontSize = 12 }
                        }
                    }
                };
            }
        }



        private static IAppUpdateService? ResolveAppUpdateService()
        {
            try
            {
                return Application.Current?.Handler?.MauiContext?.Services?.GetService<IAppUpdateService>();
            }
            catch
            {
                return null;
            }
        }

        private void SetAboutInfoTextSafe()
        {
            try
            {
                if (_appUpdateService == null)
                    return;

                if (AboutVersionLabel != null)
                    AboutVersionLabel.Text = $"v{_appUpdateService.CurrentVersionDisplay}";

                if (AboutPlatformLabel != null)
                    AboutPlatformLabel.Text = _appUpdateService.PlatformDisplayName;

                if (AboutStoreLabel != null)
                    AboutStoreLabel.Text = _appUpdateService.StoreDisplayName;
            }
            catch
            {
            }
        }

        private void SetAppVersionTextSafe()
{
    // UI should show only Major.Minor.Patch, even if the platform reports 4 parts.
    // On Windows, AppInfo can throw when the app is running unpackaged (no package identity).
    // Keep Settings navigation resilient by falling back to the assembly version.
    string raw;

    try
    {
        raw = AppInfo.Current.VersionString ?? string.Empty;
    }
    catch
    {
        raw = string.Empty;
    }

    if (string.IsNullOrWhiteSpace(raw))
    {
        try
        {
            raw = typeof(App).Assembly.GetName().Version?.ToString() ?? string.Empty;
        }
        catch
        {
            raw = string.Empty;
        }
    }

    if (string.IsNullOrWhiteSpace(raw))
        raw = "0.0.0";

    var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries);

    var display =
        parts.Length >= 3
            ? $"{parts[0]}.{parts[1]}.{parts[2]}"
            : raw;

    try
    {
        if (AppVersionLabel != null)
            AppVersionLabel.Text = $"v{display}";
    }
    catch
    {
    }
}



        protected override void OnAppearing()
        {
            base.OnAppearing();

            try
            {
                if (LogEnabledToggle != null)
                {
                    LogEnabledToggle.IsToggled = AppLog.ReloadEnabledStateFromStorage();
                }
            }
            catch
            {
            }


            if (BindingContext is SettingsViewModel vm)
            {
                // Schedule the load so navigation and layout can complete first.
                // Always observe exceptions to avoid iOS killing the process.
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        await vm.OnPageOpenedAsync();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            AppLog.DebugWriteLine(() => $"SettingsPage.OnAppearing load failed: {ex}");
                        }
                        catch
                        {
                        }
                    }
                });
            }
        }
        private async void OnFilterTextSliderValueChanged(object sender, ValueChangedEventArgs e)
        {
            await ApplyFilterTextHorizontalScrollAsync(e.NewValue);
        }

        private async void OnFilterRulesTextScrollViewLoaded(object sender, EventArgs e)
        {
            if (BindingContext is SettingsViewModel vm)
            {
                await ApplyFilterTextHorizontalScrollAsync(vm.FilterTextHorizontalOffset);
            }
        }

        private async Task ApplyFilterTextHorizontalScrollAsync(double offset)
        {
            try
            {
                if (FilterRulesTextScrollView == null)
                    return;

                await FilterRulesTextScrollView.ScrollToAsync(offset, 0, false);
            }
            catch
            {
            }
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
                    TabNavigationService.Instance.Request(AppTab.Main);
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

                await UiDialogs.AlertAsync("Log saved", result.Message, "Close");
            }
            catch (Exception ex)
            {
                await UiDialogs.AlertAsync(
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
                    await UiDialogs.AlertAsync("Copied", "Log copied to clipboard.", "Close");
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
        // collapse the expandable cards and discard any unsaved
        // connection changes to avoid half applied states.

        public void OnTabHiding()
        {
            try
            {
                CollapseAllCards();
            }
            catch
            {
            }

            if (BindingContext is SettingsViewModel vm)
            {
                try
                {
                    if (vm.HasChanges)
                        vm.DiscardConnectionChanges();
                }
                catch
                {
                }
            }
        }

        protected override void OnDisappearing()
        {
            try
            {
                CollapseAllCards();
            }
            catch
            {
                // Never block navigation.
            }

            if (BindingContext is SettingsViewModel vm)
            {
                if (vm.HasChanges)
                {
                    vm.DiscardConnectionChanges();
                }
            }

            base.OnDisappearing();
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


        // Validate click handler.
        // iOS can re-layout and flicker password entries if we push Text updates every keystroke.
        // We bind the Entry Text updates on Unfocused and force an Unfocus here so the latest
        // values commit right before we run validation.
        private async void OnValidateClicked(object sender, EventArgs e)
        {
            try
            {
                // Force bindings to commit their Text values.
                // Only unfocus the custom URL entry when Custom server is actually selected.
                if (BindingContext is SettingsViewModel urlVm && urlVm.IsCustomServerSelected)
                    ServerUrlEntry?.Unfocus();

                BasicAuthUsernameEntry?.Unfocus();
                BasicAuthPasswordEntry?.Unfocus();

                // Give the UI thread a moment to propagate binding updates.
                await Task.Delay(50);

                if (BindingContext is SettingsViewModel vm && vm.ValidateServerCommand != null)
                {
                    if (vm.ValidateServerCommand.CanExecute(null))
                    {
                        vm.ValidateServerCommand.Execute(null);
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    AppLog.DebugWriteLine(() => $"SettingsPage.OnValidateClicked failed: {ex}");
                }
                catch
                {
                }
            }
        }

private void ToggleSection(VisualElement body, Label chevron, VisualElement? description = null)
{
    if (body == null || chevron == null)
        return;

    var isExpanded = body.IsVisible;

    body.IsVisible = !isExpanded;
    chevron.Text = body.IsVisible ? "▼" : "▶";

    if (description != null)
        description.IsVisible = body.IsVisible;
}

private void CollapseSection(VisualElement body, Label chevron, VisualElement? description = null)
{
    if (body == null || chevron == null)
        return;

    body.IsVisible = false;
    chevron.Text = "▶";

    if (description != null)
        description.IsVisible = false;
}

private void CollapseAllCards()
{
    // Connection
    CollapseSection(ConnectionFieldsGrid, ConnectionChevronLabel, ConnectionDescriptionLabel);

    // Non-connection cards
    CollapseSection(AutoplayFieldsGrid, AutoplayChevronLabel);
    CollapseSection(FiltersBodyLayout, FiltersChevronLabel);
    CollapseSection(AudioFiltersBodyLayout, AudioFiltersChevronLabel);
    CollapseSection(AddressDetectionBodyLayout, AddressDetectionChevronLabel);
    CollapseSection(BluetoothBodyLayout, BluetoothChevronLabel);
    CollapseSection(ThemeBodyLayout, ThemeChevronLabel);
    CollapseSection(TelemetryBodyLayout, TelemetryChevronLabel);
    CollapseSection(LogBodyLayout, LogChevronLabel);

    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Autoplay", false);
        vm.SetSettingsCardOpenState("Filters", false);
        vm.SetSettingsCardOpenState("AudioFilters", false);
        vm.SetSettingsCardOpenState("AddressDetection", false);
        vm.SetSettingsCardOpenState("Bluetooth", false);
        vm.SetSettingsCardOpenState("Theme", false);
        vm.SetSettingsCardOpenState("Telemetry", false);
        vm.SetSettingsCardOpenState("Log", false);
    }
}

private void OnConnectionHeaderTapped(object sender, EventArgs e)
{
    ToggleSection(ConnectionFieldsGrid, ConnectionChevronLabel, ConnectionDescriptionLabel);

    if (!ConnectionFieldsGrid.IsVisible)
        return;

    var vm = BindingContext as SettingsViewModel;
    if (vm == null)
        return;

    MainThread.BeginInvokeOnMainThread(async () =>
    {
        try
        {
            await vm.EnsureDirectoryServersFreshAsync(force: false);
        }
        catch (Exception ex)
        {
            try { AppLog.DebugWriteLine(() => $"SettingsPage: connection refresh failed: {ex}"); } catch { }
        }
    });
}

private void OnServerPickerFocused(object sender, FocusEventArgs e)
{
    var vm = BindingContext as SettingsViewModel;
    if (vm == null)
        return;

    MainThread.BeginInvokeOnMainThread(async () =>
    {
        try
        {
            await vm.EnsureDirectoryServersFreshAsync(force: false);
        }
        catch (Exception ex)
        {
            try { AppLog.DebugWriteLine(() => $"SettingsPage: picker refresh failed: {ex}"); } catch { }
        }
    });
}

private void OnAutoplayHeaderTapped(object sender, EventArgs e)
{
    ToggleSection(AutoplayFieldsGrid, AutoplayChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Autoplay", AutoplayFieldsGrid.IsVisible);
    }
}

private void OnFiltersHeaderTapped(object sender, EventArgs e)
{
    var willOpen = !FiltersBodyLayout.IsVisible;

    ToggleSection(FiltersBodyLayout, FiltersChevronLabel);
    if (BindingContext is SettingsViewModel vm)
        vm.SetSettingsCardOpenState("Filters", FiltersBodyLayout.IsVisible);

    if (!willOpen || BindingContext is not SettingsViewModel loadVm)
        return;

    MainThread.BeginInvokeOnMainThread(async () =>
    {
        try
        {
            await Task.Yield();
            await loadVm.EnsureFiltersReadyAsync();
        }
        catch (Exception ex)
        {
            try { AppLog.DebugWriteLine(() => $"SettingsPage: filters load failed: {ex}"); } catch { }
        }
    });
}


private void OnAudioFiltersHeaderTapped(object sender, EventArgs e)
{
    var willOpen = !AudioFiltersBodyLayout.IsVisible;
    var pre = BindingContext as SettingsViewModel;
    if (willOpen && pre != null)
        pre.EnsureSectionLoaded("AudioFilters");

    ToggleSection(AudioFiltersBodyLayout, AudioFiltersChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("AudioFilters", AudioFiltersBodyLayout.IsVisible);
    }
}

private void OnAddressDetectionHeaderTapped(object sender, EventArgs e)
{
    var willOpen = !AddressDetectionBodyLayout.IsVisible;
    var pre = BindingContext as SettingsViewModel;
    if (willOpen && pre != null)
        pre.EnsureSectionLoaded("AddressDetection");

    ToggleSection(AddressDetectionBodyLayout, AddressDetectionChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("AddressDetection", AddressDetectionBodyLayout.IsVisible);
    }
}

private void OnBluetoothHeaderTapped(object sender, EventArgs e)
{
    var willOpen = !BluetoothBodyLayout.IsVisible;
    var pre = BindingContext as SettingsViewModel;
    if (willOpen && pre != null)
        pre.EnsureSectionLoaded("Bluetooth");

    ToggleSection(BluetoothBodyLayout, BluetoothChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Bluetooth", BluetoothBodyLayout.IsVisible);
    }
}

private void OnThemeHeaderTapped(object sender, EventArgs e)
{
    var willOpen = !ThemeBodyLayout.IsVisible;
    var pre = BindingContext as SettingsViewModel;
    if (willOpen && pre != null)
        pre.EnsureSectionLoaded("Theme");

    ToggleSection(ThemeBodyLayout, ThemeChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Theme", ThemeBodyLayout.IsVisible);
    }
}

private void OnTelemetryHeaderTapped(object sender, EventArgs e)
{
    var willOpen = !TelemetryBodyLayout.IsVisible;
    var pre = BindingContext as SettingsViewModel;
    if (willOpen && pre != null)
        pre.EnsureSectionLoaded("Telemetry");

    ToggleSection(TelemetryBodyLayout, TelemetryChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Telemetry", TelemetryBodyLayout.IsVisible);
    }
}

private void OnLogHeaderTapped(object sender, EventArgs e)
{
    var willOpen = !LogBodyLayout.IsVisible;
    var pre = BindingContext as SettingsViewModel;
    if (willOpen && pre != null)
        pre.EnsureSectionLoaded("Log");

    ToggleSection(LogBodyLayout, LogChevronLabel);

    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Log", LogBodyLayout.IsVisible);
    }

    if (LogBodyLayout.IsVisible)
    {
        RefreshLogText();
    }
}

private void OnRefreshLogClicked(object sender, EventArgs e)
{
    RefreshLogText();
}

private async void OnClearLogClicked(object sender, EventArgs e)
{
    try
    {
        AppLog.ClearAll();
        RefreshLogText();
        await UiDialogs.AlertAsync("Cleared", "Log cleared.", "Close");
    }
    catch (Exception ex)
    {
        await UiDialogs.AlertAsync("Error", $"Could not clear the log:\n{ex.Message}", "Close");
    }
}

private void OnLogEnabledToggled(object sender, ToggledEventArgs e)
{
    try
    {
        AppLog.SetEnabled(e.Value);

        if (LogBodyLayout != null && LogBodyLayout.IsVisible)
        {
            RefreshLogText();
        }
    }
    catch
    {
    }
}

private async void OnCopyLogClicked(object sender, EventArgs e)
{
    try
    {
        var text = LogEditor?.Text ?? string.Empty;
        await AppClipboard.SetTextAsync(text);
        await UiDialogs.AlertAsync("Copied", "Log copied to clipboard.", "Close");
    }
    catch (Exception ex)
    {
        await UiDialogs.AlertAsync("Error", $"Could not copy the log:\n{ex.Message}", "Close");
    }
}

private async void OnDownloadLogClicked(object sender, EventArgs e)
{
    try
    {
        var snapshotTime = DateTime.Now;

        var lines = AppLog.GetSnapshot(500);
        var bodyText = lines.Length == 0
            ? "No log entries yet."
            : string.Join(Environment.NewLine, lines);

        // Pull basic connection info for the header if available.
        var serverUrl = string.Empty;
        var username = string.Empty;

        if (BindingContext is SettingsViewModel vm)
        {
            serverUrl = vm.ServerUrl ?? string.Empty;
            username = vm.BasicAuthUsername ?? string.Empty;
        }

        var fileContent = BuildLogFileContent(snapshotTime, bodyText, serverUrl, username);

        var stamp = snapshotTime.ToString("yyyy-MM-dd_HH-mm");
        var fileName = $"{stamp}_JoesScannerLog.txt";

        var result = await SaveTextFileWithUserPromptAsync(fileName, fileContent);
        if (!result.Ok)
            return;

        await UiDialogs.AlertAsync("Log saved", result.Message, "Close");
    }
    catch (Exception ex)
    {
        await UiDialogs.AlertAsync(
            "Error saving log",
            $"Could not save the log file:\\n{ex.Message}",
            "Close");
    }
}

private async void OnCheckForUpdatesClicked(object sender, EventArgs e)
{
    if (_appUpdateService == null)
    {
        await UiDialogs.AlertAsync("Updates", "Update service is not available in this build.", "Close");
        return;
    }

    try
    {
        var result = await _appUpdateService.CheckForUpdatesAsync();
        await UiDialogs.AlertAsync("Updates", result.Message, "Close");
    }
    catch (Exception ex)
    {
        await UiDialogs.AlertAsync("Updates", $"Could not check for updates:\n{ex.Message}", "Close");
    }
}

private async void OnOpenStorePageClicked(object sender, EventArgs e)
{
    if (_appUpdateService == null)
    {
        await UiDialogs.AlertAsync("Store page", "Store page is not available in this build.", "Close");
        return;
    }

    try
    {
        await _appUpdateService.OpenStorePageAsync();
    }
    catch (Exception ex)
    {
        await UiDialogs.AlertAsync("Store page", $"Could not open the store page:\n{ex.Message}", "Close");
    }
}

private async void OnOpenSupportSiteClicked(object sender, EventArgs e)
{
    if (_appUpdateService == null)
    {
        await UiDialogs.AlertAsync("Support", "Support link is not available in this build.", "Close");
        return;
    }

    try
    {
        await _appUpdateService.OpenSupportSiteAsync();
    }
    catch (Exception ex)
    {
        await UiDialogs.AlertAsync("Support", $"Could not open the support site:\n{ex.Message}", "Close");
    }
}

private void RefreshLogText()
{
    try
    {
        if (!AppLog.ReloadEnabledStateFromStorage())
        {
            LogEditor.Text = "Logging is disabled. Turn on Enable logging to capture entries."; 
            return;
        }

        var lines = AppLog.GetSnapshot(500);
        LogEditor.Text = lines.Length == 0
            ? "No log entries yet."
            : string.Join(Environment.NewLine, lines);
    }
    catch (Exception ex)
    {
        try
        {
            LogEditor.Text = $"Could not load log:\\n{ex.Message}";
        }
        catch
        {
        }
    }
}

}
}