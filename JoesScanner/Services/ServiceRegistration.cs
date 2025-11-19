using JoesScanner.ViewModels;
using JoesScanner.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace JoesScanner.Services;

public static class ServiceRegistration
{
    public static MauiAppBuilder ConfigureJoesScanner(this MauiAppBuilder builder)
    {
        // Services
        builder.Services.AddSingleton<ICallStreamService, CallStreamService>();
        builder.Services.AddSingleton<IAudioCacheService, AudioCacheService>();
        builder.Services.AddSingleton<IAudioPlaybackService, AudioPlaybackService>();
        builder.Services.AddSingleton<ISettingsService, SettingsService>();

        // ViewModels
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();

        // Views
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<SettingsPage>();

        return builder;
    }
}
