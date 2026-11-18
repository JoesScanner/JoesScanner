using System.Diagnostics;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Devices;
using Microsoft.Maui.ApplicationModel;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace JoesScanner.Views;

public partial class RootPage : ContentPage
{
    private readonly IServiceProvider _services;

    private readonly Dictionary<AppTab, ContentPage> _pages = new();
    private readonly Dictionary<AppTab, View> _views = new();

    private static bool UseHostedViewCaching =>
        DeviceInfo.Platform != DevicePlatform.iOS;

    private static bool UsePersistentTabHosts =>
        DeviceInfo.Platform == DevicePlatform.WinUI;

    private AppTab _current = AppTab.Main;

    private ContentPage? _activePage;
    private View? _activeView;
    private bool _hasAppearedOnce;
    private bool _rootVisible;
    private bool _settingsWarmupQueued;
    private bool _settingsWarmupStarted;
    private View? _mainLayoutView;
    private SettingsPage? _preparedSettingsPage;
    private IElementHandler? _settingsHandlerOnHide;

    private int _navigationOverlayDepth;
    private int _navigationRequestVersion;

    private int _initialNavigationStarted;

    public RootPage(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _rootVisible = true;

        TabNavigationService.Instance.TabRequested -= OnTabRequested;
        TabNavigationService.Instance.TabRequested += OnTabRequested;

        // iOS can tear down native handlers across a kill and relaunch cycle.
        // Reusing cached hosted views can then crash when MAUI tries to reattach them.
        // Only rebuild hosted tabs when the active host is actually empty.
        //
        // Rebuilding on every re-appearance can clear ContentHost after returning from a modal
        // picker and leave only the tab/header area visible until the user taps the tab again.
        if (_hasAppearedOnce && DeviceInfo.Platform == DevicePlatform.iOS && !HasActiveHostedContent())
        {
            try
            {
                RebuildAllTabs();
            }
            catch (Exception ex)
            {
                LogNavError($"RootPage.RebuildAllTabs failed: {ex}");
            }
        }

        _hasAppearedOnce = true;
        // Do not allow navigation exceptions to kill the app.
        //
        // iOS is more stable if the hosted-content attach happens one dispatcher turn later,
        // after the native page handler is fully ready.
        try
        {
            if (DeviceInfo.Platform == DevicePlatform.iOS)
            {
                _ = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        await Task.Yield();
                        await Task.Delay(1);

                        // First appearance: perform the normal initial attach once.
                        if (Interlocked.Exchange(ref _initialNavigationStarted, 1) == 0)
                        {
                            SwitchTo(tab: _current);
                            return;
                        }

                        // Later iOS appearances, including returning from modal pickers:
                        // if the hosted content was cleared, reattach the current tab.
                        if (!HasActiveHostedContent())
                        {
                            LogNavError($"RootPage.OnAppearing detected empty host on iOS. Reattaching {_current}.");
                            SwitchTo(tab: _current);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogNavError($"RootPage.OnAppearing deferred SwitchTo({_current}) failed: {ex}");
                        _ = SafeAlertAsync("Navigation error", $"Settings navigation failed:\n{FormatNavException(ex)}");
                    }
                });
            }
            else
            {
                SwitchTo(tab: _current);
            }
        }
        catch (Exception ex)
        {
            LogNavError($"RootPage.OnAppearing initial SwitchTo({_current}) failed: {ex}");
            _ = SafeAlertAsync("Navigation error", $"Settings navigation failed:\n{FormatNavException(ex)}");
        }
        if (_current == AppTab.Main && _activeView is { } mainView)
            QueueSettingsWarmupAfterLayout(mainView);
    }

    protected override void OnDisappearing()
    {
        _rootVisible = false;
        StopWaitingForMainLayout();
        TabNavigationService.Instance.TabRequested -= OnTabRequested;
        base.OnDisappearing();
    }

    private void QueueSettingsWarmupAfterLayout(View view)
    {
        if (!_rootVisible || _settingsWarmupStarted || _settingsWarmupQueued)
            return;

        if (view.Handler == null || !view.IsLoaded || view.Width <= 0 || view.Height <= 0)
        {
            if (_mainLayoutView == view)
                return;

            StopWaitingForMainLayout();
            _mainLayoutView = view;
            view.SizeChanged += OnMainLayoutChanged;
            view.Loaded += OnMainLayoutChanged;
            return;
        }

        StopWaitingForMainLayout();
        _settingsWarmupQueued = true;
        if (!Dispatcher.Dispatch(StartSettingsWarmup))
            _settingsWarmupQueued = false;
    }

    private void OnMainLayoutChanged(object? sender, EventArgs e)
    {
        if (_current == AppTab.Main && sender is View view && view == _activeView)
            QueueSettingsWarmupAfterLayout(view);
    }

    private void StopWaitingForMainLayout()
    {
        if (_mainLayoutView is not { } view)
            return;

        view.SizeChanged -= OnMainLayoutChanged;
        view.Loaded -= OnMainLayoutChanged;
        _mainLayoutView = null;
    }

    private void StartSettingsWarmup()
    {
        _settingsWarmupQueued = false;
        if (!_rootVisible || _current != AppTab.Main || _settingsWarmupStarted)
            return;

        _settingsWarmupStarted = true;
        try
        {
            _ = _services.GetRequiredService<SettingsViewModel>().PrepareLocalDataAsync();

            // Managed XAML needs the dispatcher. Give queued input and playback callbacks priority.
#if WINDOWS
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PrepareSettingsPage);
#elif ANDROID
            Android.OS.Looper.MyQueue()?.AddIdleHandler(new SettingsIdleHandler(this));
#endif
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Settings timing: startup preparation failed ({ex.GetType().Name}).");
        }
    }

    private void PrepareSettingsPage()
    {
        if (!_rootVisible || _current != AppTab.Main || _pages.ContainsKey(AppTab.Settings))
            return;

        try
        {
            var start = Stopwatch.GetTimestamp();
            var page = _services.GetRequiredService<SettingsPage>();
            if (page.Handler == null && page.Content is { Handler: null })
                _preparedSettingsPage = page;

            var elapsed = Stopwatch.GetElapsedTime(start);
            AppLog.Add(() => $"Settings timing: idle managed preparation={elapsed.TotalMilliseconds:F1} ms, reusable={_preparedSettingsPage != null}.");
#if WINDOWS
            if (_preparedSettingsPage != null)
            {
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, HostPreparedWindowsSettings);
            }
#endif
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Settings timing: managed preparation failed ({ex.GetType().Name}).");
        }
    }

