using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using JoesScanner.Views.Controls;

namespace JoesScanner.Views
{
    // Settings page code behind.
    // Responsible for wiring up navigation and discarding unsaved
    // connection changes when the user leaves the page.
    // Also provides handlers for filter tap gestures.
    public partial class SettingsPage : ContentPage, ITabHidingAware
    {
        private readonly IAppUpdateService? _appUpdateService;
        private SettingsViewModel? _observedSettingsViewModel;
        private bool _isTabVisible;
        private View? _tracedHost;
        private long _hostAttachStarted;
        private long _navigationStarted;
        private bool _firstLayoutTraced;
        private Editor? LogEditor => LogBodyLayout?.Content?.FindByName<Editor>(nameof(LogEditor));

        public static readonly BindableProperty FiltersTextTranslationXProperty = BindableProperty.Create(
            nameof(FiltersTextTranslationX),
            typeof(double),
            typeof(SettingsPage),
            0d);

        public double FiltersTextTranslationX
        {
            get => (double)GetValue(FiltersTextTranslationXProperty);
            set => SetValue(FiltersTextTranslationXProperty, value);
        }

        // Profile UI is managed via a simple Picker plus a Manage menu.

        // Default constructor used by XAML.
        // BindingContext is expected to be supplied by DI / Shell.
        public SettingsPage()
        {
            SafeInitializeComponent();
            _appUpdateService = ResolveAppUpdateService();
            SetAppVersionTextSafe();
            SetAboutInfoTextSafe();

        }

        // Optional constructor if you ever want to inject the view model.
        public SettingsPage(SettingsViewModel viewModel, IAppUpdateService appUpdateService)
        {
            SafeInitializeComponent();
            var bindingStarted = Stopwatch.GetTimestamp();
            BindingContext = viewModel;
            // The tab host takes this content out of its page. Keep its bindings through that move.
            if (Content is { } content)
                content.BindingContext = viewModel;
            AppLog.Add(() => $"Settings timing: root binding setup={Stopwatch.GetElapsedTime(bindingStarted).TotalMilliseconds:F1} ms.");
            _appUpdateService = appUpdateService;
            SetAppVersionTextSafe();
            SetAboutInfoTextSafe();

        }


        private void SafeInitializeComponent()
        {
            var started = Stopwatch.GetTimestamp();
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
            finally
            {
                AppLog.Add(() => $"Settings timing: managed XAML initialization={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
            }
        }

        internal void TraceFirstHostAttachment(View view, long navigationStarted)
        {
            if (!AppLog.IsEnabled || _firstLayoutTraced)
                return;

            StopTracingHost();
            _tracedHost = view;
            _navigationStarted = navigationStarted;
            _hostAttachStarted = Stopwatch.GetTimestamp();
            if (view is SettingsLayoutGrid grid)
                grid.BeginLayoutTrace();
            view.Loaded += OnTracedHostLayout;
            view.SizeChanged += OnTracedHostLayout;
        }

        internal void CheckFirstVisibleLayout()
        {
            if (_tracedHost is { IsVisible: true, IsLoaded: true, Width: > 0, Height: > 0 })
                AppLog.Add("Settings timing: visible host already has loaded, nonzero bounds after activation.");
        }

        private void OnTracedHostLayout(object? sender, EventArgs e)
        {
            if (_tracedHost is not { IsVisible: true, IsLoaded: true, Width: > 0, Height: > 0 } view)
                return;

            _firstLayoutTraced = true;
            var sinceAttach = Stopwatch.GetElapsedTime(_hostAttachStarted);
            var sinceNavigation = Stopwatch.GetElapsedTime(_navigationStarted);
            AppLog.Add(() => $"Settings timing: first visible loaded nonzero layout={sinceAttach.TotalMilliseconds:F1} ms from activation sequence start, {sinceNavigation.TotalMilliseconds:F1} ms from navigation start.");
            view.Loaded -= OnTracedHostLayout;
            view.SizeChanged -= OnTracedHostLayout;
        }

        private void StopTracingHost()
        {
            if (_tracedHost is not { } view)
                return;

            if (view is SettingsLayoutGrid grid)
                grid.EndLayoutTrace();
            view.Loaded -= OnTracedHostLayout;
            view.SizeChanged -= OnTracedHostLayout;
            _tracedHost = null;
        }


        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged();
            HookBindingContext();
        }

        private void HookBindingContext()
        {
            if (_observedSettingsViewModel != null)
            {
                _observedSettingsViewModel.PropertyChanged -= OnSettingsViewModelPropertyChanged;
                _observedSettingsViewModel = null;
            }

            if (_isTabVisible && BindingContext is SettingsViewModel vm)
            {
                _observedSettingsViewModel = vm;
                vm.PropertyChanged += OnSettingsViewModelPropertyChanged;
                vm.RefreshLogEnabledFromRuntime();
            }
        }

