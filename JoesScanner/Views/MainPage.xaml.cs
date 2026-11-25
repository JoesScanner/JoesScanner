using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

#if IOS
using UIKit;
using Foundation;
#endif

namespace JoesScanner.Views
{
    public partial class MainPage : ContentPage, ITabShowingAware, ITabHidingAware
    {
        private readonly MainViewModel _viewModel = null!;

        private bool _handlersAttached;

        public CallCardOptionsService CallCardOptions { get; }

        public MainPage(MainViewModel viewModel, CallCardOptionsService callCardOptions)
        {
            CallCardOptions = callCardOptions;
            InitializeComponent();

            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            BindingContext = _viewModel;
            InitializeLiveDisplay();

            try
            {
                SizeChanged += (_, __) => UpdateMediaButtonsScrollWidth();
                if (MediaIndicatorsPanel != null)
                    MediaIndicatorsPanel.SizeChanged += (_, __) => UpdateMediaButtonsScrollWidth();
                if (MediaButtonsStack != null)
                    MediaButtonsStack.SizeChanged += (_, __) => UpdateMediaButtonsScrollWidth();
            }
            catch
            {
            }

            // WinUI can report 0 widths during initial layout passes.
            // Nudge the scroll width calculation after the first render so the indicators hug the last button.
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(200), () =>
                        {
                            UpdateMediaButtonsScrollWidth();
                            return false;
                        });
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }



