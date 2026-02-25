using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui.ApplicationModel;

namespace JoesScanner.Views.Controls;

public partial class AppTabStrip : ContentView
{
    // Static sizing. Adjust these two numbers to taste.
    private const double StaticTabIconSize = 55d;
    private const double StaticStripHeight = 55d;

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

        HandlerChanged += (_, _) => BeginAttachCommsBadge();
        HandlerChanged += (_, _) => StartHistoryIconWatcher();

        // Keep the hooks in place in case you ever re-enable AutoSizeEnabled,
        // but UpdateSizing will apply static sizing when AutoSizeEnabled is false.
        SizeChanged += (_, _) => UpdateSizing();
        Loaded += (_, _) => UpdateSizing();

        UpdateSizing();
    }

    private CancellationTokenSource? _attachCts;

    private ICommsBadgeService? _commsBadge;

    private ISettingsService? _settingsService;

    private CancellationTokenSource? _historyIconCts;
    private string _lastHistoryIcon = string.Empty;

    private void BeginAttachCommsBadge()
    {
        if (_commsBadge != null)
        {
            UpdateCommsButtonBadge();
            return;
        }

        StopAttachCommsBadge();
        _attachCts = new CancellationTokenSource();

        // Retry for a short period until the MauiContext services become available.
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 20 && !_attachCts.IsCancellationRequested; i++)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try { TryAttachCommsBadge(); } catch { }
                    });

                    if (_commsBadge != null)
                        break;

                    await Task.Delay(250, _attachCts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        });
    }

    private void StopAttachCommsBadge()
    {
        try { _attachCts?.Cancel(); } catch { }
        try { _attachCts?.Dispose(); } catch { }
        _attachCts = null;
    }

    private void TryAttachCommsBadge()
    {
        if (_commsBadge != null)
        {
            UpdateCommsButtonBadge();
            return;
        }

        try
        {
            // Prefer this control's Handler, fall back to Application handler.
            var services = Handler?.MauiContext?.Services ?? Application.Current?.Handler?.MauiContext?.Services;
            _commsBadge = services?.GetService(typeof(ICommsBadgeService)) as ICommsBadgeService;

            if (_commsBadge == null)
                return;

            _commsBadge.Changed += OnCommsBadgeChanged;
            UpdateCommsButtonBadge();
        }
        catch
        {
        }
    }

    private void DetachCommsBadge()
    {
        try
        {
            if (_commsBadge != null)
                _commsBadge.Changed -= OnCommsBadgeChanged;
        }
        catch
        {
        }
    }

    private void OnCommsBadgeChanged()
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(UpdateCommsButtonBadge);
        }
        catch
        {
        }
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
            "//communications" => AppTab.Communications,
            "//log" => AppTab.Log,
            "//settings" => AppTab.Settings,
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
                System.Diagnostics.Debug.WriteLine($"AppTabStrip.NavigateTo({tab}) failed: {ex}");
                Console.WriteLine($"AppTabStrip.NavigateTo({tab}) failed: {ex}");
            }
            catch
            {
            }

            try
            {
                _ = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        var page = Application.Current?.MainPage;
                        if (page != null)
                            await page.DisplayAlert("Navigation error", $"Unable to open {tab}: {ex.Message}", "OK");
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
    private void OnLogTapped(object sender, TappedEventArgs e) => NavigateTo("//log");
    private void OnSettingsTapped(object sender, TappedEventArgs e) => NavigateTo("//settings");

    // Legacy Clicked handlers (kept in case anything else still wires to them)
    private void OnMainClicked(object sender, EventArgs e) => NavigateTo("//main");
    private void OnHistoryClicked(object sender, EventArgs e) => NavigateTo("//history");
    private void OnArchiveClicked(object sender, EventArgs e) => NavigateTo("//history");
    private void OnStatsClicked(object sender, EventArgs e) => NavigateTo("//stats");
    private void OnCommunicationsClicked(object sender, EventArgs e) => NavigateTo("//communications");
    private void OnLogClicked(object sender, EventArgs e) => NavigateTo("//log");
    private void OnSettingsClicked(object sender, EventArgs e) => NavigateTo("//settings");


    private void StartHistoryIconWatcher()
    {
        try
        {
            if (_historyIconCts != null)
                return;

            if (Handler?.MauiContext?.Services == null)
                return;

            _settingsService ??= Handler.MauiContext.Services.GetService<ISettingsService>();
            if (_settingsService == null)
                return;

            _historyIconCts = new CancellationTokenSource();
            _ = RunHistoryIconLoopAsync(_historyIconCts.Token);
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

    private async Task RunHistoryIconLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                UpdateHistoryIcon();
            }
            catch
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch
            {
                break;
            }
        }
    }

    private void UpdateHistoryIcon()
    {
        if (HistoryButton == null || _settingsService == null)
            return;

        var isHosted = IsHostedJoeServerSelected(_settingsService.ServerUrl);

        // Custom servers are always treated as full access.
        var isFullAccess = !isHosted || _settingsService.SubscriptionTierLevel >= 2;

        var desired = isFullAccess ? "mc_history_full.png" : "mc_history.png";
        if (string.Equals(desired, _lastHistoryIcon, StringComparison.OrdinalIgnoreCase))
            return;

        _lastHistoryIcon = desired;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (HistoryButton != null)
                    HistoryButton.Source = desired;
            }
            catch
            {
            }
        });
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