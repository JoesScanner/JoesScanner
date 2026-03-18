using Microsoft.Extensions.DependencyInjection;
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

            // Resolve the persisted logging preference before any app initialization work begins.
            AppLog.ReloadEnabledStateFromStorage();

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


        protected override Window CreateWindow(IActivationState? activationState)
        {
            Page rootPage;
#if IOS
            // iOS launch is more stable when we avoid the extra Shell/DataTemplate hop and
            // host the RootPage directly. RootPage already owns the tab strip and content area,
            // so Shell does not add value on iOS here.
            rootPage = _services.GetRequiredService<Views.RootPage>();
#else
            rootPage = new AppShell(_services);
#endif

            var window = new Window(rootPage);

#if WINDOWS
            const double defaultWidth = 430;
            const double defaultHeight = 1000;

            const double minWidth = 430;
            const double minHeight = 500;

            var width = AppStateStore.GetDouble("window_width", defaultWidth);
            var height = AppStateStore.GetDouble("window_height", defaultHeight);
            var x = AppStateStore.GetDouble("window_x", double.NaN);
            var y = AppStateStore.GetDouble("window_y", double.NaN);

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
                AppStateStore.SetDouble("window_width", window.Width);
                AppStateStore.SetDouble("window_height", window.Height);
                AppStateStore.SetDouble("window_x", window.X);
                AppStateStore.SetDouble("window_y", window.Y);
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