        private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SettingsViewModel.LogEnabled))
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    if (_isTabVisible && LogBodyLayout?.IsVisible == true)
                        RefreshLogText();
                }
                catch
                {
                }
            });
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

        private void OnMuteFilterClicked(object? sender, EventArgs e)
        {
            if (BindingContext is not SettingsViewModel vm)
                return;

            if (sender is not BindableObject bindable || bindable.BindingContext is not FilterRule rule)
                return;

            if (vm.ToggleMuteFilterCommand?.CanExecute(rule) == true)
                vm.ToggleMuteFilterCommand.Execute(rule);
        }

        private void OnDisableFilterClicked(object? sender, EventArgs e)
        {
            if (BindingContext is not SettingsViewModel vm)
                return;

            if (sender is not BindableObject bindable || bindable.BindingContext is not FilterRule rule)
                return;

            if (vm.ToggleDisableFilterCommand?.CanExecute(rule) == true)
                vm.ToggleDisableFilterCommand.Execute(rule);
        }

        private async void OnFiltersScrollSliderValueChanged(object? sender, ValueChangedEventArgs e)
        {
            try
            {
                FiltersTextTranslationX = -e.NewValue;

                if (FiltersBodyLayout.Content?.FindByName<ScrollView>("FiltersTextScrollView") is { } scroll)
                    await scroll.ScrollToAsync(e.NewValue, 0, false);
            }
            catch
            {
            }
        }


        protected override void OnAppearing()
        {
            var started = Stopwatch.GetTimestamp();
            base.OnAppearing();
            if (BindingContext is SettingsViewModel connections)
                connections.RefreshScannerConnectionRows();
            var baseElapsed = Stopwatch.GetElapsedTime(started);
            if (_isTabVisible)
                return;

            _isTabVisible = true;
            var bindingStarted = Stopwatch.GetTimestamp();
            HookBindingContext();
            var bindingElapsed = Stopwatch.GetElapsedTime(bindingStarted);
            if (BindingContext is SettingsViewModel vm)
            {
                var queued = Stopwatch.GetTimestamp();
                Dispatcher.Dispatch(async () =>
                {
                    if (_isTabVisible)
                    {
                        var callback = Stopwatch.GetTimestamp();
                        var pending = vm.OnPageOpenedAsync();
                        var wait = Stopwatch.GetElapsedTime(queued, callback);
                        var synchronous = Stopwatch.GetElapsedTime(callback);
                        AppLog.Add(() => $"Settings timing: Settings data callback dispatcher wait={wait.TotalMilliseconds:F1} ms, synchronous work before returning Task={synchronous.TotalMilliseconds:F1} ms.");
                        await pending;
                    }
                });
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            AppLog.Add(() => $"Settings timing: SettingsPage.OnAppearing base={baseElapsed.TotalMilliseconds:F1} ms, binding subscription={bindingElapsed.TotalMilliseconds:F1} ms, total={elapsed.TotalMilliseconds:F1} ms.");
        }

        private void OnFilterTextSliderValueChanged(object sender, ValueChangedEventArgs e)
        {
            // The filter rows bind directly to FilterTextHorizontalTranslation on the page view model.
            // No imperative scroll synchronization is needed here.
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

            // We include server URL in the log export header, but never credentials.
            string serverUrl = "(unknown)";

            if (BindingContext is SettingsViewModel vm)
            {
                serverUrl = string.IsNullOrWhiteSpace(vm.ServerUrl)
                    ? "(not set)"
                    : vm.ServerUrl;

            }

            var viewer = new LogViewerPage(bodyText, async () =>
            {
                await SaveLogFileInteractiveAsync(bodyText, serverUrl);
            });

            await Navigation.PushModalAsync(new NavigationPage(viewer));
        }

        private async Task SaveLogFileInteractiveAsync(string bodyText, string serverUrl)
        {
            try
            {
                var snapshotTime = DateTime.Now;
                var fileContent = BuildLogFileContent(snapshotTime, bodyText, serverUrl);

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

        private static string BuildLogFileContent(DateTime snapshotTime, string bodyText, string serverUrl)
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
            StopTracingHost();
            if (!_isTabVisible)
                return;

            _isTabVisible = false;
            HookBindingContext();
            CollapseAllCards();
            if (BindingContext is SettingsViewModel vm && vm.HasChanges)
                vm.DiscardConnectionChanges();
        }

        protected override void OnDisappearing()
        {
            var started = Stopwatch.GetTimestamp();
            OnTabHiding();
            var hidingElapsed = Stopwatch.GetElapsedTime(started);
            base.OnDisappearing();
            var elapsed = Stopwatch.GetElapsedTime(started);
            AppLog.Add(() => $"Settings timing: SettingsPage.OnDisappearing hide callback={hidingElapsed.TotalMilliseconds:F1} ms, total={elapsed.TotalMilliseconds:F1} ms.");
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


        private void OnScannerConnectionAdd(object? sender, EventArgs e)
        {
            if (BindingContext is SettingsViewModel vm)
                vm.OpenNewScannerConnection();
        }

        private void OnScannerConnectionCancel(object? sender, EventArgs e)
        {
            if (BindingContext is SettingsViewModel vm)
                vm.CloseScannerEditor();
        }

        private void OnScannerConnectionEdit(object? sender, EventArgs e)
        {
            if (sender is Button { BindingContext: ScannerConnectionRow row } && BindingContext is SettingsViewModel vm)
                vm.EditScannerConnection(row);
        }

        private void OnScannerConnectionToggled(object? sender, ToggledEventArgs e)
        {
            if (sender is Microsoft.Maui.Controls.Switch { BindingContext: ScannerConnectionRow row } toggle &&
                BindingContext is SettingsViewModel vm && row.Enabled != e.Value)
            {
                vm.SetScannerConnectionEnabled(row, e.Value);
                toggle.IsToggled = row.Enabled;
            }
        }

        private void OnScannerConnectionMoveUp(object? sender, EventArgs e) => MoveScannerConnection(sender, -1);
        private void OnScannerConnectionMoveDown(object? sender, EventArgs e) => MoveScannerConnection(sender, 1);

        private async void MoveScannerConnection(object? sender, int direction)
        {
            if (sender is not Button { BindingContext: ScannerConnectionRow row } || BindingContext is not SettingsViewModel vm)
                return;
            try { vm.MoveScannerConnection(row, direction); }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Could not reorder saved servers ({ex.GetType().Name}).");
                await UiDialogs.AlertAsync("Server order", "The server order could not be saved.", "Close");
            }
        }

        private async void OnScannerConnectionRemove(object? sender, EventArgs e)
        {
            if (sender is not Button { BindingContext: ScannerConnectionRow row } || BindingContext is not SettingsViewModel vm ||
                !await UiDialogs.ConfirmAsync("Remove server?", $"Remove {row.Name} and its saved recorder login?", "Remove", "Cancel"))
                return;
            try { vm.RemoveScannerConnection(row); }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Could not remove a saved server ({ex.GetType().Name}).");
                await UiDialogs.AlertAsync("Remove server", "The server could not be removed. Try again.", "Close");
            }
        }

        private async void OnScannerConnectionSaveDirectory(object? sender, EventArgs e)
        {
            if (BindingContext is not SettingsViewModel vm)
                return;
            try { vm.SaveDirectoryConnection(); }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Could not save a directory server ({ex.GetType().Name}).");
                await UiDialogs.AlertAsync("Save server", "The server could not be saved.", "Close");
            }
        }

        // Commit the Entry bindings that update on Unfocused before starting validation.
        private async void OnValidateClicked(object sender, EventArgs e)
        {
            try
            {
                // Force bindings to commit their Text values.
                // Only unfocus the custom URL entry when Custom server is actually selected.
                if (BindingContext is SettingsViewModel urlVm && urlVm.CanEditScannerAddress)
                    ConnectionFieldsGrid.Content?.FindByName<Entry>("ServerUrlEntry")?.Unfocus();

                ConnectionFieldsGrid.Content?.FindByName<Entry>("BasicAuthUsernameEntry")?.Unfocus();
                ConnectionFieldsGrid.Content?.FindByName<Entry>("BasicAuthPasswordEntry")?.Unfocus();

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
    if (!isExpanded && body is DeferredContentView deferred && !deferred.TryLoadContent())
        return;

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
    CollapseSection(AudioBodyLayout, AudioChevronLabel);
    CollapseSection(AddressDetectionBodyLayout, AddressDetectionChevronLabel);
    CollapseSection(BluetoothBodyLayout, BluetoothChevronLabel);
    CollapseSection(ThemeBodyLayout, ThemeChevronLabel);
    CollapseSection(TelemetryBodyLayout, TelemetryChevronLabel);
    CollapseSection(LogBodyLayout, LogChevronLabel);
    CollapseSection(CallCardOptionsBody, CallCardOptionsChevron);

    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Autoplay", false);
        vm.SetSettingsCardOpenState("Filters", false);
        vm.SetSettingsCardOpenState("AudioFilters", false);
        vm.SetSettingsCardOpenState("Audio", false);
        vm.SetSettingsCardOpenState("AddressDetection", false);
        vm.SetSettingsCardOpenState("Bluetooth", false);
        vm.SetSettingsCardOpenState("Theme", false);
        vm.SetSettingsCardOpenState("Telemetry", false);
        vm.SetSettingsCardOpenState("Log", false);
    }
}

