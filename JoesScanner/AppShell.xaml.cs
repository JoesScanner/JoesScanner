using JoesScanner.Views;

namespace JoesScanner;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Route for the Settings page
        Routing.RegisterRoute("settings", typeof(SettingsPage));
    }
}
