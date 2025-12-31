using System;
using JoesScanner.Services;
using Microsoft.Maui.Storage;

namespace JoesScanner
{
    public partial class App : Application
    {
        private readonly ISettingsService _settings;
        private readonly ITelemetryService _telemetryService;

        public App(ISettingsService settings, ITelemetryService telemetryService)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));

            InitializeComponent();

            // Always create a new session token for each process start.
            // This guarantees the first ping after startup uses the new token.
            _settings.AuthSessionToken = Guid.NewGuid().ToString();

            // Touch DeviceInstallId so it is created early if settings lazily generates it.
            _ = _settings.DeviceInstallId;

            _telemetryService.TrackAppStarted();

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    _telemetryService.TrackAppStopping();
                }
                catch
                {
                }
            };
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell());

#if WINDOWS
            const double defaultWidth = 500;
            const double defaultHeight = 1000;

            const double minWidth = 500;
            const double minHeight = 500;

            var width = Preferences.Get("WindowWidth", defaultWidth);
            var height = Preferences.Get("WindowHeight", defaultHeight);
            var x = Preferences.Get("WindowX", double.NaN);
            var y = Preferences.Get("WindowY", double.NaN);

            window.Width = width;
            window.Height = height;

            window.MinimumWidth = minWidth;
            window.MinimumHeight = minHeight;

            if (!double.IsNaN(x) && !double.IsNaN(y))
            {
                window.X = x;
                window.Y = y;
            }

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
