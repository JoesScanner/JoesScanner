using System;
using JoesScanner.Models;
using Microsoft.Maui.Controls;

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

        // Keep the hooks in place in case you ever re-enable AutoSizeEnabled,
        // but UpdateSizing will apply static sizing when AutoSizeEnabled is false.
        SizeChanged += (_, _) => UpdateSizing();
        Loaded += (_, _) => UpdateSizing();

        UpdateSizing();
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

        const int tabCount = 7;
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

    private async void NavigateTo(string route)
    {
        if (Shell.Current is null)
        {
            return;
        }

        try
        {
            await Shell.Current.GoToAsync(route);
        }
        catch
        {
            // Intentionally ignore navigation errors.
        }
    }

    // TapGestureRecognizer handlers (Border + Image approach)
    private void OnMainTapped(object sender, TappedEventArgs e) => NavigateTo("//main");
    private void OnHistoryTapped(object sender, TappedEventArgs e) => NavigateTo("//history");
    private void OnArchiveTapped(object sender, TappedEventArgs e) => NavigateTo("//archive");
    private void OnStatsTapped(object sender, TappedEventArgs e) => NavigateTo("//stats");
    private void OnCommunicationsTapped(object sender, TappedEventArgs e) => NavigateTo("//communications");
    private void OnLogTapped(object sender, TappedEventArgs e) => NavigateTo("//log");
    private void OnSettingsTapped(object sender, TappedEventArgs e) => NavigateTo("//settings");

    // Legacy Clicked handlers (kept in case anything else still wires to them)
    private void OnMainClicked(object sender, EventArgs e) => NavigateTo("//main");
    private void OnHistoryClicked(object sender, EventArgs e) => NavigateTo("//history");
    private void OnArchiveClicked(object sender, EventArgs e) => NavigateTo("//archive");
    private void OnStatsClicked(object sender, EventArgs e) => NavigateTo("//stats");
    private void OnCommunicationsClicked(object sender, EventArgs e) => NavigateTo("//communications");
    private void OnLogClicked(object sender, EventArgs e) => NavigateTo("//log");
    private void OnSettingsClicked(object sender, EventArgs e) => NavigateTo("//settings");
}
