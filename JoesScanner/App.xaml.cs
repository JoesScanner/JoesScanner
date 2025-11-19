using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace JoesScanner;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // Root shell for the app
        MainPage = new AppShell();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);

#if WINDOWS
        // Default starting size (adjust to taste)
        window.Width = 1000;
        window.Height = 700;

        // Minimum size so layout does not collapse
        window.MinimumWidth = 800;
        window.MinimumHeight = 500;
#endif

        return window;
    }
}
