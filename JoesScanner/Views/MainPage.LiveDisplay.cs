using System.Collections.ObjectModel;
using System.Collections.Specialized;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;

namespace JoesScanner.Views;

public partial class MainPage
{
    private const string SeparateLiveKey = "live_separate_view";
    private const string FavoritesLiveKey = "live_favorites_view";
    private bool _favoritesLive;
    private LiveCallDisplay _liveDisplay = null!;
    private readonly Dictionary<Guid, LivePanel> _serverPanels = [];
    private readonly Dictionary<Guid, (View Tab, Button Button)> _serverTabs = [];
    private Guid[] _shownPanels = [];

#if WINDOWS || MACCATALYST
    private const bool UseSideBySide = true;
#else
    private const bool UseSideBySide = false;
#endif

    private void InitializeLiveDisplay()
    {
        _favoritesLive = AppStateStore.GetBool(FavoritesLiveKey, false);
        _liveDisplay = new LiveCallDisplay(_viewModel.Calls, AppStateStore.GetBool(SeparateLiveKey, false), _viewModel.Favorites.IsSaved);
        FavoritesCallsView.ItemTemplate = CallsView.ItemTemplate;
        FavoritesCallsView.ItemsSource = _liveDisplay.Favorites;
        ServerPanels.SizeChanged += (_, _) => ShowServerPanels();
    }

    private void AttachLiveDisplay()
    {
        _liveDisplay.Observe();
        _viewModel.ScannerConnectionLayoutChanged += OnScannerConnectionLayoutChanged;
        _viewModel.Favorites.Changed += OnFavoriteMembershipChanged;
        _viewModel.RefreshScannerConnectionRows();
        OnScannerConnectionLayoutChanged();
    }

    private void DetachLiveDisplay()
    {
        _viewModel.ScannerConnectionLayoutChanged -= OnScannerConnectionLayoutChanged;
        _viewModel.Favorites.Changed -= OnFavoriteMembershipChanged;
        _liveDisplay.Detach();
    }

    private void OnCombinedLiveClicked(object? sender, EventArgs e) => SetLiveDisplay(separate: false);
    private void OnSeparateLiveClicked(object? sender, EventArgs e)
    {
        SetLiveDisplay(separate: true);
        if (_liveDisplay.SelectedServer is { } id)
            _viewModel.ChooseLiveAudioSource(LiveAudioSource.Recorder(id));
    }

    private async void OnLiveAudioSourceClicked(object? sender, EventArgs e)
    {
        var recorders = _viewModel.ScannerConnections.Where(row => row.Enabled).ToArray();
        var duplicateNames = recorders.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = recorders.Select(row => (Source: LiveAudioSource.Recorder(row.Id),
                Text: duplicateNames.Contains(row.Name) ? $"Recorder · {row.Name} ({row.Id.ToString("N")[..8]})" : $"Recorder · {row.Name}"))
            .Append((Source: LiveAudioSource.Favorites, Text: "Favorites")).ToArray();
        try
        {
            var selected = await UiDialogs.ActionSheetAsync("Automatic Playback Source", "Cancel", null,
                choices.Select(choice => choice.Text).ToArray());
            var choice = choices.FirstOrDefault(choice => choice.Text == selected);
            if (_handlersAttached && _viewModel.IsSelectedLiveAudioMode && choice.Text != null)
                _viewModel.ChooseLiveAudioSource(choice.Source);
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Could not choose the Live audio source ({ex.GetType().Name}).");
        }
    }

