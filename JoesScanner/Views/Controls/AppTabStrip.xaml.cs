using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui.Dispatching;

namespace JoesScanner.Views.Controls;

public partial class AppTabStrip : ContentView
{
    // Static sizing. Adjust these two numbers to taste.
    private const double StaticTabIconSize = 70d;
    private const double StaticStripHeight = 70d;
    private readonly IDispatcher _viewDispatcher;

    public static readonly BindableProperty SelectedTabProperty = BindableProperty.Create(
        nameof(SelectedTab),
        typeof(AppTab),
        typeof(AppTabStrip),
        AppTab.Main);

    public static readonly BindableProperty TabIconSizeProperty = BindableProperty.Create(
        nameof(TabIconSize),
        typeof(double),
        typeof(AppTabStrip),
        StaticTabIconSize);

    public static readonly BindableProperty MainIconSizeProperty = BindableProperty.Create(
        nameof(MainIconSize),
        typeof(double),
        typeof(AppTabStrip),
        StaticTabIconSize);

    public static readonly BindableProperty TabStripHeightProperty = BindableProperty.Create(
        nameof(TabStripHeight),
        typeof(double),
        typeof(AppTabStrip),
        StaticStripHeight);

    public static readonly BindableProperty AutoSizeEnabledProperty = BindableProperty.Create(
        nameof(AutoSizeEnabled),
        typeof(bool),
        typeof(AppTabStrip),
        false,
        propertyChanged: (bindable, _, _) => ((AppTabStrip)bindable).UpdateSizing());

    public static readonly BindableProperty TitleTextProperty = BindableProperty.Create(
        nameof(TitleText),
        typeof(string),
        typeof(AppTabStrip),
        "JoesScanner");

    public static readonly BindableProperty SubTextProperty = BindableProperty.Create(
        nameof(SubText),
        typeof(string),
        typeof(AppTabStrip),
        "Hear the Action, Know the Story!");

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public string SubText
    {
        get => (string)GetValue(SubTextProperty);
        set => SetValue(SubTextProperty, value);
    }

    public AppTab SelectedTab
    {
        get => (AppTab)GetValue(SelectedTabProperty);
        set => SetValue(SelectedTabProperty, value);
    }

    public double TabIconSize
    {
        get => (double)GetValue(TabIconSizeProperty);
        set => SetValue(TabIconSizeProperty, value);
    }

    public double MainIconSize
    {
        get => (double)GetValue(MainIconSizeProperty);
        set => SetValue(MainIconSizeProperty, value);
    }

    public double TabStripHeight
    {
        get => (double)GetValue(TabStripHeightProperty);
        set => SetValue(TabStripHeightProperty, value);
    }

    public bool AutoSizeEnabled
    {
        get => (bool)GetValue(AutoSizeEnabledProperty);
        set => SetValue(AutoSizeEnabledProperty, value);
    }

    public AppTabStrip()
    {
        _viewDispatcher = Dispatcher;
        InitializeComponent();

        // The control can be created before a Handler (and therefore MauiContext.Services) exists.
        // If we only attempt DI resolution once, the comms badge may never attach, which prevents
        // the button background from updating when new messages arrive.
        Loaded += (_, _) => BeginAttachCommsBadge();
        Loaded += (_, _) => StartHistoryIconWatcher();
        Unloaded += (_, _) =>
        {
            StopAttachCommsBadge();
            StopHistoryIconWatcher();
            DetachCommsBadge();
        };

        HandlerChanged += (_, _) =>
        {
            if (Handler == null)
            {
                StopAttachCommsBadge();
                StopHistoryIconWatcher();
                DetachCommsBadge();
                return;
            }

            BeginAttachCommsBadge();
            StartHistoryIconWatcher();
        };

        // Keep the hooks in place in case you ever re-enable AutoSizeEnabled,
        // but UpdateSizing will apply static sizing when AutoSizeEnabled is false.
        SizeChanged += (_, _) => UpdateSizing();
        Loaded += (_, _) => UpdateSizing();

        UpdateSizing();
    }

    private IDispatcherTimer? _attachTimer;
    private int _attachAttempts;

    private ICommsBadgeService? _commsBadge;

