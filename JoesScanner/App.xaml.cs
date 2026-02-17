using JoesScanner.Services;
using JoesScanner.ViewModels;

namespace JoesScanner
{
    public partial class App : Application
    {
        private readonly IServiceProvider _services;
        private readonly ISettingsService _settings;
        private readonly ITelemetryService _telemetryService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly ICommsBadgeService _commsBadgeService;

        public App(IServiceProvider services, ISettingsService settings, ITelemetryService telemetryService, ISubscriptionService subscriptionService, ICommsBadgeService commsBadgeService)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
            _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
            _commsBadgeService = commsBadgeService ?? throw new ArgumentNullException(nameof(commsBadgeService));

            InitializeComponent();

            // Always create a new session token for each process start.
            // This guarantees the first ping after startup uses the new token.
            _settings.AuthSessionToken = Guid.NewGuid().ToString();

            // Touch DeviceInstallId so it is created early if settings lazily generates it.
            _ = _settings.DeviceInstallId;

            _telemetryService.TrackAppStarted();

            // Verify auth + subscription once per app start.
            // This keeps the local subscription cache current and ensures the server can associate
            // the current session token to an account whenever credentials are configured.
            BeginStartupAuthVerification();

            // Start the communications badge poller so the tab can show unread state.
            // The poller is best effort and will no-op until credentials are configured and the session is associated.
            try { _commsBadgeService.Start(); } catch { }

            // Preload communications once per app start so the messages page is ready immediately.
            BeginStartupCommsPreload();

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

        private void BeginStartupAuthVerification()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var serverUrl = (_settings.ServerUrl ?? string.Empty).Trim();
                    if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
                        return;

                    // Only the hosted Joe's Scanner backend requires subscription checks.
                    if (!string.Equals(serverUri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase))
                        return;

                    var user = (_settings.BasicAuthUsername ?? string.Empty).Trim();
                    var pass = (_settings.BasicAuthPassword ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                        return;

                    await _subscriptionService.EnsureSubscriptionAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort. Offline behavior is handled inside SubscriptionService.
                }
            });
        }

        private void BeginStartupCommsPreload()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var vm = _services.GetService(typeof(CommunicationsViewModel)) as CommunicationsViewModel;
                    if (vm == null)
                        return;

                    await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);

                    await vm.PreloadOnAppStartAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell(_services));

#if WINDOWS
            const double defaultWidth = 430;
            const double defaultHeight = 1000;

            const double minWidth = 430;
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

            // Autoplay on app launch (all platforms).
            // If enabled, start monitoring and ensure the playback pipeline is running.
            // For Joe's hosted server, require user/pass (website API credentials) before starting.
            try
            {
                if (_settings.AutoPlay)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        try
                        {
                            await Task.Delay(400);

                            var serverUrl = (_settings.ServerUrl ?? string.Empty).Trim();
                            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
                                return;

                            if (string.Equals(serverUri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase))
                            {
                                var user = (_settings.BasicAuthUsername ?? string.Empty).Trim();
                                var pass = (_settings.BasicAuthPassword ?? string.Empty).Trim();
                                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                                    return;
                            }

                            var vm = _services.GetService(typeof(MainViewModel)) as MainViewModel;
                            if (vm == null)
                                return;

                            await vm.StartMonitoringWithAutoplayAsync();
                        }
                        catch
                        {
                        }
                    });
                }
            }
            catch
            {
            }


            return window;
        }
    }
}
