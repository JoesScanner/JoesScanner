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
                // fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Services
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<ICallStreamService, CallStreamService>();
        builder.Services.AddSingleton<IAudioPlaybackService, AudioPlaybackService>();
        builder.Services.AddSingleton<ISubscriptionService, SubscriptionService>();

        builder.Services.AddSingleton<ITelemetryService, TelemetryService>();

        builder.Services.AddSingleton(sp => new HttpClient());
        builder.Services.AddSingleton<IJoesScannerApiClient, JoesScannerApiClient>();

        // View models
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();

        // Pages
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