    private void OnFavoriteMembershipChanged() => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_handlersAttached)
            _liveDisplay.RefreshFavorites();
    });

    private void OnFavoritesLiveClicked(object? sender, EventArgs e)
    {
        _favoritesLive = true;
        AppStateStore.SetBool(FavoritesLiveKey, true);
        _viewModel.ChooseLiveAudioSource(LiveAudioSource.Favorites);
        RenderLiveDisplay();
    }

    private async void OnFavoriteCallClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: CallItem call })
            return;
        try
        {
            var favorites = _viewModel.Favorites;
            favorites.SetCall(favorites.OwnerKey, call, !favorites.IsSaved(call));
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"The Live Favorite could not be saved ({ex.GetType().Name}).");
            await UiDialogs.AlertAsync("Favorites", "This Favorite could not be saved. Try again.", "Close");
        }
    }

    private void SetLiveDisplay(bool separate, bool save = true)
    {
        if (_favoritesLive && save)
            AppStateStore.SetBool(FavoritesLiveKey, false);
        _favoritesLive = false;
        if (_liveDisplay.Separate != separate)
        {
            _liveDisplay.SetSeparate(separate);
            if (save)
                AppStateStore.SetBool(SeparateLiveKey, separate);
        }
        RenderLiveDisplay();
    }

    private void OnScannerConnectionLayoutChanged()
    {
        var configured = _viewModel.ScannerConnections.ToDictionary(row => row.Id);
        _liveDisplay.SetServers(_viewModel.ScannerConnections.Where(row => row.Enabled).Select(row => row.Id));
        foreach (var id in _serverPanels.Keys.Where(id => !configured.ContainsKey(id)).ToArray())
        {
            _serverPanels.Remove(id);
            _liveDisplay.ForgetServer(id);
        }

        UpdateServerTabs(configured);
        RenderLiveDisplay();
    }

    private void UpdateServerTabs(IReadOnlyDictionary<Guid, ScannerConnectionRow> configured)
    {
        var ids = _liveDisplay.Servers;
        foreach (var id in _serverTabs.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            ServerTabs.Children.Remove(_serverTabs[id].Tab);
            _serverTabs.Remove(id);
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (!_serverTabs.TryGetValue(id, out var tab))
            {
                var button = new Button { FontSize = 12 };
                button.SetBinding(Button.TextProperty, nameof(ScannerConnectionRow.Name));
                button.Clicked += (_, _) =>
                {
                    SelectLiveServer(id);
                    _viewModel.ChooseLiveAudioSource(LiveAudioSource.Recorder(id));
                };
                var status = CreateServerStatusIndicator(10);
                status.HorizontalOptions = LayoutOptions.Center;
                tab = (new VerticalStackLayout { BindingContext = configured[id], Spacing = 1, Children = { button, status } }, button);
                _serverTabs.Add(id, tab);
            }

            var current = ServerTabs.Children.IndexOf(tab.Tab);
            if (current == i)
                continue;
            if (current >= 0)
                ServerTabs.Children.RemoveAt(current);
            ServerTabs.Children.Insert(i, tab.Tab);
        }
    }

    private static HorizontalStackLayout CreateServerStatusIndicator(double fontSize)
    {
        var status = new Label { FontSize = fontSize, VerticalOptions = LayoutOptions.Center };
        status.SetBinding(Label.TextProperty, nameof(ScannerConnectionRow.StatusText));
        var dot = new BoxView { WidthRequest = 8, HeightRequest = 8, CornerRadius = 4, VerticalOptions = LayoutOptions.Center };
        dot.SetBinding(BoxView.ColorProperty, nameof(ScannerConnectionRow.StatusColor));
        return new HorizontalStackLayout { Spacing = 4, Children = { dot, status } };
    }

    private void RenderLiveDisplay()
    {
        var separate = !_favoritesLive && _liveDisplay.Separate;
        _viewModel.SetVisibleLiveView(separate ? _liveDisplay.SelectedServer : null,
            _favoritesLive ? _viewModel.Favorites.Contains : null);
        FavoritesCallsView.IsVisible = _favoritesLive;
        CallsView.IsVisible = !_favoritesLive && !separate;
        SeparateLiveHost.IsVisible = separate;
        ApplyLiveButtonStyle(CombinedLiveButton, CallsView.IsVisible);
        ApplyLiveButtonStyle(SeparateLiveButton, separate);
        ApplyLiveButtonStyle(FavoritesLiveButton, _favoritesLive);
        foreach (var (id, tab) in _serverTabs)
            ApplyLiveButtonStyle(tab.Button, id == _liveDisplay.SelectedServer);
        ShowServerPanels();
    }

    private static void ApplyLiveButtonStyle(Button button, bool selected)
    {
        var key = selected ? "PillButtonStyle" : "SecondaryPillButtonStyle";
        if (Application.Current?.Resources.TryGetValue(key, out var style) == true && style is Style buttonStyle)
            button.Style = buttonStyle;
    }

    private void SelectLiveServer(Guid id)
    {
        if (!_liveDisplay.Select(id))
            return;
        RenderLiveDisplay();
        ScrollSelectedTabIntoView();
    }

    private void OnServerPanelSwipe(object? sender, SwipedEventArgs e)
    {
        if (_liveDisplay.MoveSelection(e.Direction == SwipeDirection.Left ? 1 : -1))
        {
            if (_liveDisplay.SelectedServer is { } id)
                _viewModel.ChooseLiveAudioSource(LiveAudioSource.Recorder(id));
            RenderLiveDisplay();
            ScrollSelectedTabIntoView();
        }
    }

    private void ScrollSelectedTabIntoView()
    {
        if (ServerTabsScroll.IsVisible && _liveDisplay.SelectedServer is { } id && _serverTabs.TryGetValue(id, out var tab))
            _ = ServerTabsScroll.ScrollToAsync(tab.Tab, ScrollToPosition.MakeVisible, animated: false);
    }

    private void ShowServerPanels()
    {
        if (!_handlersAttached)
            return;
        var shown = _favoritesLive ? [] : _liveDisplay.VisibleServers(SeparateLiveHost.Width, UseSideBySide);
        NoEnabledRecorders.IsVisible = _liveDisplay.Separate && shown.Length == 0;
        ServerTabsScroll.IsVisible = _liveDisplay.Separate &&
            (UseSideBySide ? _liveDisplay.Servers.Count > shown.Length : _liveDisplay.Servers.Count > 1);
        if (shown.SequenceEqual(_shownPanels))
            return;

        var previous = _shownPanels;
        _shownPanels = shown;
        // Keep existing CollectionView handlers and reader positions when another
        // panel is added, removed or moved alongside them.
        foreach (var id in previous.Except(shown))
        {
            var panel = ServerPanels.Children.OfType<View>().FirstOrDefault(view =>
                view.BindingContext is ScannerConnectionRow row && row.Id == id);
            if (panel != null)
                ServerPanels.Children.Remove(panel);
        }
        ServerPanels.ColumnDefinitions.Clear();
        for (var i = 0; i < shown.Length; i++)
        {
            ServerPanels.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var id = shown[i];
            var panel = GetLivePanel(id);
            Grid.SetColumn(panel.Root, i);
            if (!previous.Contains(id))
            {
                ServerPanels.Children.Add(panel.Root);
                QueuePanelScroll(panel, panel.FollowLive ? null : panel.Anchor);
            }
        }
    }

    private LivePanel GetLivePanel(Guid id)
    {
        if (_serverPanels.TryGetValue(id, out var panel))
            return panel;

        var row = _viewModel.ScannerConnections.First(connection => connection.Id == id);
        var cards = _liveDisplay.ForServer(id);
        var name = new Label { FontAttributes = FontAttributes.Bold, FontSize = 12, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(ScannerConnectionRow.Name));
        var statusGroup = CreateServerStatusIndicator(11);
        var header = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 5,
            Padding = new Thickness(4, 2)
        };
        header.Add(name);
        header.Add(statusGroup, 1, 0);

        var calls = new CollectionView
        {
            AutomationId = $"LiveServer-{id:N}",
            ItemTemplate = CallsView.ItemTemplate,
            ItemsSource = cards,
            SelectionMode = SelectionMode.None,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            VerticalScrollBarVisibility = ScrollBarVisibility.Never
        };
        var root = new Grid
        {
            BindingContext = row,
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)]
        };
        root.Add(header);
        root.Add(calls, 0, 1);
