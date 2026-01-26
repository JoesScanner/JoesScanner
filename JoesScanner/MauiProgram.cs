using JoesScanner.Services;
using JoesScanner.ViewModels;
using JoesScanner.Views;

namespace JoesScanner;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                // Add custom fonts here if needed.
            });

        // Services
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<ICallStreamService, CallStreamService>();
        builder.Services.AddSingleton<ICallHistoryService, CallHistoryService>();
        builder.Services.AddSingleton<IAudioPlaybackService, AudioPlaybackService>();
        builder.Services.AddSingleton<ISystemMediaService, SystemMediaService>();
        builder.Services.AddSingleton<ISubscriptionService, SubscriptionService>();
        builder.Services.AddSingleton<ITelemetryService, TelemetryService>();

        // Use separate HttpClient instances for services that need them.
        // Keeping HttpClient transient here avoids shared state issues (BaseAddress, headers, timeout).
        builder.Services.AddTransient(sp => new HttpClient());

        builder.Services.AddSingleton<ICommunicationsService, CommunicationsService>();
        builder.Services.AddSingleton<ICommsBadgeService, CommsBadgeService>();
        builder.Services.AddSingleton<IJoesScannerApiClient, JoesScannerApiClient>();

        // Local filter profile storage (device only).
        builder.Services.AddSingleton<IFilterProfileStore, LocalFilterProfileStore>();

        // Local Settings filter profile storage (device only).
        builder.Services.AddSingleton<ISettingsFilterProfileStore, LocalSettingsFilterProfileStore>();

        // View models
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingleton<ArchiveViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();

        builder.Services.AddSingleton<CommunicationsViewModel>();

        // Pages
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<HistoryPage>();
        builder.Services.AddTransient<ArchivePage>();
        builder.Services.AddTransient<StatsPage>();
        builder.Services.AddTransient<CommunicationsPage>();
        builder.Services.AddTransient<LogPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