#if WINDOWS
    private void HostPreparedWindowsSettings()
    {
        if (!_rootVisible || _current != AppTab.Main || _preparedSettingsPage == null ||
            _pages.ContainsKey(AppTab.Settings) || ContentHost.Handler == null)
            return;

        // The collapsed page goes directly into its permanent host. Keep it hidden and leave
        // the Settings lifecycle dormant until selection. Other platforms use their existing path.
        View? candidate = null;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var (_, view, origin) = GetOrCreate(AppTab.Settings);
            candidate = view;
            if (AppLog.IsEnabled)
                LogSettingsStage($"idle obtain page ({origin})", started);
            AddPersistentHostedView(view, isSettings: true, operation: "idle");
            var elapsed = Stopwatch.GetElapsedTime(started);
            AppLog.Add(() => $"Settings timing: idle permanent host preparation={elapsed.TotalMilliseconds:F1} ms, handler ready={view.Handler != null}.");
        }
        catch (Exception ex)
        {
            _pages.Remove(AppTab.Settings);
            _views.Remove(AppTab.Settings);
            if (candidate != null && ContentHost.Children.Contains(candidate))
            {
                try { ContentHost.Children.Remove(candidate); }
                catch (Exception cleanupError)
                {
                    AppLog.Add(() => $"Settings timing: rollback of failed idle host add failed ({cleanupError.GetType().Name}).");
                }
            }
            AppLog.Add(() => $"Settings timing: idle permanent host preparation failed ({ex.GetType().Name}).");
        }
    }
#endif

#if ANDROID
    private sealed class SettingsIdleHandler(RootPage root) : Java.Lang.Object, Android.OS.MessageQueue.IIdleHandler
    {
        private readonly WeakReference<RootPage> _root = new(root);

        public bool QueueIdle()
        {
            if (_root.TryGetTarget(out var page))
                page.PrepareSettingsPage();

            return false;
        }
    }
