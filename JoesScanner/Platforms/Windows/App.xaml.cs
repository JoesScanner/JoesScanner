namespace JoesScanner.WinUI;

public partial class App : Microsoft.Maui.MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
