using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace JoesScanner
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell());

#if WINDOWS
            const double defaultWidth = 1100;
            const double defaultHeight = 720;

            const double minWidth = 900;
            const double minHeight = 600;

            // Restore last size and position if available
            var width = Preferences.Get("WindowWidth", defaultWidth);
            var height = Preferences.Get("WindowHeight", defaultHeight);
            var x = Preferences.Get("WindowX", double.NaN);
            var y = Preferences.Get("WindowY", double.NaN);

            window.Width = width;
            window.Height = height;

            window.MinimumWidth = minWidth;
            window.MinimumHeight = minHeight;

            // Only apply position if we have valid stored coordinates
            if (!double.IsNaN(x) && !double.IsNaN(y))
            {
                window.X = x;
                window.Y = y;
            }

            // Persist size and position on change
            window.SizeChanged += (_, _) =>
            {
                Preferences.Set("WindowWidth", window.Width);
                Preferences.Set("WindowHeight", window.Height);
                Preferences.Set("WindowX", window.X);
                Preferences.Set("WindowY", window.Y);
            };
#endif

            return window;
        }
    }
}