#endif

    private async void OnTabRequested(object? sender, AppTab tab)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => SwitchToWithFeedbackAsync(tab));
        }
        catch (Exception ex)
        {
            LogNavError($"RootPage.OnTabRequested({tab}) failed: {ex}");
            _ = SafeAlertAsync("Navigation error", $"Switching tabs failed:\n{FormatNavException(ex)}");
        }
    }

    private async Task SwitchToWithFeedbackAsync(AppTab tab)
    {
        var version = ++_navigationRequestVersion;
        // Settings no longer depends on overlay animation to finish loading its data.
        if (_current == AppTab.Settings || tab == AppTab.Settings)
        {
            SwitchTo(tab);
            return;
        }

        var shouldShowOverlay = _current != tab;

        if (!shouldShowOverlay)
        {
            await MainThread.InvokeOnMainThreadAsync(() => SwitchTo(tab));
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(ShowNavigationFeedbackAsync);

        try
        {
            if (version == _navigationRequestVersion)
                SwitchTo(tab);
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(HideNavigationFeedbackAsync);
        }
    }

    private async Task ShowNavigationFeedbackAsync()
    {
        try
        {
            Interlocked.Increment(ref _navigationOverlayDepth);

            if (NavigationFeedbackOverlay == null)
                return;

            NavigationFeedbackOverlay.IsVisible = true;
            NavigationFeedbackOverlay.InputTransparent = false;

            if (NavigationFeedbackSpinner != null)
            {
                NavigationFeedbackSpinner.IsVisible = false;
                NavigationFeedbackSpinner.IsRunning = false;
            }

            await NavigationFeedbackOverlay.FadeTo(0.14, 70, Easing.CubicOut);

            await Task.Yield();

            if (NavigationFeedbackSpinner != null)
            {
                NavigationFeedbackSpinner.IsVisible = true;
                NavigationFeedbackSpinner.IsRunning = true;
            }
        }
        catch
        {
        }
    }

    private async Task HideNavigationFeedbackAsync()
    {
        try
        {
            if (Interlocked.Decrement(ref _navigationOverlayDepth) > 0)
                return;

            if (NavigationFeedbackOverlay == null)
                return;

            if (NavigationFeedbackSpinner != null)
            {
                NavigationFeedbackSpinner.IsRunning = false;
                NavigationFeedbackSpinner.IsVisible = false;
            }

            await NavigationFeedbackOverlay.FadeTo(0, 90, Easing.CubicIn);
            NavigationFeedbackOverlay.InputTransparent = true;
            NavigationFeedbackOverlay.IsVisible = false;
        }
        catch
        {
            try
            {
                if (NavigationFeedbackSpinner != null)
                {
                    NavigationFeedbackSpinner.IsRunning = false;
                    NavigationFeedbackSpinner.IsVisible = false;
                }

                if (NavigationFeedbackOverlay != null)
                {
                    NavigationFeedbackOverlay.Opacity = 0;
                    NavigationFeedbackOverlay.InputTransparent = true;
                    NavigationFeedbackOverlay.IsVisible = false;
                }
            }
            catch
            {
            }
        }
    }

    private ContentPage CreatePage(AppTab tab)
    {
        return tab switch
        {
            AppTab.Main => _services.GetRequiredService<MainPage>(),
            AppTab.History => _services.GetRequiredService<HistoryPage>(),
            // Archive is intentionally mapped to History for now.
            // We keep the enum and route so we can later switch this to a longer range
            // without reworking tab navigation.
            AppTab.Archive => _services.GetRequiredService<HistoryPage>(),
            AppTab.Stats => _services.GetRequiredService<StatsPage>(),
            AppTab.Communications => _services.GetRequiredService<CommunicationsPage>(),
            AppTab.Settings => _services.GetRequiredService<SettingsPage>(),
            _ => _services.GetRequiredService<MainPage>()
        };
    }

    private (ContentPage page, View view, string origin) GetOrCreate(AppTab tab)
    {
        if (UseHostedViewCaching && _pages.TryGetValue(tab, out var existingPage) && _views.TryGetValue(tab, out var existingView))
            return (existingPage, existingView, "host cache");

        var prepared = tab == AppTab.Settings ? _preparedSettingsPage : null;
        if (tab == AppTab.Settings)
            _preparedSettingsPage = null;
        var reusePrepared = prepared is { Handler: null, Content: { Handler: null } };
        var page = reusePrepared ? prepared! : CreatePage(tab);
        var view = page.Content ?? new ContentView();

        // Detach so we can host it inside RootPage.
        page.Content = null;

        // Preserve BindingContext inheritance that the view would normally get from the page.
        try
        {
            if (!ReferenceEquals(view.BindingContext, page.BindingContext))
                view.BindingContext = page.BindingContext;
        }
        catch
        {
        }

        if (UseHostedViewCaching)
        {
            _pages[tab] = page;
            _views[tab] = view;
        }

        return (page, view, reusePrepared ? "idle-managed page" : "new page");
    }

    private void SwitchTo(AppTab tab)
    {
        // Guard the entire switch path so a single exception cannot abort the app on iOS.
        try
        {
            SwitchToCore(tab);
        }
        catch (Exception ex)
        {
            LogNavError($"RootPage.SwitchTo({tab}) failed: {ex}");
            _ = SafeAlertAsync(
                "Navigation error",
                $"Unable to open {tab}:\n\n{FormatNavException(ex)}\n\nFULL:\n{ex}");
        }
    }

    private void SwitchToCore(AppTab tab)
    {
        if (_current == tab && HasActiveHostedContent())
        {
            // Even if the user is already on this tab, treat it as a show event.
            if (_activePage is { } currentPage)
            {
                try
                {
                    if (currentPage is ITabShowingAware showingAware)
                        showingAware.OnTabShowing();
                }
                catch
                {
                }

                try { currentPage.SendAppearing(); } catch { }
            }

            return;
        }

        var previousTab = _current;
        var trace = AppLog.IsEnabled && (previousTab == AppTab.Settings || tab == AppTab.Settings);
        var started = Stopwatch.GetTimestamp();
        var (page, view, origin) = GetOrCreate(tab);
        if (trace)
            LogSettingsStage($"{previousTab} to {tab} obtain page and view ({origin})", started);
        var stageStarted = Stopwatch.GetTimestamp();
        StopWaitingForMainLayout();

        var leavingSettings = previousTab == AppTab.Settings ? _activeView : null;
        if (leavingSettings != null && UsePersistentTabHosts)
            _settingsHandlerOnHide = leavingSettings.Handler;

        if (_activePage is { } oldPage)
        {
            if (oldPage is ITabHidingAware hidingAware)
                hidingAware.OnTabHiding();

            if (trace)
                LogSettingsStage($"{previousTab} OnTabHiding", stageStarted);
            stageStarted = Stopwatch.GetTimestamp();
            oldPage.SendDisappearing();
            if (trace)
                LogSettingsStage($"{previousTab} SendDisappearing", stageStarted);
        }

        stageStarted = Stopwatch.GetTimestamp();
        _activePage = page;
        _activeView = view;
        _current = tab;
        TabStrip.SelectedTab = tab;

        var settingsPage = page as SettingsPage;
        var isSettings = settingsPage != null;
        var handlerBefore = view.Handler;
        var alreadyHosted = ReferenceEquals(view.Parent, ContentHost);
        if (trace)
        {
            LogSettingsStage("tab selection and state", stageStarted);
            if (isSettings)
            {
                var retained = handlerBefore != null && ReferenceEquals(handlerBefore, _settingsHandlerOnHide);
                AppLog.Add(() => $"Settings navigation: view already hosted={alreadyHosted}, handler before activation={handlerBefore != null}, same handler as previous visit={retained}.");
            }
        }
        settingsPage?.TraceFirstHostAttachment(view, started);

        if (UsePersistentTabHosts)
        {
            AddPersistentHostedView(view, isSettings, operation: "navigation");
            stageStarted = Stopwatch.GetTimestamp();
            ActivatePersistentHostedView(view);
            if (trace)
                LogSettingsStage("persistent host visibility and activation", stageStarted);
        }
        else
        {
            stageStarted = Stopwatch.GetTimestamp();
            ContentHost.Children.Clear();
            if (trace)
                LogSettingsStage("remove outgoing hosted view", stageStarted);
            AddHostedView(view, isSettings, operation: "navigation");
        }

        if (trace)
        {
            if (isSettings)
                AppLog.Add(() => $"Settings navigation: handler after activation={view.Handler != null}, same as before activation={handlerBefore != null && ReferenceEquals(handlerBefore, view.Handler)}.");
            if (leavingSettings != null)
                AppLog.Add(() => $"Settings navigation: after hiding, still hosted={ReferenceEquals(leavingSettings.Parent, ContentHost)}, same handler={_settingsHandlerOnHide != null && ReferenceEquals(_settingsHandlerOnHide, leavingSettings.Handler)}.");
        }

        // In this app, many tabs are hosted by detaching a page's Content and reparenting the
        // resulting view inside RootPage. That hosting pattern is fast, but the normal MAUI page
        // lifecycle is not reliable enough to be the only signal for tab visibility.
        //
        // Give pages an explicit tab-shown callback first, then still send the normal appearing
        // event as a best-effort fallback for handlers that rely on it.
        stageStarted = Stopwatch.GetTimestamp();
        try
        {
            if (page is ITabShowingAware showingAware)
                showingAware.OnTabShowing();
        }
        catch
        {
        }

        if (trace)
            LogSettingsStage($"{tab} OnTabShowing", stageStarted);
        stageStarted = Stopwatch.GetTimestamp();
        page.SendAppearing();
        if (trace)
            LogSettingsStage($"{tab} SendAppearing", stageStarted);
        settingsPage?.CheckFirstVisibleLayout();
        if (tab == AppTab.Main)
            QueueSettingsWarmupAfterLayout(view);
        if (trace)
        {
            LogSettingsStage($"{previousTab} to {tab} synchronous total", started);
            Dispatcher.Dispatch(() => AppLog.Add(() => $"Settings navigation: dispatcher resumed after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms."));
        }
    }

    private static void LogSettingsStage(string stage, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        AppLog.Add(() => $"Settings navigation: {stage}={elapsed.TotalMilliseconds:F1} ms.");
    }
    
    private bool HasActiveHostedContent()
    {
        try
        {
            if (ContentHost == null)
                return false;

            if (UsePersistentTabHosts)
            {
                foreach (var child in ContentHost.Children)
                {
                    if (child is View childView && childView.IsVisible)
                        return true;
                }

                return false;
            }

            return ContentHost.Children.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void AddPersistentHostedView(View targetView, bool isSettings, string operation)
    {
        if (ReferenceEquals(targetView.Parent, ContentHost))
            return;

        if (isSettings && targetView.Parent != null)
            throw new InvalidOperationException("Settings content already belongs to another host.");

        try
        {
            if (targetView.Parent is Layout existingParent && existingParent != ContentHost)
            {
                existingParent.Remove(targetView);
            }
        }
        catch
        {
        }

        if (!ContentHost.Children.Contains(targetView))
        {
            var started = Stopwatch.GetTimestamp();
            targetView.IsVisible = false;
            if (isSettings)
            {
                targetView.InputTransparent = true;
                if (AppLog.IsEnabled)
                    LogSettingsStage($"{operation} set Settings hidden before host add", started);
            }
            AddHostedView(targetView, isSettings, operation);
        }
    }

    private void AddHostedView(View view, bool isSettings, string operation)
    {
        if (!isSettings || !AppLog.IsEnabled)
        {
            ContentHost.Children.Add(view);
            return;
        }

        var instrumented = Stopwatch.GetTimestamp();
        using var handlers = view.Handler == null ? new SettingsHandlerTrace(view, operation) : null;
        LogSettingsStage($"{operation} set up Settings handler tracing", instrumented);
        var started = Stopwatch.GetTimestamp();
        if (handlers != null)
            handlers.AttachStarted = started;
        ContentHost.Children.Add(view);
        LogSettingsStage($"{operation} ContentHost.Children.Add(Settings)", started);
    }

    private void ActivatePersistentHostedView(View targetView)
    {
        foreach (var child in ContentHost.Children)
        {
            if (child is not View childView)
                continue;

            var isTarget = ReferenceEquals(childView, targetView);

            childView.InputTransparent = !isTarget;
            childView.IsVisible = isTarget;
        }

        try
        {
            targetView.ZIndex = 1;
        }
        catch
        {
        }
    }

    private void RebuildAllTabs()
    {
        // Detach current content first to prevent reparenting old views while we clear caches.
        try
        {
            ContentHost.Children.Clear();
        }
        catch
        {
        }

        if (_activePage is ITabHidingAware hidingAware)
            hidingAware.OnTabHiding();
        _activePage?.SendDisappearing();
        _activePage = null;
        _activeView = null;
        StopWaitingForMainLayout();

        _pages.Clear();
        _views.Clear();
        _settingsHandlerOnHide = null;
    }

    private static string FormatNavException(Exception ex)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            var depth = 0;
            for (var e = ex; e != null && depth < 6; e = e.InnerException, depth++)
            {
                var indent = new string(' ', depth * 2);
                sb.Append(indent);
                sb.Append(e.GetType().Name);
                sb.Append(": ");
                sb.Append(e.Message);

                try
                {
                    sb.Append("  HResult=0x");
                    sb.Append(e.HResult.ToString("X8"));
                }
                catch
                {
                }

                sb.AppendLine();

                // Include a short stack to identify the failing component.
                try
                {
                    if (!string.IsNullOrWhiteSpace(e.StackTrace))
                    {
                        var lines = e.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        var take = Math.Min(lines.Length, 12);
                        for (var i = 0; i < take; i++)
                        {
                            sb.Append(indent);
                            sb.Append("  at ");
                            sb.AppendLine(lines[i].Trim());
                        }
                    }
                }
                catch
                {
                }
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void LogNavError(string message)
    {
        try
        {
            AppLog.DebugWriteLine(message);
        }
        catch
        {
        }
    }

    private static Task SafeAlertAsync(string title, string message)
    {
        try
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    await UiDialogs.AlertAsync(title, message, "OK");
                }
                catch
                {
                }
            });
        }
        catch
        {
            return Task.CompletedTask;
        }
    }
}
