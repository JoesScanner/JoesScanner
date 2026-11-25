using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using JoesScanner.Helpers;
using JoesScanner.Services;
using JoesScanner.ViewModels;

namespace JoesScanner;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private FileStream? _storageLease;
    private Task<ISettingsService>? _databaseStartup;
    private bool _servicesStarted;

    internal string? RecoveryNotice { get; private set; }

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        _ = AppLog.InitializeAsync();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var message = new Label { Text = "Opening saved data…", TextColor = Colors.White };
        var progress = new ActivityIndicator { IsRunning = true, Color = Colors.White };
        var retry = new Button { Text = "Retry", IsVisible = false };
        var window = new Window(new ContentPage
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24),
                Spacing = 12,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                Children = { progress, message, retry }
            }
        });
#if WINDOWS
        window.Width = 430;
        window.Height = 1000;
        window.MinimumWidth = 430;
        window.MinimumHeight = 500;
#endif
        window.Created += (_, _) => window.Dispatcher.Dispatch(() => _ = OpenAsync());
        retry.Clicked += (_, _) => _ = OpenAsync();
        return window;

        async Task OpenAsync()
        {
            retry.IsVisible = false;
            progress.IsRunning = progress.IsVisible = true;
            message.Text = "Opening saved data…";
            ISettingsService settings;
            try
            {
                settings = await (_databaseStartup ??= Task.Run(PrepareDatabasesAsync));
            }
            catch (Exception error)
            {
                _databaseStartup = null;
                AppLog.Basic(() => $"Opening saved data failed ({DatabaseRecovery.Describe(error)}).");
                progress.IsRunning = progress.IsVisible = false;
                message.Text = error switch
                {
                    NewerDatabaseException => "Your saved data requires a newer version of Joe’s Scanner.",
                    SqliteException { SqliteErrorCode: 5 or 6 } => "Saved data is busy. Close any other copy of Joe’s Scanner and retry.",
                    SqliteException { SqliteErrorCode: 13 } => "Saved data needs more free space. Free up space and retry.",
                    UnauthorizedAccessException or SqliteException { SqliteErrorCode: 3 or 8 or 23 } =>
                        "Joe’s Scanner could not write to its data folder. Check folder permissions and retry.",
                    _ => "Saved data is temporarily unavailable. Check storage and close any other copy of Joe’s Scanner, then retry."
                };
                retry.IsVisible = true;
                return;
            }

#if WINDOWS
            RestoreWindowPlacement(window);
#endif
            var startMonitoring = !_servicesStarted;
            if (startMonitoring)
            {
                _servicesStarted = true;
                StartBackgroundServices(settings);
            }
#if IOS
            window.Page = _services.GetRequiredService<Views.RootPage>();
#else
            window.Page = new AppShell(_services);
#endif
            if (startMonitoring)
            {
                try
                {
                    await _services.GetRequiredService<MainViewModel>()
                        .StartMonitoringWithAutoplayAsync(() => Task.Delay(400));
                }
                catch (Exception error)
                {
                    AppLog.Basic(() => $"Could not start Live monitoring on launch ({DiagnosticRedaction.Describe(error)}).");
                }
            }
        }
    }

    private async Task<ISettingsService> PrepareDatabasesAsync()
    {
        await AppLog.InitializeAsync().ConfigureAwait(false);
        var path = _services.GetRequiredService<IDatabasePathProvider>().DbPath;
        var recoveredMain = false;
        var recoveredFavorites = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _storageLease ??= DatabaseRecovery.AcquireLease(path);
                recoveredMain |= DatabaseRecovery.PrepareMain(path);
                recoveredFavorites |= FavoritesRepository.PrepareStorage(path);
                var settings = _services.GetRequiredService<ISettingsService>();
                _ = settings.DeviceInstallId; // Initialize the cache and protected migration off the UI thread.
                settings.AuthSessionToken = Guid.NewGuid().ToString();
                var invalidSettings = settings is SettingsService { HadUnreadableSettings: true };
                var credentialWarning = settings.SavedCredentialWarning;
                RecoveryNotice = DatabaseRecovery.StartupNotice(recoveredMain, recoveredFavorites, invalidSettings, credentialWarning);
                _ = _services.GetRequiredService<FavoritesService>();
                if (invalidSettings)
                    AppLog.Basic("Ignored unreadable saved settings while retaining other local data.");
#if WINDOWS
                ReadWindowPlacement();
#endif
                return settings;
            }
            catch (SqliteException error) when (attempt < 2 && error.SqliteErrorCode is 5 or 6)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
            }
        }
    }

    private void StartBackgroundServices(ISettingsService settings)
    {
        var telemetry = _services.GetRequiredService<ITelemetryService>();
        telemetry.TrackAppStarted();
        settings.LocalOnlyModeChanged += (_, _) =>
        {
            if (!settings.LocalOnlyMode)
            {
                telemetry.TrackAppStarted();
                BeginStartupAuthVerification(settings);
            }
        };

        var favorites = _services.GetRequiredService<FavoritesService>();
        favorites.Start();
        favorites.Wake();
        Connectivity.Current.ConnectivityChanged += (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
                favorites.Wake(force: true);
        };

        BeginStartupAuthVerification(settings);
        try { _services.GetRequiredService<ICommsBadgeService>().Start(); } catch { }
        _services.GetRequiredService<IDbMaintenanceService>().Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { telemetry.TrackAppStopping(); } catch { }
        };
    }

    private void BeginStartupAuthVerification(ISettingsService settings)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (settings.LocalOnlyMode ||
                    !Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var serverUri) ||
                    !HostedServerRules.RequiresApiFirewallCredentials(settings, serverUri))
                    return;

                await _services.GetRequiredService<ISubscriptionService>()
                    .EnsureSubscriptionAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                AppLog.Basic(() => $"Startup account verification deferred ({DiagnosticRedaction.Describe(error)}).");
            }
        });
    }

#if WINDOWS
    private (double Width, double Height, double X, double Y) _placement;

    private void ReadWindowPlacement() => _placement = (
        AppStateStore.GetDouble("window_width", 430),
        AppStateStore.GetDouble("window_height", 1000),
        AppStateStore.GetDouble("window_x", double.NaN),
        AppStateStore.GetDouble("window_y", double.NaN));

    private void RestoreWindowPlacement(Window window)
    {
        window.Width = _placement.Width;
        window.Height = _placement.Height;
        if (double.IsFinite(_placement.X) && double.IsFinite(_placement.Y))
        {
            window.X = _placement.X;
            window.Y = _placement.Y;
        }

        window.SizeChanged += (_, _) =>
        {
            try
            {
                AppStateStore.SetDouble("window_width", window.Width);
                AppStateStore.SetDouble("window_height", window.Height);
                AppStateStore.SetDouble("window_x", window.X);
                AppStateStore.SetDouble("window_y", window.Y);
            }
            catch (Exception error)
            {
                AppLog.Basic(() => $"Saving window placement deferred ({DatabaseRecovery.Describe(error)}).");
            }
        };
    }
#endif
}