    private CancellationTokenSource? _historyIconCts;

    private void BeginAttachCommsBadge()
    {
        if (TryAttachCommsBadge())
        {
            StopAttachCommsBadge();
            return;
        }
        if (_attachTimer != null)
            return;

        // Services can still be unavailable before the handler finishes attaching.
        _attachAttempts = 0;
        _attachTimer = _viewDispatcher.CreateTimer();
        _attachTimer.Interval = TimeSpan.FromMilliseconds(250);
        _attachTimer.IsRepeating = true;
        _attachTimer.Tick += OnAttachTimerTick;
        _attachTimer.Start();
    }

    private void OnAttachTimerTick(object? sender, EventArgs e)
    {
        if (TryAttachCommsBadge() || ++_attachAttempts == 20)
            StopAttachCommsBadge();
    }

    private void StopAttachCommsBadge()
    {
        if (_attachTimer is not { } timer)
            return;

        _attachTimer = null;
        timer.Stop();
        timer.Tick -= OnAttachTimerTick;
    }

    private bool TryAttachCommsBadge()
    {
        try
        {
            if (_commsBadge == null)
            {
                var services = Handler?.MauiContext?.Services ?? Application.Current?.Handler?.MauiContext?.Services;
                _commsBadge = services?.GetService(typeof(ICommsBadgeService)) as ICommsBadgeService;
                if (_commsBadge == null)
                    return false;

                _commsBadge.Changed += OnCommsBadgeChanged;
            }

            UpdateCommsButtonBadge();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.DebugWriteLine(() => $"Comms badge attachment failed: {ex.GetType().Name}");
            return false;
        }
    }

    private void DetachCommsBadge()
    {
        if (_commsBadge is not { } badge)
            return;

        _commsBadge = null;
        badge.Changed -= OnCommsBadgeChanged;
    }

    private void OnCommsBadgeChanged()
    {
        if (_viewDispatcher.IsDispatchRequired)
            _viewDispatcher.Dispatch(UpdateAttachedCommsBadge);
        else
            UpdateAttachedCommsBadge();
    }

    private void UpdateAttachedCommsBadge()
    {
        if (_commsBadge != null)
            UpdateCommsButtonBadge();
    }

    private void UpdateCommsButtonBadge()
    {
        if (CommunicationsButton == null)
            return;

        var hasUnread = _commsBadge?.HasUnread == true;

        // When unread, inset the icon slightly so the green background remains visible.
        // Use a fixed inset in pixels so the effect is consistent across icon sizes.
        const double unreadInsetPx = 2d;
        CommunicationsButton.Padding = hasUnread
            ? new Thickness(unreadInsetPx)
            : new Thickness(0);

        // When new messages exist, tint the button background green.
        CommunicationsButton.BackgroundColor = hasUnread
            ? Color.FromArgb("#16a34a")
            : Colors.Transparent;
    }

    private void UpdateSizing()
    {
        if (_viewDispatcher.IsDispatchRequired)
        {
            _viewDispatcher.Dispatch(UpdateSizing);
            return;
        }

        // Static sizing mode (default).
        if (!AutoSizeEnabled)
        {
            if (Math.Abs(TabIconSize - StaticTabIconSize) > 0.1)
            {
                TabIconSize = StaticTabIconSize;
            }

            if (Math.Abs(MainIconSize - StaticTabIconSize) > 0.1)
            {
                MainIconSize = StaticTabIconSize;
            }

            if (Math.Abs(TabStripHeight - StaticStripHeight) > 0.1)
            {
                TabStripHeight = StaticStripHeight;
            }

            return;
        }

        // Responsive sizing mode (optional).
        var width = Width;
        if (width <= 0)
        {
            return;
        }

        const int tabCount = 6;
        const double minSize = 50d;
        const double maxSize = 70d;

        // Conservative estimate of how much horizontal space to allocate per tab.
        var perTab = width / tabCount;

        // Map perTab to icon size with some padding.
        var target = Math.Clamp(perTab - 8d, minSize, maxSize);

        TabIconSize = target;
        MainIconSize = target;
        TabStripHeight = target;
    }

