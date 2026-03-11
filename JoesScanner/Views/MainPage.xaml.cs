using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

#if IOS
using UIKit;
using Foundation;
#endif

namespace JoesScanner.Views
{
    public partial class MainPage : ContentPage
    {
        private readonly MainViewModel _viewModel = null!;

        private bool _handlersAttached;
        private bool _followLive = true;

        private bool _isAutoScrolling;
        private int _lastFirstVisibleIndex;

        private DateTime _suppressScrollFollowDetectionUntilUtc;

        private CallItem? _trackedTopItem;


        private readonly HashSet<CallItem> _resizeTrackedItems = new();
#if IOS
        private long _lastIosMeasureInvalidationTicks;
#endif
        private CancellationTokenSource? _pendingScrollCts;

        private bool _addressAlertHandlersAttached;

        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            BindingContext = _viewModel;

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


            try
            {
                _viewModel?.RequestJumpToLiveScroll += OnRequestJumpToLiveScroll;
            }
            catch
            {
            }

            AttachAutoFollowHandlers();

            AttachAddressAlertHandlers();
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
                            AppLog.Add(() => $"Audio(UI): Overlay toggle error. {ex.GetType().Name}: {ex.Message}");
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
                    AppLog.Add(() => $"Audio(UI): Audio menu option error. {ex.GetType().Name}: {ex.Message}");
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

private void AttachAddressAlertHandlers()
        {
            if (_addressAlertHandlersAttached)
                return;

            _addressAlertHandlersAttached = true;

            try
            {
                AppLog.Add(() => $"AddrAlert(UI): MainPage init. vm={_viewModel?.GetType().Name ?? "(null)"} visibleCount={_viewModel?.AddressAlerts?.Count ?? -1} hasVisibleFlag={_viewModel?.HasAddressAlerts}");
            }
            catch
            {
            }

            try
            {
                if (_viewModel?.AddressAlerts != null)
                {
                    _viewModel.AddressAlerts.CollectionChanged += (_, __) =>
                    {
                        try
                        {
                            AppLog.Add(() => $"AddrAlert(UI): MainPage AddressAlerts changed. visibleCount={_viewModel.AddressAlerts.Count} hasVisibleFlag={_viewModel.HasAddressAlerts}");
                        }
                        catch
                        {
                        }
                    };
                }
            }
            catch
            {
            }
        }

        private void OnRequestJumpToLiveScroll()
        {
            try
            {
                // This is a deliberate override of the user's scroll position.
                // Jump-to-live should always re-enable live-follow and snap to the newest call.
                _followLive = true;
                _lastFirstVisibleIndex = int.MaxValue;

                // Some platforms will fire Scrolled events while we are forcing the list back
                // to the top. If we apply our "user scrolled away" detection during that window,
                // it can immediately flip _followLive back to false.
                _suppressScrollFollowDetectionUntilUtc = DateTime.UtcNow.AddMilliseconds(900);

                TrackTopItemIfNeeded();

                // Explicit user action: bypass the "already at top" optimization and
                // scroll twice to defeat layout/virtualization timing on some platforms.
                ForceScrollToTopWithRetry(firstDelayMs: 10, retryDelayMs: 140);
            }
            catch
            {
            }
        }


        private void AttachAutoFollowHandlers()
        {
            if (_handlersAttached)
                return;

            _handlersAttached = true;

            try
            {
                CallsView.Scrolled += OnCallsViewScrolled;
            }
            catch
            {
            }

            try
            {
                if (_viewModel?.Calls != null)
                    _viewModel.Calls.CollectionChanged += OnCallsCollectionChanged;
            }
            catch
            {
            }

            TrackTopItemIfNeeded();


            TrackAllCallItemsForResize();
        }

        private void DetachAutoFollowHandlers()
        {
            if (!_handlersAttached)
                return;

            _handlersAttached = false;

            try
            {
                CallsView.Scrolled -= OnCallsViewScrolled;
            }
            catch
            {
            }

            try
            {
                if (_viewModel?.Calls != null)
                    _viewModel.Calls.CollectionChanged -= OnCallsCollectionChanged;
            }
            catch
            {
            }

            UntrackAllCallItemsForResize();

            try
            {
                _viewModel?.RequestJumpToLiveScroll -= OnRequestJumpToLiveScroll;
            }
            catch
            {
            }

            CancelPendingScroll();
            UntrackTopItem();
        }

