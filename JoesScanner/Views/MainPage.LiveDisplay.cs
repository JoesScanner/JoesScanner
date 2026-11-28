using System.Collections.Specialized;
using System.ComponentModel;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using JoesScanner.Views.Controls;

namespace JoesScanner.Views;

public partial class MainPage
{
    private const string SeparateLiveKey = "live_separate_view";
    private const string FavoritesLiveKey = "live_favorites_view";
    private bool _favoritesLive;
    private LiveCallDisplay _liveDisplay = null!;
    private LiveReadingViewport _combinedViewport = null!;
    private LiveReadingViewport _favoritesViewport = null!;
    private CallItem? _observedPin;
    private readonly HashSet<LiveTranscriptLabel> _transcriptLabels = [];
#if WINDOWS
    private View _favoritesEmpty = null!;
#endif
    private long _readerGeneration;
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
        FavoritesCallsView.ReadingSource = _liveDisplay.Favorites;
#if WINDOWS
        // The WinUI ItemsView hosts the MAUI card factory. Keep the MAUI empty
        // view in the page's visual and accessibility tree beside it.
        _favoritesEmpty = (View)FavoritesCallsView.EmptyView;
        FavoritesCallsView.EmptyView = null;
        _favoritesEmpty.IsVisible = false;
        UnifiedLiveHost.Add(_favoritesEmpty, 0, 1);
#endif
        _combinedViewport = new(CallsView, CombinedNewCallsButton, _liveDisplay.CombinedReader);
        _favoritesViewport = new(FavoritesCallsView, FavoritesNewCallsButton, _liveDisplay.FavoritesReader);
        ServerPanels.SizeChanged += (_, _) => ShowServerPanels();
    }

    private void AttachLiveDisplay()
    {
        if (_readerGeneration != _viewModel.LiveDisplayGeneration)
        {
            _readerGeneration = _viewModel.LiveDisplayGeneration;
            _liveDisplay.ResetReaders();
        }
        _liveDisplay.Observe();
#if WINDOWS
        ((INotifyCollectionChanged)_liveDisplay.Favorites).CollectionChanged += OnFavoritesCollectionChanged;
#endif
        _viewModel.LiveCardAdmitted += _liveDisplay.RecordVisibleAdmission;
        _viewModel.RequestJumpToLiveScroll += OnRequestJumpToLiveScroll;
        _viewModel.PropertyChanged += OnLiveViewModelChanged;
        _viewModel.ScannerConnectionLayoutChanged += OnScannerConnectionLayoutChanged;
        _viewModel.Favorites.Changed += OnFavoriteMembershipChanged;
        _viewModel.RefreshScannerConnectionRows();
        OnScannerConnectionLayoutChanged();
    }

    private void DetachLiveDisplay()
    {
        _viewModel.LiveCardAdmitted -= _liveDisplay.RecordVisibleAdmission;
        _viewModel.RequestJumpToLiveScroll -= OnRequestJumpToLiveScroll;
        _viewModel.PropertyChanged -= OnLiveViewModelChanged;
        _viewModel.ScannerConnectionLayoutChanged -= OnScannerConnectionLayoutChanged;
        _viewModel.Favorites.Changed -= OnFavoriteMembershipChanged;
        _combinedViewport.Show(false);
        _favoritesViewport.Show(false);
        foreach (var panel in _serverPanels.Values)
            panel.Viewport.Show(false);
        RefreshPlaybackPins();
        RefreshTranscriptFeedback();
#if WINDOWS
        ((INotifyCollectionChanged)_liveDisplay.Favorites).CollectionChanged -= OnFavoritesCollectionChanged;
#endif
        _liveDisplay.Detach();
    }

    private void OnLiveViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveLiveCard))
        {
            RefreshPlaybackPins();
            return;
        }
        if (e.PropertyName != nameof(MainViewModel.IsRunning))
            return;
        _readerGeneration = _viewModel.LiveDisplayGeneration;
        _combinedViewport.CancelPendingScroll();
        _favoritesViewport.CancelPendingScroll();
        foreach (var panel in _serverPanels.Values)
            panel.Viewport.CancelPendingScroll();
        foreach (var label in _transcriptLabels)
            label.CancelCorrection();
    }

    private void RefreshPlaybackPins()
    {
        var active = _handlersAttached ? _viewModel.ActiveLiveCard : null;
        if (!ReferenceEquals(_observedPin, active))
        {
            if (_observedPin != null)
                _observedPin.PropertyChanged -= OnPinnedCardChanged;
            _observedPin = active;
            if (active != null)
                active.PropertyChanged += OnPinnedCardChanged;
        }
        var unified = UnifiedLiveHost.IsVisible ? _liveDisplay.PinFor(active, favoritesOnly: _favoritesLive) : null;
        var recorderName = unified?.ServerId is { } recorderId
            ? _viewModel.ScannerConnections.FirstOrDefault(row => row.Id == recorderId)?.Name : null;
        UnifiedPlaybackPin.Present(unified, recorderName);
        foreach (var (id, panel) in _serverPanels)
            panel.Pin.Present(_shownPanels.Contains(id) ? _liveDisplay.PinFor(active, recorder: id) : null);
    }

    private void OnPinnedCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _observedPin) && e.PropertyName == nameof(CallItem.CardMetadata))
            RefreshPlaybackPins();
    }

    private void OnLiveTranscriptLoaded(object? sender, EventArgs e)
    {
        if (sender is not LiveTranscriptLabel label)
            return;
        _transcriptLabels.Add(label);
        // A native template can load before MAUI attaches its logical parent.
        // The label verifies the active list when it receives a correction.
        label.ShowCorrectionFeedback(_handlersAttached);
    }

    private void OnLiveTranscriptUnloaded(object? sender, EventArgs e)
    {
        if (sender is not LiveTranscriptLabel label)
            return;
        _transcriptLabels.Remove(label);
        label.ShowCorrectionFeedback(false);
    }

    private void RefreshTranscriptFeedback()
    {
        foreach (var label in _transcriptLabels)
            label.ShowCorrectionFeedback(_handlersAttached && label.IsInVisibleList());
    }

    private void OnLiveCardSizeChanged(object? sender, EventArgs e)
    {
        if (!_handlersAttached || sender is not VisualElement { BindingContext: CallItem call })
            return;
        if (_favoritesLive)
            _favoritesViewport.CardSizeChanged(call);
        else if (!_liveDisplay.Separate)
            _combinedViewport.CardSizeChanged(call);
        else if (call.ServerId is { } id && _serverPanels.TryGetValue(id, out var panel))
            panel.Viewport.CardSizeChanged(call);
    }

    private void OnRequestJumpToLiveScroll()
    {
        if (_favoritesLive)
            _favoritesViewport.FollowLatest();
        else if (!_liveDisplay.Separate)
            _combinedViewport.FollowLatest();
        else
            foreach (var id in _shownPanels)
                _serverPanels[id].Viewport.FollowLatest();
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
        {
            _liveDisplay.RefreshFavorites();
            RefreshPlaybackPins();
        }
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
            if (_serverPanels.Remove(id, out var panel))
            {
                panel.Viewport.Show(false);
                panel.Pin.Present(null);
                ServerPanels.Children.Remove(panel.Root);
            }
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
                button.SetDynamicResource(StyleProperty, "PillButtonStyle");
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

#if WINDOWS
    private void OnFavoritesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateFavoritesEmptyView();

    private void UpdateFavoritesEmptyView() =>
        _favoritesEmpty.IsVisible = _favoritesLive && _liveDisplay.Favorites.Count == 0;
#endif

    private void RenderLiveDisplay()
    {
        var separate = !_favoritesLive && _liveDisplay.Separate;
        _viewModel.SetVisibleLiveView(separate ? _liveDisplay.SelectedServer : null,
            _favoritesLive ? _viewModel.Favorites.Contains : null);
        _favoritesViewport.Show(_handlersAttached && _favoritesLive);
        _combinedViewport.Show(_handlersAttached && !_favoritesLive && !separate);
        FavoritesCallsView.IsVisible = _favoritesLive;
#if WINDOWS
        UpdateFavoritesEmptyView();
#endif
        CallsView.IsVisible = !_favoritesLive && !separate;
        UnifiedLiveHost.IsVisible = !separate;
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
        // Swapping the whole style reapplies its shadow to an already parented WinUI button.
        // Selection only changes the two colors shared with the pill styles.
        button.SetDynamicResource(BackgroundColorProperty, selected ? "PillButtonBackground" : "SecondaryPillButtonBackground");
        button.SetDynamicResource(Button.BorderColorProperty, selected ? "PillButtonBorder" : "SecondaryPillButtonBorder");
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
        {
            foreach (var id in shown)
                _serverPanels[id].Viewport.Show(true);
            RefreshPlaybackPins();
            RefreshTranscriptFeedback();
            return;
        }

        var previous = _shownPanels;
        _shownPanels = shown;
        // Keep existing CollectionView handlers and reader positions when another
        // panel is added, removed or moved alongside them.
        foreach (var id in previous.Except(shown))
        {
            if (!_serverPanels.TryGetValue(id, out var panel))
                continue;
            panel.Viewport.Show(false);
            ServerPanels.Children.Remove(panel.Root);
        }
        ServerPanels.ColumnDefinitions.Clear();
        for (var i = 0; i < shown.Length; i++)
        {
            ServerPanels.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var id = shown[i];
            var panel = GetLivePanel(id);
            Grid.SetColumn(panel.Root, i);
            if (!previous.Contains(id))
                ServerPanels.Children.Add(panel.Root);
            panel.Viewport.Show(true);
        }
        RefreshPlaybackPins();
        RefreshTranscriptFeedback();
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
        var header = new LiveRecorderHeader(id, name, statusGroup);

        var calls = new LiveCollectionView
        {
            AutomationId = $"LiveServer-{id:N}",
            ItemTemplate = CallsView.ItemTemplate,
            ReadingSource = cards,
            SelectionMode = SelectionMode.None,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            VerticalScrollBarVisibility = ScrollBarVisibility.Never
        };
        var root = new Grid
        {
            BindingContext = row,
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)]
        };
        var pin = new LivePlaybackPinView();
        root.Add(header);
        root.Add(pin, 0, 1);
        root.Add(calls, 0, 2);
