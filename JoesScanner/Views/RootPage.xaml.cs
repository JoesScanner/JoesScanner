using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Devices;
using Microsoft.Maui.ApplicationModel;

namespace JoesScanner.Views;

public partial class RootPage : ContentPage
{
    private readonly IServiceProvider _services;

    private readonly Dictionary<AppTab, ContentPage> _pages = new();
    private readonly Dictionary<AppTab, View> _views = new();

    private AppTab _current = AppTab.Main;

    private bool _hasAppearedOnce;

    public RootPage(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        TabNavigationService.Instance.TabRequested -= OnTabRequested;
        TabNavigationService.Instance.TabRequested += OnTabRequested;

        // iOS can tear down native handlers across a kill and relaunch cycle.
        // Reusing cached hosted views can then crash when MAUI tries to reattach them.
        // To keep Settings and other tabs stable, recreate the hosted tab content after the first appearance.
        if (_hasAppearedOnce && DeviceInfo.Platform == DevicePlatform.iOS)
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
        try
        {
            SwitchTo(tab: _current);
        }
        catch (Exception ex)
        {
            LogNavError($"RootPage.OnAppearing initial SwitchTo({_current}) failed: {ex}");
            _ = SafeAlertAsync("Navigation error", $"Settings navigation failed: {ex.Message}");
        }
    }

    protected override void OnDisappearing()
    {
        TabNavigationService.Instance.TabRequested -= OnTabRequested;
        base.OnDisappearing();
    }

    private void OnTabRequested(object? sender, AppTab tab)
    {
        try
        {
            SwitchTo(tab);
        }
        catch (Exception ex)
        {
            LogNavError($"RootPage.OnTabRequested({tab}) failed: {ex}");
            _ = SafeAlertAsync("Navigation error", $"Switching tabs failed: {ex.Message}");
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

    private (ContentPage page, View view) GetOrCreate(AppTab tab)
    {
        if (_pages.TryGetValue(tab, out var existingPage) && _views.TryGetValue(tab, out var existingView))
            return (existingPage, existingView);

        var page = CreatePage(tab);
        var view = page.Content ?? new ContentView();

        // Detach so we can host it inside RootPage.
        page.Content = null;

        // Preserve BindingContext inheritance that the view would normally get from the page.
        try
        {
            view.BindingContext = page.BindingContext;
        }
        catch
        {
        }

        _pages[tab] = page;
        _views[tab] = view;

        return (page, view);
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
            _ = SafeAlertAsync("Navigation error", $"Unable to open {tab}: {ex.Message}");
        }
    }

    private void SwitchToCore(AppTab tab)
    {
        if (_current == tab && ContentHost.Content != null)
        {
            // Even if the user is already on this tab, treat it as a show event.
            if (_pages.TryGetValue(_current, out var currentPage))
            {
                try { currentPage.SendAppearing(); } catch { }
            }

            return;
        }

        // Tell the previous page it is going away so it can detach handlers.
        if (_pages.TryGetValue(_current, out var oldPage))
        {
            try
            {
                if (oldPage is ITabHidingAware hidingAware)
                    hidingAware.OnTabHiding();
            }
            catch
            {
            }

            try { oldPage.SendDisappearing(); } catch { }
        }

        _current = tab;

        try
        {
            TabStrip.SelectedTab = tab;
        }
        catch
        {
        }

        var (page, view) = GetOrCreate(tab);

        ContentHost.Content = view;

        // Tell the new page it is visible so it can attach handlers and refresh data.
        try { page.SendAppearing(); } catch { }
    }

    private void RebuildAllTabs()
    {
        // Detach current content first to prevent reparenting old views while we clear caches.
        try
        {
            ContentHost.Content = null;
        }
        catch
        {
        }

        foreach (var kvp in _pages)
        {
            try { kvp.Value.SendDisappearing(); } catch { }
        }

        _pages.Clear();
        _views.Clear();
    }

    private static void LogNavError(string message)
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
                    var page = Application.Current?.MainPage;
                    if (page != null)
                        await page.DisplayAlert(title, message, "OK");
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