#if ANDROID || IOS
        foreach (var direction in new[] { SwipeDirection.Left, SwipeDirection.Right })
        {
            var swipe = new SwipeGestureRecognizer { Direction = direction };
            swipe.Swiped += OnServerPanelSwipe;
            root.GestureRecognizers.Add(swipe);
        }
#endif

        panel = new LivePanel(id, root, calls, cards);
        _serverPanels.Add(id, panel);
        calls.Scrolled += (_, e) =>
        {
            if (!_shownPanels.Contains(id) || DateTime.UtcNow < panel.IgnoreScrollUntil ||
                e.FirstVisibleItemIndex < 0 || e.FirstVisibleItemIndex >= cards.Count)
                return;
            panel.FollowLive = e.FirstVisibleItemIndex <= 1;
            panel.Anchor = cards[e.FirstVisibleItemIndex];
        };
        ((INotifyCollectionChanged)cards).CollectionChanged += (_, e) =>
        {
            if (panel.FollowLive && e is { Action: NotifyCollectionChangedAction.Add, NewStartingIndex: 0 })
                QueuePanelScroll(panel);
        };
        return panel;
    }

    private void QueuePanelScroll(LivePanel panel, CallItem? target = null, bool center = false)
    {
        if (!_handlersAttached || !_shownPanels.Contains(panel.Id))
            return;
        var revision = ++panel.ScrollRevision;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(35), () =>
        {
            if (!_handlersAttached || !_shownPanels.Contains(panel.Id) || panel.Calls.Handler == null ||
                revision != panel.ScrollRevision || panel.Cards.Count == 0 || target != null && !panel.Cards.Contains(target))
                return;
            if (target == null && !panel.FollowLive)
                return;
            panel.IgnoreScrollUntil = DateTime.UtcNow.AddMilliseconds(200);
            panel.Calls.ScrollTo(target ?? panel.Cards[0], position: center ? ScrollToPosition.Center : ScrollToPosition.Start,
                animate: center);
        });
    }

    private void ScrollSeparatePanelsToLive()
    {
        foreach (var id in _shownPanels)
        {
            var panel = _serverPanels[id];
            panel.FollowLive = true;
            QueuePanelScroll(panel);
        }
    }

    private void ScrollToAddressCall(CallItem call)
    {
        if (_favoritesLive && _liveDisplay.Favorites.Contains(call))
        {
            FavoritesCallsView.ScrollTo(call, position: ScrollToPosition.Center, animate: true);
            return;
        }
        if (_favoritesLive)
            SetLiveDisplay(false, save: false);
        if (_liveDisplay.Separate && call.ServerId is { } id && _liveDisplay.Servers.Contains(id))
        {
            SelectLiveServer(id);
            var panel = GetLivePanel(id);
            panel.FollowLive = false;
            panel.Anchor = call;
            QueuePanelScroll(panel, call, center: true);
            return;
        }
        if (_liveDisplay.Separate)
            SetLiveDisplay(separate: false, save: false);
        CallsView.ScrollTo(call, position: ScrollToPosition.Center, animate: true);
    }

    private sealed class LivePanel(Guid id, Grid root, CollectionView calls, ReadOnlyObservableCollection<CallItem> cards)
    {
        internal Guid Id { get; } = id;
        internal Grid Root { get; } = root;
        internal CollectionView Calls { get; } = calls;
        internal ReadOnlyObservableCollection<CallItem> Cards { get; } = cards;
        internal bool FollowLive { get; set; } = true;
        internal CallItem? Anchor { get; set; }
        internal DateTime IgnoreScrollUntil { get; set; }
        internal int ScrollRevision { get; set; }
    }
}