#if ANDROID || IOS
        foreach (var direction in new[] { SwipeDirection.Left, SwipeDirection.Right })
        {
            var swipe = new SwipeGestureRecognizer { Direction = direction };
            swipe.Swiped += OnServerPanelSwipe;
            root.GestureRecognizers.Add(swipe);
        }
#endif

        panel = new LivePanel(root, new(calls, header.NewCalls, _liveDisplay.ReaderForServer(id)), pin);
        _serverPanels.Add(id, panel);
        return panel;
    }

    private void ScrollToAddressCall(CallItem call)
    {
        if (_favoritesLive && _liveDisplay.Favorites.Contains(call))
        {
            _favoritesViewport.Reveal(call);
            return;
        }
        if (_favoritesLive)
            SetLiveDisplay(false, save: false);
        if (_liveDisplay.Separate && call.ServerId is { } id && _liveDisplay.Servers.Contains(id))
        {
            SelectLiveServer(id);
            GetLivePanel(id).Viewport.Reveal(call);
            return;
        }
        if (_liveDisplay.Separate)
            SetLiveDisplay(separate: false, save: false);
        _combinedViewport.Reveal(call);
    }

    private sealed record LivePanel(Grid Root, LiveReadingViewport Viewport, LivePlaybackPinView Pin);
}