private async void OnCallCardOptionsHeaderTapped(object? sender, TappedEventArgs e)
{
    ToggleSection(CallCardOptionsBody, CallCardOptionsChevron);
    if (CallCardOptionsBody.IsVisible && BindingContext is SettingsViewModel settings)
        await settings.CallCardOptions.LoadAsync();
}

private void OnConnectionHeaderTapped(object sender, EventArgs e)
{
    var vm = BindingContext as SettingsViewModel;
    ToggleSection(ConnectionFieldsGrid, ConnectionChevronLabel, ConnectionDescriptionLabel);

    if (!ConnectionFieldsGrid.IsVisible || vm == null)
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
    ToggleSection(FiltersBodyLayout, FiltersChevronLabel);
    if (BindingContext is SettingsViewModel vm)
        vm.SetSettingsCardOpenState("Filters", FiltersBodyLayout.IsVisible);
}

private void OnPreviousFilterPageClicked(object? sender, EventArgs e)
{
    if (BindingContext is SettingsViewModel vm)
        vm.MoveFilterPage(-1);
}

private void OnNextFilterPageClicked(object? sender, EventArgs e)
{
    if (BindingContext is SettingsViewModel vm)
        vm.MoveFilterPage(1);
}


private void OnAudioFiltersHeaderTapped(object sender, EventArgs e)
{
    ToggleSection(AudioFiltersBodyLayout, AudioFiltersChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("AudioFilters", AudioFiltersBodyLayout.IsVisible);
    }
}

