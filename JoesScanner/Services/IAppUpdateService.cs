using System.Threading.Tasks;

namespace JoesScanner.Services;

public interface IAppUpdateService
{
    string SupportUrl { get; }
    string PrivacyPolicyUrl { get; }
    string StoreUrl { get; }
    string StoreDisplayName { get; }
    string PlatformDisplayName { get; }
    string CurrentVersionDisplay { get; }
    Task<AppUpdateCheckResult> CheckForUpdatesAsync();
    Task OpenSupportSiteAsync();
    Task OpenPrivacyPolicyAsync();
    Task OpenStorePageAsync();
}

public sealed class AppUpdateCheckResult
{
    public bool Success { get; init; }
    public bool UpdateTriggered { get; init; }
    public bool StorePageOpened { get; init; }
    public string Message { get; init; } = string.Empty;
}
