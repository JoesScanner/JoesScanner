using JoesScanner.Views;

namespace JoesScanner;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        Routing.RegisterRoute("main", typeof(MainPage));
        Routing.RegisterRoute("history", typeof(HistoryPage));
        Routing.RegisterRoute("archive", typeof(ArchivePage));
        Routing.RegisterRoute("stats", typeof(StatsPage));
        Routing.RegisterRoute("communications", typeof(CommunicationsPage));
        Routing.RegisterRoute("log", typeof(LogPage));
        Routing.RegisterRoute("settings", typeof(SettingsPage));
    }
}