            Unloaded += OnPageUnloaded;
        }
        private bool _isAudioMenuOpen;

        private void OnAudioMenuClicked(object? sender, EventArgs e)
        {
            if (_isAudioMenuOpen)
                return;

            _isAudioMenuOpen = true;

            try
            {
                AppLog.Add(() => "Audio(UI): Speaker button clicked. Toggling audio menu overlay.");

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        AudioMenuOverlay.IsVisible = !AudioMenuOverlay.IsVisible;
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            AppLog.Add(() => $"Audio(UI): Overlay toggle error. {DiagnosticRedaction.Describe(ex)}");
                        }
                        catch
                        {
                        }
                    }
                });
            }
            catch
            {
            }
            finally
            {
                _isAudioMenuOpen = false;
            }
        }

        private void OnAudioMenuBackdropTapped(object? sender, TappedEventArgs e)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    AudioMenuOverlay.IsVisible = false;
                });
            }
            catch
            {
            }
        }

        private async void OnAudioMenuOptionClicked(object? sender, EventArgs e)
        {
            try
            {
                var button = sender as Button;
                var param = button?.CommandParameter?.ToString();

                double? speedStep = null;
                if (!string.IsNullOrWhiteSpace(param) && !string.Equals(param, "Off", StringComparison.OrdinalIgnoreCase))
                {
                    // PlaybackSpeedStep in this app is a step index:
                    // 0 = 1x, 1 = 1.25x, 2 = 1.5x, 3 = 1.75x, 4 = 2x
                    if (int.TryParse(param, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedStep))
                    {
                        speedStep = parsedStep;
                    }
                }

                AppLog.Add(() => $"Audio(UI): Audio menu option clicked. Value={(speedStep.HasValue ? speedStep.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Off")}");

                AudioMenuOverlay.IsVisible = false;

                await _viewModel.ApplyAudioMenuSelectionAsync(speedStep);
            }
            catch (Exception ex)
            {
                try
                {
                    AppLog.Add(() => $"Audio(UI): Audio menu option error. {DiagnosticRedaction.Describe(ex)}");
                }
                catch
                {
                }
            }
        }


        
        private void UpdateMediaButtonsScrollWidth()
        {
            try
            {
                if (MediaButtonsScroll == null || MediaIndicatorsPanel == null || MediaButtonsStack == null)
                    return;

                var pageWidth = Width;
                if (pageWidth <= 0)
                    return;

                // Space available for the scroll region after the indicators block.
                var indicatorsWidth = MediaIndicatorsPanel.Width;
                var horizontalGutters = 16; // padding and safety buffer
                var available = pageWidth - indicatorsWidth - horizontalGutters;

                if (available <= 0)
                    return;

                // If content is smaller than available, shrink scrollview to content so there is no dead gap.
                // If content is larger, clamp to available so the user can scroll.
                var contentWidth = MediaButtonsStack.Width;
                if (contentWidth <= 0)
                {
                    // Fallback estimate if not measured yet.
                    contentWidth = 5 * 38;
                }

                var desired = Math.Min(contentWidth, available);

                if (desired < 0)
                    desired = 0;

                // Only apply if changed enough to avoid layout churn.
                if (Math.Abs(MediaButtonsScroll.WidthRequest - desired) > 0.5)
                {
                    MediaButtonsScroll.WidthRequest = desired;
                }
            }
            catch
            {
            }
        }

        private void AttachLiveHandlers()
        {
            if (_handlersAttached)
                return;

            _handlersAttached = true;
            _viewModel.AddressAlerts.CollectionChanged += OnAddressAlertsChanged;
            AttachLiveDisplay();
        }

        private void DetachLiveHandlers()
        {
            if (!_handlersAttached)
                return;

            _handlersAttached = false;
            _viewModel.AddressAlerts.CollectionChanged -= OnAddressAlertsChanged;
            DetachLiveDisplay();
        }

        private void OnPageUnloaded(object? sender, EventArgs e) => DetachLiveHandlers();

        private void OnAddressAlertsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            AppLog.Add(() => $"AddrAlert(UI): MainPage AddressAlerts changed. visibleCount={_viewModel.AddressAlerts.Count} hasVisibleFlag={_viewModel.HasAddressAlerts}");
        }

        private async void OnSettingsTapped(object sender, EventArgs e)
        {
            try
            {
                // Settings is a top-level Shell tab. Use an absolute route so we switch tabs
                // instead of pushing onto the current navigation stack (which can surface an iOS back button).
                TabNavigationService.Instance.Request(AppTab.Settings);
            }
            catch
            {
            }
        }

        private async void OnLogTapped(object sender, EventArgs e)
        {
            try
            {
                var lines = AppLog.GetSnapshot(600);
                var text = (lines == null || lines.Length == 0)
                    ? "No log entries yet."
                    : string.Join(Environment.NewLine, lines);

                var editor = new Editor
                {
                    Text = text,
                    IsReadOnly = true,
                    AutoSize = EditorAutoSizeOption.Disabled,
                    FontSize = 12,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill
                };

                var copyButton = new Button { Text = "Copy" };
                copyButton.Clicked += async (_, __) =>
                {
                    try
                    {
                        await Clipboard.Default.SetTextAsync(text);
                    }
                    catch
                    {
                    }
                };

#if WINDOWS
                var shareButton = new Button { Text = "Close" };
#else
                var shareButton = new Button { Text = "Share" };
#endif

                var closeButton = new Button { Text = "Close" };

#if WINDOWS
                shareButton.Clicked += async (_, __) =>
                {
                    try
                    {
                        if (UiDialogs.GetHostPage()?.Navigation is { } navigation)
                            await navigation.PopModalAsync();
                    }
                    catch
                    {
                    }
                };
#else
                shareButton.Clicked += async (_, __) =>
                {
                    try
                    {
                        await Share.Default.RequestAsync(new ShareTextRequest
                        {
                            Text = text,
                            Title = "Share log"
                        });
                    }
                    catch
                    {
                    }
                };
#endif

                closeButton.Clicked += async (_, __) =>
                {
                    try
                    {
                        if (UiDialogs.GetHostPage()?.Navigation is { } navigation)
                            await navigation.PopModalAsync();
                    }
                    catch
                    {
                    }
                };

                var buttonBar = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Star },
                        new ColumnDefinition { Width = GridLength.Star },
                        new ColumnDefinition { Width = GridLength.Star }
                    },
                    ColumnSpacing = 8
                };

                buttonBar.Add(copyButton, 0, 0);
                buttonBar.Add(shareButton, 1, 0);
                buttonBar.Add(closeButton, 2, 0);

                var root = new Grid
                {
                    Padding = new Thickness(12),
                    RowDefinitions =
                    {
                        new RowDefinition { Height = GridLength.Star },
                        new RowDefinition { Height = GridLength.Auto }
                    },
                    RowSpacing = 10
                };

                var scroll = new ScrollView { Content = editor };

                root.Add(scroll);
                Grid.SetRow(scroll, 0);

                root.Add(buttonBar);
                Grid.SetRow(buttonBar, 1);

                var page = new ContentPage
                {
                    Title = "Log",
                    Content = root
                };

                if (UiDialogs.GetHostPage()?.Navigation is { } navigation)
                    await navigation.PushModalAsync(new NavigationPage(page));
            }
            catch
            {
            }
        }

        private async void OnSiteTapped(object sender, EventArgs e)
        {
            try
            {
                var uri = new Uri("https://www.joesscanner.com/");
                if (_viewModel.AllowsExternalLink(uri))
                    await Launcher.Default.OpenAsync(uri);
            }
            catch
            {
            }
        }

        

        private async Task OpenMapsForCallAsync(JoesScanner.Models.CallItem? call)
        {
            try
            {
                if (call == null)
                    return;

                // Respect user settings when available (do not hard-fail if services are not ready).
                var settingsService = this.Handler?.MauiContext?.Services?.GetService<ISettingsService>();
                if (settingsService != null)
                {
                    if (!settingsService.AddressDetectionEnabled)
                        return;

                    if (!settingsService.AddressDetectionOpenMapsOnTap)
                        return;
                }

                if (string.IsNullOrWhiteSpace(call.DetectedAddress))
                    return;

                var mapAnchor = string.Empty;
                try
                {
                    if (settingsService != null)
                        settingsService.TryGetMapAnchorForServerUrl(settingsService.ServerUrl, out mapAnchor);
                }
                catch
                {
                }

                var uri = BuildMapsSearchUri(call.DetectedAddress, mapAnchor);
                await Launcher.Default.OpenAsync(uri);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"AddressDetection: open maps failed. {DiagnosticRedaction.Describe(ex)}");
            }
        }

        private async void OnOpenMapsClicked(object sender, EventArgs e)
        {
            if (sender is Button button && button.CommandParameter is JoesScanner.Models.CallItem call)
            {
                await OpenMapsForCallAsync(call);
            }
        }

        private async void OnDetectedAddressTapped(object sender, EventArgs e)
        {
            try
            {
                // BindingContext is the CallItem from the binding context.
                if (sender is BindableObject bo && bo.BindingContext is JoesScanner.Models.CallItem callFromContext)
                {
                    await OpenMapsForCallAsync(callFromContext);
                    return;
                }

                // Fallback: some platforms pass the parameter differently.
                if (sender is Element el && el.BindingContext is JoesScanner.Models.CallItem callFromElement)
                {
                    await OpenMapsForCallAsync(callFromElement);
                }
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"AddressDetection: open maps failed. {DiagnosticRedaction.Describe(ex)}");
            }
        }

        private async void OnWhat3WordsTapped(object sender, EventArgs e)
        {
            try
            {
                if (sender is BindableObject bo && bo.BindingContext is JoesScanner.Models.CallItem callFromContext)
                {
                    await OpenWhat3WordsForCallAsync(callFromContext);
                    return;
                }

                if (sender is Element el && el.BindingContext is JoesScanner.Models.CallItem callFromElement)
                {
                    await OpenWhat3WordsForCallAsync(callFromElement);
                }
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"What3Words: open map failed. {DiagnosticRedaction.Describe(ex)}");
            }
        }

        
        private async void OnWhat3WordsCopyTapped(object sender, EventArgs e)
        {
            try
            {
                if (sender is BindableObject bo && bo.BindingContext is JoesScanner.Models.CallItem call)
                {
                    if (string.IsNullOrWhiteSpace(call.What3WordsAddress))
                        return;

                    await Clipboard.Default.SetTextAsync(call.What3WordsAddress.Trim());
                }
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"What3Words: copy failed. {DiagnosticRedaction.Describe(ex)}");
            }
        }

        private async void OnWhat3WordsCoordsCopyTapped(object sender, EventArgs e)
        {
            try
            {
                if (sender is BindableObject bo && bo.BindingContext is JoesScanner.Models.CallItem call)
                {
                    if (string.IsNullOrWhiteSpace(call.What3WordsCoordinatesText))
                        return;

                    await Clipboard.Default.SetTextAsync(call.What3WordsCoordinatesText.Trim());
                }
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"What3Words: copy coords failed. {DiagnosticRedaction.Describe(ex)}");
            }
        }


