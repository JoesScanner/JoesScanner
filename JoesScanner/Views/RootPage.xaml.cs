using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Devices;
using Microsoft.Maui.ApplicationModel;
using System;
using System.Threading;

namespace JoesScanner.Views;

public partial class RootPage : ContentPage
{
    private readonly IServiceProvider _services;

    private readonly Dictionary<AppTab, ContentPage> _pages = new();
    private readonly Dictionary<AppTab, View> _views = new();

    private static bool UseHostedViewCaching => DeviceInfo.Platform != DevicePlatform.iOS;

    private AppTab _current = AppTab.Main;

    private bool _hasAppearedOnce;

    private int _warmupStarted;

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
            _ = SafeAlertAsync("Navigation error", $"Settings navigation failed:\n{FormatNavException(ex)}");
        }

        // Warm up local storage and settings caches.
        //
        // Goals:
        // - Remove the one-time first-open hitch when the user taps Settings.
        // - Do not block initial navigation or audio playback.
        //
        // Strategy:
        // - Pre-create the Settings tab UI (XAML tree) on the UI thread after first render.
        // - Prime SQLite and filter profile reads on a background thread.
        // - Resolve and cache app data paths once to avoid repeated WinRT exceptions on Windows.
        if (Interlocked.Exchange(ref _warmupStarted, 1) == 0)
        {
            // Pre-create Settings tab UI as soon as the UI is ready so the first tap is instant.
            //
            // IMPORTANT:
            // iOS App Store / TestFlight builds are much less tolerant of reparenting a native-backed
            // MAUI visual tree between hidden and visible hosts. Keep the warmup path off on iOS and
            // let iOS create views only when they are actually shown.
            if (DeviceInfo.Platform != DevicePlatform.iOS)
            {
                _ = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        // Keep this delay very small. In production, users can often tap Settings
                        // quickly after launch, so we want the heavy XAML tree built early.
                        await Task.Yield();
                        await Task.Delay(50);
                        View settingsView;
                        if (DeviceInfo.Platform == DevicePlatform.WinUI)
                        {
                            // Use a throwaway Settings instance for warmup so we never re-host the same View.
                            var tmpPage = _services.GetRequiredService<SettingsPage>();
                            settingsView = tmpPage.Content ?? new ContentView();
                            tmpPage.Content = null;
                            try { settingsView.BindingContext = tmpPage.BindingContext; } catch { }
                        }
                        else
                        {
                            var (_settingsPage, _settingsView) = GetOrCreate(AppTab.Settings);
                            settingsView = _settingsView;
                        }

                        // Creating the Settings page is not enough to eliminate the first-open hitch.
                        // The hitch often comes from the first time MAUI creates native handlers and
                        // runs the initial layout pass for the Settings visual tree.
                        //
                        // WarmupHost is an invisible, tiny host that forces handler creation and a
                        // first layout pass without changing the visible UI.
                        if (WarmupHost != null)
                        {
                            try
                            {
                                WarmupHost.Content = settingsView;

                                // Give MAUI a moment to attach handlers and measure/arrange.
                                await Task.Delay(20);
                            }
                            finally
                            {
                                // Detach so the real tab switch can host the view later.
                                WarmupHost.Content = null;
                            }
                        }
                    }
                    catch
                    {
                    }
                });
            }

            // Prime DB and profile reads off the UI thread.
            _ = Task.Run(async () =>
            {
                try
                {
                    _ = AppPaths.GetAppDataDirectorySafe();

                    var settings = _services.GetRequiredService<ISettingsService>();
                    var serverKey = NormalizeServerKey(settings.ServerUrl);

                    var store = _services.GetRequiredService<IFilterProfileStore>();
                    _ = await store.GetProfilesForServerAsync(serverKey, CancellationToken.None);
                }
                catch
                {
                }
            });
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
            _ = SafeAlertAsync("Navigation error", $"Switching tabs failed:\n{FormatNavException(ex)}");
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
        if (UseHostedViewCaching && _pages.TryGetValue(tab, out var existingPage) && _views.TryGetValue(tab, out var existingView))
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

        if (UseHostedViewCaching)
        {
            _pages[tab] = page;
            _views[tab] = view;
        }

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
            _ = SafeAlertAsync(
                "Navigation error",
                $"Unable to open {tab}:\n\n{FormatNavException(ex)}\n\nFULL:\n{ex}");
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

        var previousTab = _current;

        // Tell the previous page it is going away so it can detach handlers.
        if (_pages.TryGetValue(previousTab, out var oldPage))
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

        
        // WinUI can throw COMException 0x800F1000 when re-attaching a previously hosted view
        // that has already been attached to a different parent (WarmupHost, ContentHost, etc).
        // To keep Settings stable on Windows, always recreate the Settings tab view.
        if (DeviceInfo.Platform == DevicePlatform.WinUI && tab == AppTab.Settings)
        {
            try
            {
                _pages.Remove(AppTab.Settings);
                _views.Remove(AppTab.Settings);
            }
            catch
            {
            }
        }

        // iOS release builds are particularly sensitive to reusing a detached native-backed view.
        // Recreate the outgoing and incoming hosted views each time instead of caching them.
        if (!UseHostedViewCaching)
        {
            try
            {
                _pages.Remove(previousTab);
                _views.Remove(previousTab);
                _pages.Remove(tab);
                _views.Remove(tab);
            }
            catch
            {
            }
        }

        var (page, view) = GetOrCreate(tab);

        ContentHost.Content = null;
        ContentHost.Content = view;

        // Tell the new page it is visible so it can attach handlers and refresh data.
        try { page.SendAppearing(); } catch { }
    }

    
    private static string NormalizeServerKey(string? serverUrl)
    {
        var raw = (serverUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        raw = raw.TrimEnd('/');

        try
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return raw;

            var builder = new UriBuilder(uri)
            {
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
                Port = uri.IsDefaultPort ? -1 : uri.Port
            };

            return builder.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return raw;
        }
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