private void OnAudioHeaderTapped(object sender, EventArgs e)
{
    ToggleSection(AudioBodyLayout, AudioChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Audio", AudioBodyLayout.IsVisible);
    }
}

private void OnAddressDetectionHeaderTapped(object sender, EventArgs e)
{
    ToggleSection(AddressDetectionBodyLayout, AddressDetectionChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("AddressDetection", AddressDetectionBodyLayout.IsVisible);
    }
}

private void OnBluetoothHeaderTapped(object sender, EventArgs e)
{
    ToggleSection(BluetoothBodyLayout, BluetoothChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Bluetooth", BluetoothBodyLayout.IsVisible);
    }
}

private void OnThemeHeaderTapped(object sender, EventArgs e)
{
    ToggleSection(ThemeBodyLayout, ThemeChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Theme", ThemeBodyLayout.IsVisible);
    }
}

private void OnTelemetryHeaderTapped(object sender, EventArgs e)
{
    ToggleSection(TelemetryBodyLayout, TelemetryChevronLabel);
    if (BindingContext is SettingsViewModel vm)
    {
        vm.SetSettingsCardOpenState("Telemetry", TelemetryBodyLayout.IsVisible);
    }
}

private void OnLogHeaderTapped(object sender, EventArgs e)
{
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
        if (BindingContext is SettingsViewModel vm)
        {
            serverUrl = vm.ServerUrl ?? string.Empty;
        }

        var fileContent = BuildLogFileContent(snapshotTime, bodyText, serverUrl);

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

private async void OnOpenPrivacyPolicyClicked(object sender, EventArgs e)
{
    if (_appUpdateService == null)
    {
        await UiDialogs.AlertAsync("Privacy Policy", "Privacy Policy link is not available in this build.", "Close");
        return;
    }

    try
    {
        await _appUpdateService.OpenPrivacyPolicyAsync();
    }
    catch (Exception ex)
    {
        await UiDialogs.AlertAsync("Privacy Policy", $"Could not open the Privacy Policy:\n{ex.Message}", "Close");
    }
}

private void RefreshLogText()
{
    if (LogEditor is not { } editor)
        return;

    try
    {
        if (!AppLog.IsEnabled)
        {
            editor.Text = "Logging is disabled. Turn on Enable logging to capture entries.";
            return;
        }

        var lines = AppLog.GetSnapshot(500);
        editor.Text = lines.Length == 0
            ? "No log entries yet."
            : string.Join(Environment.NewLine, lines);
    }
    catch (Exception ex)
    {
        try
        {
            editor.Text = $"Could not load log:\\n{ex.Message}";
        }
        catch
        {
        }
    }
}

}
}
