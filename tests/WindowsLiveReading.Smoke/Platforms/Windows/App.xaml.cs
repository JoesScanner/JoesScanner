namespace JoesScanner.WinUI;

public partial class App : MauiWinUIApplication
{
    public App() => InitializeComponent();

    protected override MauiApp CreateMauiApp() =>
        MauiApp.CreateBuilder().UseMauiApp<LiveReadingSmokeApplication>().Build();
}
