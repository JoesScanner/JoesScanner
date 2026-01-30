using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

#if IOS
using UIKit;
#endif

namespace JoesScanner.Views
{
    public partial class MainPage : ContentPage
    {
        private readonly MainViewModel _viewModel;

        private bool _handlersAttached;
        private bool _followLive = true;

        private bool _isAutoScrolling;
        private int _lastFirstVisibleIndex;

        private CallItem? _trackedTopItem;


        private readonly HashSet<CallItem> _resizeTrackedItems = new();
        private long _lastIosMeasureInvalidationTicks;
        private CancellationTokenSource? _pendingScrollCts;

        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            BindingContext = _viewModel;

            AttachAutoFollowHandlers();
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

            CancelPendingScroll();
            UntrackTopItem();
        }

        private void OnCallsViewScrolled(object sender, ItemsViewScrolledEventArgs e)
        {
            try
            {
                if (_isAutoScrolling)
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

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    if (delayMs > 0)
                        await Task.Delay(delayMs, token);

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
    }
}