private static async Task OpenWhat3WordsForCallAsync(JoesScanner.Models.CallItem call)
        {
            if (call == null)
                return;

            if (string.IsNullOrWhiteSpace(call.What3WordsAddress))
                return;

            var uri = BuildWhat3WordsUri(call.What3WordsAddress);
            await Launcher.Default.OpenAsync(uri);
        }

        


        private ISettingsService? TryResolveSettingsService()
        {
            try
            {
                return Application.Current?.Handler?.MauiContext?.Services?.GetService(typeof(ISettingsService)) as ISettingsService;
            }
            catch
            {
                return null;
            }
        }


        private void OnCallSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (sender is not CollectionView cv)
                    return;

                var selected = e.CurrentSelection?.FirstOrDefault() as CallItem;

                cv.SelectedItem = null;

                if (selected == null)
                    return;

                if (BindingContext is MainViewModel vm && vm.PlayAudioCommand != null)
                {
                    if (vm.PlayAudioCommand.CanExecute(selected))
                        vm.PlayAudioCommand.Execute(selected);
                }
            }
            catch
            {
            }
        }

        public void OnTabShowing() => AttachLiveHandlers();
        public void OnTabHiding() => DetachLiveHandlers();

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            AttachLiveHandlers();

            if (BindingContext is MainViewModel vm)
            {
                // iOS can miss WebSocket update notifications while the app is backgrounded.
                // When the user returns to the main page, refresh calls with missing transcription or duration.
                await vm.RefreshStaleCallMetadataAsync();
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            DetachLiveHandlers();
        }
    