        private void OnCallsViewScrolled(object? sender, ItemsViewScrolledEventArgs e)
        {
            try
            {
                if (_isAutoScrolling)
                    return;

                // While Jump-to-live is actively forcing the list position, ignore scrolled
                // events so we do not incorrectly disable follow mode.
                if (DateTime.UtcNow < _suppressScrollFollowDetectionUntilUtc)
                    return;

                _lastFirstVisibleIndex = e.FirstVisibleItemIndex;

                var shouldFollow = e.FirstVisibleItemIndex <= 1;

                if (shouldFollow == _followLive)
                    return;

                _followLive = shouldFollow;

                if (_followLive)
                {
                    TrackTopItemIfNeeded();
                    RequestScrollToTop(delayMs: 10);
                }
                else
                {
                    CancelPendingScroll();
                    UntrackTopItem();
                }
            }
            catch
            {
                _followLive = true;
                TrackTopItemIfNeeded();
            }
        }

        private void OnCallsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_followLive)
            {
                UntrackTopItem();
                return;
            }

            TrackTopItemIfNeeded();

            HandleResizeTrackingForCollectionChange(e);

            if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0)
            {
                // Let the CollectionView finish its insert layout first, then scroll once.
                RequestScrollToTop(delayMs: 35);
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                RequestScrollToTop(delayMs: 35);
                return;
            }
        }

        private void TrackTopItemIfNeeded()
        {
            if (!_followLive)
                return;

            try
            {
                var calls = _viewModel?.Calls;
                if (calls == null || calls.Count == 0)
                {
                    UntrackTopItem();
                    return;
                }

                var newTop = calls[0];
                if (ReferenceEquals(newTop, _trackedTopItem))
                    return;

                UntrackTopItem();

                _trackedTopItem = newTop;
                _trackedTopItem.PropertyChanged += OnTopItemPropertyChanged;
            }
            catch
            {
            }
        }

        private void UntrackTopItem()
        {
            try
            {
                if (_trackedTopItem != null)
                    _trackedTopItem.PropertyChanged -= OnTopItemPropertyChanged;
            }
            catch
            {
            }
            finally
            {
                _trackedTopItem = null;
            }
        }

        private void OnTopItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!_followLive)
                return;

            if (e.PropertyName != nameof(CallItem.Transcription))
                return;

            // Keep this very small so it corrects offset drift without causing visible flashing.
            RequestScrollToTop(delayMs: 10);
        }

        private void RequestScrollToTop(int delayMs)
        {
            if (!_followLive)
                return;

            CancelPendingScroll();

            _pendingScrollCts = new CancellationTokenSource();
            var token = _pendingScrollCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (delayMs > 0)
                        await Task.Delay(delayMs, token);

                    if (token.IsCancellationRequested)
                        return;

                    if (!_followLive)
                        return;

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        try
                        {
                            if (token.IsCancellationRequested)
                                return;

                            if (!_followLive)
                                return;

                            // If we are already pinned to the top, do not force another scroll.
                            if (_lastFirstVisibleIndex == 0)
                                return;

                            ScrollToTopNoAnimCore();
                        }
                        catch
                        {
                        }
                    });
                }
                catch
                {
                }
            });
        }

        private void ForceScrollToTopWithRetry(int firstDelayMs, int retryDelayMs)
        {
            // Explicit user action (Jump-to-live): override any pending auto-follow scroll
            // and snap to the newest call even if the top item is already partially visible.
            CancelPendingScroll();

            _pendingScrollCts = new CancellationTokenSource();
            var token = _pendingScrollCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (firstDelayMs > 0)
                        await Task.Delay(firstDelayMs, token);

                    if (token.IsCancellationRequested)
                        return;

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        try
                        {
                            if (token.IsCancellationRequested)
                                return;

                            ScrollToTopNoAnimCore();
                        }
                        catch
                        {
                        }
                    });

                    if (retryDelayMs > 0)
                        await Task.Delay(retryDelayMs, token);

                    if (token.IsCancellationRequested)
                        return;

                    // Second pass helps when CollectionView virtualization/layout hasn't fully
                    // settled yet (observed on iOS/Android occasionally).
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        try
                        {
                            if (token.IsCancellationRequested)
                                return;

                            ScrollToTopNoAnimCore();
                        }
                        catch
                        {
                        }
                    });
                }
                catch
                {
                }
            });
        }
        private void CancelPendingScroll()
        {
            try
            {
                _pendingScrollCts?.Cancel();
                _pendingScrollCts?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _pendingScrollCts = null;
            }
        }

        private void ScrollToTopNoAnimCore()
        {
            
            if (!MainThread.IsMainThread)
            {
                MainThread.BeginInvokeOnMainThread(ScrollToTopNoAnimCore);
                return;
            }

            if (_isAutoScrolling)
                return;

            _isAutoScrolling = true;

            try
            {
                try
                {
                    // Use the item reference when possible. This tends to be less jarring than index scrolls.
                    var calls = _viewModel?.Calls;
                    if (calls != null && calls.Count > 0)
                    {
                        CallsView.ScrollTo(calls[0], position: ScrollToPosition.Start, animate: false);
                    }
                    else
                    {
                        CallsView.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
                    }
                }
                catch
                {
                }
            }
            finally
            {
                _isAutoScrolling = false;
            }
        }

        private void HandleResizeTrackingForCollectionChange(NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
                {
                    foreach (var obj in e.NewItems)
                    {
                        if (obj is CallItem item)
                            TrackCallItemForResize(item);
                    }
                }
                else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
                {
                    foreach (var obj in e.OldItems)
                    {
                        if (obj is CallItem item)
                            UntrackCallItemForResize(item);
                    }
                }
                else if (e.Action == NotifyCollectionChangedAction.Replace)
                {
                    if (e.OldItems != null)
                    {
                        foreach (var obj in e.OldItems)
                        {
                            if (obj is CallItem item)
                                UntrackCallItemForResize(item);
                        }
                    }

                    if (e.NewItems != null)
                    {
                        foreach (var obj in e.NewItems)
                        {
                            if (obj is CallItem item)
                                TrackCallItemForResize(item);
                        }
                    }
                }
                else if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    UntrackAllCallItemsForResize();
                    TrackAllCallItemsForResize();
                }
            }
            catch
            {
            }
        }

        private void TrackAllCallItemsForResize()
        {
            try
            {
                var calls = _viewModel?.Calls;
                if (calls == null)
                    return;

                foreach (var item in calls)
                {
                    TrackCallItemForResize(item);
                }
            }
            catch
            {
            }
        }

        private void TrackCallItemForResize(CallItem item)
        {
            try
            {
                if (item == null)
                    return;

                if (_resizeTrackedItems.Add(item))
                    item.PropertyChanged += OnCallItemPropertyChangedForResize;
            }
            catch
            {
            }
        }

        private void UntrackCallItemForResize(CallItem item)
        {
            try
            {
                if (item == null)
                    return;

                if (_resizeTrackedItems.Remove(item))
                    item.PropertyChanged -= OnCallItemPropertyChangedForResize;
            }
            catch
            {
            }
        }

        private void UntrackAllCallItemsForResize()
        {
            try
            {
                if (_resizeTrackedItems.Count == 0)
                    return;

                foreach (var item in _resizeTrackedItems.ToList())
                {
                    try
                    {
                        item.PropertyChanged -= OnCallItemPropertyChangedForResize;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _resizeTrackedItems.Clear();
            }
        }

        private void OnCallItemPropertyChangedForResize(object? sender, PropertyChangedEventArgs e)
        {
#if IOS
            try
            {
                if (e.PropertyName != nameof(CallItem.Transcription))
                    return;

                if (sender is not CallItem item)
                    return;

                if (!item.IsPlaying)
                    return;

                if (!ShouldInvalidateIosItemSizesNow())
                    return;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        CallsView.InvalidateMeasure();

                        if (CallsView.Handler?.PlatformView is UICollectionView cv)
                        {
                            cv.CollectionViewLayout?.InvalidateLayout();
                            cv.PerformBatchUpdates(() => { }, null);
                        }
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }
#endif
        }

#if IOS
        private bool ShouldInvalidateIosItemSizesNow()
        {
            try
            {
                var now = DateTime.UtcNow.Ticks;
                var last = Interlocked.Read(ref _lastIosMeasureInvalidationTicks);

                if (now - last < TimeSpan.FromMilliseconds(150).Ticks)
                    return false;

                Interlocked.Exchange(ref _lastIosMeasureInvalidationTicks, now);
                return true;
            }
            catch
            {
                return true;
            }
        }
#endif

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
                        await Shell.Current.Navigation.PopModalAsync();
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
                        await Shell.Current.Navigation.PopModalAsync();
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

                await Shell.Current.Navigation.PushModalAsync(new NavigationPage(page));
            }
            catch
            {
            }
        }

        private async void OnSiteTapped(object sender, EventArgs e)
        {
            try
            {
                await Launcher.Default.OpenAsync("https://www.joesscanner.com/");
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
                AppLog.Add(() => $"AddressDetection: open maps failed. {ex.Message}");
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
                AppLog.Add(() => $"AddressDetection: open maps failed. {ex.Message}");
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
                AppLog.Add(() => $"What3Words: open map failed. {ex.Message}");
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
                AppLog.Add(() => $"What3Words: copy failed. {ex.Message}");
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
                AppLog.Add(() => $"What3Words: copy coords failed. {ex.Message}");
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

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            AttachAutoFollowHandlers();

            if (BindingContext is MainViewModel vm)
            {
                await vm.TryAutoReconnectAsync();

                // iOS can miss WebSocket update notifications while the app is backgrounded.
                // When the user returns to the main page, refresh any calls still missing transcription.
                await vm.RefreshStaleTranscriptionsAsync();
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            DetachAutoFollowHandlers();
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
        try { CallsView?.ScrollTo(resolved, position: ScrollToPosition.Center, animate: true); } catch { }

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