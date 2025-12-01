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

        // Note the ? on IActivationState to match the base signature and remove the nullability warning
        protected override Window CreateWindow(IActivationState? activationState)
        {
            // Standard MAUI window hosting your AppShell
            var window = new Window(new AppShell());

#if WINDOWS
            // Default size if we have no stored values yet
            const double defaultWidth = 1100;
            const double defaultHeight = 720;

            // Minimum size so users can resize larger but not shrink to unusable
            const double minWidth = 900;
            const double minHeight = 600;

            // Restore last size and position if available
            var width = Preferences.Get("WindowWidth", defaultWidth);
            var height = Preferences.Get("WindowHeight", defaultHeight);
            var x = Preferences.Get("WindowX", double.NaN);
            var y = Preferences.Get("WindowY", double.NaN);

            // Apply size
            window.Width = width;
            window.Height = height;

            // Apply minimum size to keep layout usable, but do not restrict maximum size
            window.MinimumWidth = minWidth;
            window.MinimumHeight = minHeight;

            // Only apply position if we have valid stored coordinates
            if (!double.IsNaN(x) && !double.IsNaN(y))
            {
                window.X = x;
                window.Y = y;
            }

            // Save whenever size or position changes
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
