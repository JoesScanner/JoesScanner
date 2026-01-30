using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JoesScanner.Views;

public partial class RootPage : ContentPage
{
    private readonly IServiceProvider _services;

    private readonly Dictionary<AppTab, ContentPage> _pages = new();
    private readonly Dictionary<AppTab, View> _views = new();

    private AppTab _current = AppTab.Main;

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

        // Ensure initial content is present.
        SwitchTo(_current);
    }

    protected override void OnDisappearing()
    {
        TabNavigationService.Instance.TabRequested -= OnTabRequested;
        base.OnDisappearing();
    }

    private void OnTabRequested(object? sender, AppTab tab)
    {
        SwitchTo(tab);
    }

    private ContentPage CreatePage(AppTab tab)
    {
        return tab switch
        {
            AppTab.Main => _services.GetRequiredService<MainPage>(),
            AppTab.History => _services.GetRequiredService<HistoryPage>(),
            AppTab.Archive => _services.GetRequiredService<ArchivePage>(),
            AppTab.Stats => _services.GetRequiredService<StatsPage>(),
            AppTab.Communications => _services.GetRequiredService<CommunicationsPage>(),
            AppTab.Log => _services.GetRequiredService<LogPage>(),
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
        if (_current == tab && ContentHost.Content != null)
            return;

        // Tell the previous "page" it is going away so its timers/handlers can detach.
        if (_pages.TryGetValue(_current, out var oldPage))
        {
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

        // Tell the new "page" it is visible so it can attach handlers and refresh data.
        try { page.SendAppearing(); } catch { }
    }
}
