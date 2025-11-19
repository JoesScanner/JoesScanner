using JoesScanner.Views;

namespace JoesScanner;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Route used by the Settings button
        Routing.RegisterRoute("settings", typeof(SettingsPage));
    }
}

