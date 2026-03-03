using JoesScanner.Services;
using JoesScanner.ViewModels;
using JoesScanner.Views;
using SQLitePCL;

namespace JoesScanner;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        // SQLitePCL must be initialized once before any Microsoft.Data.Sqlite usage.
        // bundle_green provides cross-platform native SQLite in MAUI.
        Batteries_V2.Init();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                // Add custom fonts here if needed.
            });

        // Services
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<IDatabasePathProvider, DatabasePathProvider>();
        builder.Services.AddSingleton<ILocalCallsRepository, LocalCallsRepository>();
        builder.Services.AddSingleton<IHistoryLookupsRepository, HistoryLookupsRepository>();
        builder.Services.AddSingleton<ICallStreamService, CallStreamService>();
        builder.Services.AddSingleton<ICallHistoryService, CallHistoryService>();
        builder.Services.AddSingleton<IHistoryLookupsCacheService, HistoryLookupsCacheService>();
        builder.Services.AddSingleton<IAudioFilterService, AudioFilterService>();
        builder.Services.AddSingleton<IAudioPlaybackService, AudioPlaybackService>();
        builder.Services.AddSingleton<ISystemMediaService, SystemMediaService>();
        builder.Services.AddSingleton<ISubscriptionService, SubscriptionService>();
        builder.Services.AddSingleton<ITelemetryService, TelemetryService>();
        builder.Services.AddSingleton<IAuthLookupsSyncService, AuthLookupsSyncService>();
        builder.Services.AddSingleton<IAddressDetectionService, AddressDetectionService>();
        builder.Services.AddSingleton<IWhat3WordsService, What3WordsService>();

        // Call downloading (single and range zip).
        builder.Services.AddSingleton<ICallDownloadService, CallDownloadService>();

        // Tone detection highlight state (talkgroup hot window tracking).
        builder.Services.AddSingleton<IToneAlertService, ToneAlertService>();

        // Centralized playback policy evaluation.
        builder.Services.AddSingleton<IPlaybackCoordinator, PlaybackCoordinator>();

        // Use separate HttpClient instances for services that need them.
        // Keeping HttpClient transient here avoids shared state issues (BaseAddress, headers, timeout).
        builder.Services.AddTransient(sp => new HttpClient());

        builder.Services.AddSingleton<ICommunicationsService, CommunicationsService>();
        builder.Services.AddSingleton<ICommsBadgeService, CommsBadgeService>();
        builder.Services.AddSingleton<IJoesScannerApiClient, JoesScannerApiClient>();

        // Local filter profile storage (device only).
        builder.Services.AddSingleton<IFilterProfileStore, LocalFilterProfileStore>();

        // View models
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<CommunicationsViewModel>();

        // Pages
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<HistoryPage>();
        builder.Services.AddTransient<StatsPage>();
        builder.Services.AddTransient<CommunicationsPage>();
        builder.Services.AddTransient<LogPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<RootPage>();

        return builder.Build();
    }
}