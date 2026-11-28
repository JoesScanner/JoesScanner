namespace JoesScanner.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        UnhandledException += (_, args) => Console.Error.WriteLine($"Unhandled WinUI error: {args.Exception}");
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() =>
        MauiApp.CreateBuilder().UseMauiApp<LiveReadingSmokeApplication>()
            .ConfigureMauiHandlers(handlers =>
                handlers.AddHandler<Views.Controls.LiveCollectionView, LiveCollectionViewHandler>())
            .Build();
}