    private void NavigateTo(string route)
    {
        var tab = route switch
        {
            "//main" => AppTab.Main,
            "//history" => AppTab.History,
            "//archive" => AppTab.History,
            "//stats" => AppTab.Stats,
            "//communications" => AppTab.Communications,            "//settings" => AppTab.Settings,
            _ => AppTab.Main
        };

        try
        {
            SelectedTab = tab;
        }
        catch
        {
        }

        // Viewing the Communications tab counts as reading messages, clear unread state immediately.
        if (tab == AppTab.Communications)
        {
            try
            {
                // Clear the green background right away, even before the page finishes appearing.
                if (CommunicationsButton != null)
                {
                    CommunicationsButton.Padding = new Thickness(0);
                    CommunicationsButton.BackgroundColor = Colors.Transparent;
                }

                _commsBadge?.MarkAllKnownAsSeen();
            }
            catch
            {
            }

            try
            {
                UpdateCommsButtonBadge();
            }
            catch
            {
            }
        }

        try
        {
            TabNavigationService.Instance.Request(tab);
        }
        catch (Exception ex)
        {
            try
            {
                AppLog.DebugWriteLine(() => $"AppTabStrip.NavigateTo({tab}) failed: {ex}");
            }
            catch
            {
            }

            try
            {
                _ = _viewDispatcher.DispatchAsync(async () =>
                {
                    try
                    {
                        await UiDialogs.AlertAsync("Navigation error", $"Unable to open {tab}: {ex.Message}", "OK");
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }
        }
    }

    // TapGestureRecognizer handlers (Border + Image approach)
    private void OnMainTapped(object sender, TappedEventArgs e) => NavigateTo("//main");
    private void OnHistoryTapped(object sender, TappedEventArgs e) => NavigateTo("//history");
    private void OnArchiveTapped(object sender, TappedEventArgs e) => NavigateTo("//history");
    private void OnStatsTapped(object sender, TappedEventArgs e) => NavigateTo("//stats");
    private void OnCommunicationsTapped(object sender, TappedEventArgs e) => NavigateTo("//communications");
    private void OnSettingsTapped(object sender, TappedEventArgs e) => NavigateTo("//settings");

    // Legacy Clicked handlers (kept in case anything else still wires to them)
    private void OnMainClicked(object sender, EventArgs e) => NavigateTo("//main");
    private void OnHistoryClicked(object sender, EventArgs e) => NavigateTo("//history");
    private void OnArchiveClicked(object sender, EventArgs e) => NavigateTo("//history");
    private void OnStatsClicked(object sender, EventArgs e) => NavigateTo("//stats");
    private void OnCommunicationsClicked(object sender, EventArgs e) => NavigateTo("//communications");
    private void OnSettingsClicked(object sender, EventArgs e) => NavigateTo("//settings");


    private void StartHistoryIconWatcher()
    {
        try
        {
            if (_historyIconCts != null)
                return;

            if (Handler?.MauiContext?.Services.GetService<ISettingsService>() is not { } settings)
                return;

            _historyIconCts = new CancellationTokenSource();
            _ = RunHistoryIconLoopAsync(settings, _historyIconCts.Token);
        }
        catch
        {
        }
    }

    private void StopHistoryIconWatcher()
    {
        try
        {
            _historyIconCts?.Cancel();
            _historyIconCts?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _historyIconCts = null;
        }
    }

    private async Task RunHistoryIconLoopAsync(ISettingsService settings, CancellationToken ct)
    {
        string? previous = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var isHosted = IsHostedJoeServerSelected(settings.ServerUrl);
                var isFullAccess = !isHosted || settings.SubscriptionTierLevel >= 2;
                var desired = isFullAccess ? "mc_history_full.png" : "mc_history.png";

                if (desired != previous && _viewDispatcher.Dispatch(() =>
                    {
                        if (!ct.IsCancellationRequested)
                            HistoryButton.Source = desired;
                    }))
                {
                    previous = desired;
                }
            }
            catch (Exception ex)
            {
                AppLog.DebugWriteLine(() => $"History tab icon refresh failed: {ex.GetType().Name}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static bool IsHostedJoeServerSelected(string? serverUrl)
    {
        var raw = (serverUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;

        return string.Equals(uri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);
    }

}