private void OnAlertTapped(object? sender, TappedEventArgs e)
{
    try
    {
        if (e?.Parameter is not CallItem call)
            return;

        var resolved = _viewModel.ResolveCallForAddressAlert(call);
        if (resolved == null)
        {
            _viewModel.DismissAddressAlert(call);
            return;
        }

        // Scroll to the call so the user can see it in context.
        try { ScrollToAddressCall(resolved); } catch { }

	        // Address alert taps should not derail the live queue anchor.
	        // Play the selected call, then let the queue continue from where it was.
	        _ = _viewModel.PlayFromAddressAlertAsync(resolved);
    }
    catch
    {
    }
}

private void OnAlertDismissTapped(object? sender, TappedEventArgs e)
{
    try
    {
        if (e?.Parameter is not CallItem call)
            return;

        _viewModel.DismissAddressAlert(call);
    }
    catch
    {
    }
}

private void OnAlertDismissInvoked(object? sender, EventArgs e)
{
    try
    {
        if (sender is SwipeItem swipe && swipe.CommandParameter is CallItem call)
            _viewModel.DismissAddressAlert(call);
    }
    catch
    {
    }
}

        private void OnCallTapped(object? sender, TappedEventArgs e)
        {
            if (e?.Parameter is CallItem call)
            {
                if (_viewModel.PlayAudioCommand?.CanExecute(call) == true)
                {
                    if (!_favoritesLive && _liveDisplay.Separate)
                        _viewModel.SetVisibleLiveView(call.ServerId);
                    _viewModel.PlayAudioCommand.Execute(call);
                }
            }

#if IOS
            ClearIosCallCellHighlight();
#endif
        }

#if IOS
        private void ClearIosCallCellHighlight()
        {
            try
            {
                if (CallsView?.Handler?.PlatformView is not UICollectionView cv)
                    return;

                // Prevent iOS from leaving the cell in a selected state.
                cv.AllowsSelection = false;
                cv.AllowsMultipleSelection = false;

                var selected = cv.GetIndexPathsForSelectedItems();
                if (selected != null)
                {
                    foreach (var ip in selected)
                        cv.DeselectItem(ip, false);
                }

                foreach (var cell in cv.VisibleCells)
                {
                    try
                    {
                        cell.SelectedBackgroundView = new UIView { BackgroundColor = UIColor.Clear };
                    }
                    catch
                    {
                        // Some bindings do not expose SelectedBackgroundView; use KVC as a fallback.
                        cell.SetValueForKey(new UIView { BackgroundColor = UIColor.Clear }, new NSString("selectedBackgroundView"));
                    }

                    // Also clear any highlight/background views iOS may apply while the user taps.
                    try
                    {
                        cell.BackgroundView = new UIView { BackgroundColor = UIColor.Clear };
                    }
                    catch
                    {
                    }

                    try
                    {
                        cell.BackgroundColor = UIColor.Clear;
                        cell.ContentView.BackgroundColor = UIColor.Clear;
                    }
                    catch
                    {
                    }

                    cell.Selected = false;
                    cell.Highlighted = false;
                }
            }
            catch
            {
                // Ignore: this is a cosmetic-only cleanup.
            }
        }
#endif

        private static Uri BuildWhat3WordsUri(string what3Words)
        {
            var s = (what3Words ?? string.Empty).Trim();

            if (s.StartsWith("///", StringComparison.Ordinal))
                s = s.Substring(3);

            s = s.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '\'', '!', '?');

            var url = "https://map.what3words.com/" + Uri.EscapeDataString(s);
            return new Uri(url);
        }



        private static Uri BuildMapsSearchUri(string addressQuery, string mapAnchor)
        {
            var q = (addressQuery ?? string.Empty).Trim();

            var anchor = (mapAnchor ?? string.Empty).Trim();
            if (anchor.Length > 0)
                q = q + ", " + anchor;
            if (q.Length == 0)
                return new Uri("https://www.google.com/maps/search/?api=1&query=");

            var encoded = Uri.EscapeDataString(q);

            try
            {
                if (DeviceInfo.Platform == DevicePlatform.iOS)
                    return new Uri($"http://maps.apple.com/?q={encoded}");

                if (DeviceInfo.Platform == DevicePlatform.Android)
                    return new Uri($"geo:0,0?q={encoded}");
            }
            catch
            {
            }

            return new Uri($"https://www.google.com/maps/search/?api=1&query={encoded}");
        }

}
}
