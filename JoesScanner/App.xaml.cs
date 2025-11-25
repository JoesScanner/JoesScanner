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
            // Restore saved size and position (if we have them)
            double width = Preferences.Get("WindowWidth", 0d);
            double height = Preferences.Get("WindowHeight", 0d);
            double x = Preferences.Get("WindowX", double.NaN);
            double y = Preferences.Get("WindowY", double.NaN);

            if (width > 0)
                window.Width = width;

            if (height > 0)
                window.Height = height;

            if (!double.IsNaN(x))
                window.X = x;

            if (!double.IsNaN(y))
                window.Y = y;

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
