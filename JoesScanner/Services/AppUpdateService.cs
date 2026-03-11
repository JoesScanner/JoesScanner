using Microsoft.Maui.ApplicationModel;
using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
#if IOS || MACCATALYST
using Foundation;
using UIKit;
#endif
#if WINDOWS
using Windows.Services.Store;
#endif

namespace JoesScanner.Services;

public sealed class AppUpdateService : IAppUpdateService
{
    private const string DefaultSupportUrl = "https://joesscanner.com/support/";
    private const string WindowsStoreUrlConst = "https://apps.microsoft.com/detail/9n5hbztcnt4t?gl=US&hl=en-US";
    private const string AndroidStoreUrlConst = "https://play.google.com/store/apps/details?id=app.joesscanner.com";
    private const string AppleStoreUrlConst = "https://apps.apple.com/app/id6758413482";
    private const string AppleStoreLookupUrlConst = "https://itunes.apple.com/lookup?id=6758413482&country=us&entity=software";
    private const string AppleStoreDeepLinkUrlConst = "itms-apps://itunes.apple.com/app/id6758413482";

    private static readonly HttpClient _httpClient = new();

    public string SupportUrl => DefaultSupportUrl;
    public string StoreUrl => GetStoreUrl();
    public string StoreDisplayName => GetStoreDisplayName();
    public string PlatformDisplayName => GetPlatformDisplayName();
    public string CurrentVersionDisplay => GetNormalizedVersion();

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync()
    {
#if WINDOWS
        try
        {
            var context = StoreContext.GetDefault();
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates == null || updates.Count == 0)
            {
                return new AppUpdateCheckResult
                {
                    Success = true,
                    Message = "You're up to date."
                };
            }

            var result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            var stateText = result.OverallState.ToString();

            return new AppUpdateCheckResult
            {
                Success = true,
                UpdateTriggered = true,
                Message = stateText == "Completed"
                    ? "Update installed. Restart the app if Windows prompts you to finish the update."
                    : $"Microsoft Store update state: {stateText}."
            };
        }
        catch
        {
            try
            {
                await OpenStorePageAsync();
                return new AppUpdateCheckResult
                {
                    Success = true,
                    StorePageOpened = true,
                    Message = "Opened the Microsoft Store page for Joe's Scanner."
                };
            }
            catch (Exception ex)
            {
                return new AppUpdateCheckResult
                {
                    Success = false,
                    Message = $"Could not check for updates: {ex.Message}"
                };
            }
        }
#else
        var platform = DeviceInfo.Current.Platform;

        if (platform == DevicePlatform.iOS || platform == DevicePlatform.MacCatalyst)
        {
            return await CheckAppleStoreForUpdatesAsync();
        }

        if (platform == DevicePlatform.Android)
        {
            return await CheckGooglePlayForUpdatesAsync();
        }

        try
        {
            await OpenStorePageAsync();
            return new AppUpdateCheckResult
            {
                Success = true,
                StorePageOpened = true,
                Message = $"Opened the {StoreDisplayName} listing for Joe's Scanner."
            };
        }
        catch (Exception ex)
        {
            return new AppUpdateCheckResult
            {
                Success = false,
                Message = $"Could not open the store listing: {ex.Message}"
            };
        }
#endif
    }

    public async Task OpenSupportSiteAsync()
    {
        await Launcher.Default.OpenAsync(new Uri(SupportUrl));
    }

    public async Task OpenStorePageAsync()
    {
        var platform = DeviceInfo.Current.Platform;

#if IOS
        if (platform == DevicePlatform.iOS)
        {
            await OpenAppleStorePageOnIosAsync();
            return;
        }
#endif

        if (platform == DevicePlatform.MacCatalyst)
        {
            Exception? lastException = null;

            var nativeCandidates = new[]
            {
                AppleStoreDeepLinkUrlConst,
                AppleStoreUrlConst
            };

            foreach (var candidate in nativeCandidates)
            {
                try
                {
                    var opened = await MainThread.InvokeOnMainThreadAsync(() => TryOpenAppleStoreUrlNative(candidate));
                    if (opened)
                        return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            try
            {
                await Launcher.Default.OpenAsync(new Uri(AppleStoreUrlConst));
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            throw lastException ?? new InvalidOperationException("Could not open the App Store page.");
        }

        var primaryUri = new Uri(StoreUrl);
        await Launcher.Default.OpenAsync(primaryUri);
    }

    private static bool TryOpenAppleStoreUrlNative(string url)
    {
#if IOS || MACCATALYST
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var nsUrl = NSUrl.FromString(url);
        if (nsUrl == null)
            return false;

        var application = UIApplication.SharedApplication;
        if (application == null)
            return false;

        try
        {
            return application.OpenUrl(nsUrl);
        }
        catch
        {
            return false;
        }
#else
        _ = url;
        return false;
#endif
    }

#if IOS
    private static async Task OpenAppleStorePageOnIosAsync()
    {
        Exception? lastException = null;

        try
        {
            var opened = await MainThread.InvokeOnMainThreadAsync(() => TryOpenAppleStoreUrlNative(AppleStoreDeepLinkUrlConst));
            if (opened)
                return;
        }
        catch (Exception ex)
        {
            lastException = ex;
        }

        try
        {
            var opened = await MainThread.InvokeOnMainThreadAsync(() => TryOpenAppleStoreUrlNative(AppleStoreUrlConst));
            if (opened)
                return;
        }
        catch (Exception ex)
        {
            lastException = ex;
        }

        throw lastException ?? new InvalidOperationException("Could not open the App Store page on this iPhone.");
    }
#endif

    private static async Task<AppUpdateCheckResult> CheckAppleStoreForUpdatesAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync(AppleStoreLookupUrlConst);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            {
                return new AppUpdateCheckResult
                {
                    Success = false,
                    Message = "Could not find the App Store listing for Joe's Scanner."
                };
            }

            var app = results[0];
            var latestVersion = app.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return new AppUpdateCheckResult
                {
                    Success = false,
                    Message = "The App Store did not return a version number for Joe's Scanner."
                };
            }

            var installedVersion = GetNormalizedVersion();
            var comparison = CompareVersionStrings(latestVersion, installedVersion);

            if (comparison > 0)
            {
                return new AppUpdateCheckResult
                {
                    Success = true,
                    Message = $"Update available. Installed: {installedVersion}. App Store: {NormalizeVersionString(latestVersion)}. Use Store page to open the App Store.",
                };
            }

            return new AppUpdateCheckResult
            {
                Success = true,
                Message = $"You're up to date. Installed: {installedVersion}. App Store: {NormalizeVersionString(latestVersion)}."
            };
        }
        catch (Exception ex)
        {
            return new AppUpdateCheckResult
            {
                Success = false,
                Message = $"Could not check the App Store in the background: {ex.Message}"
            };
        }
    }

    private static async Task<AppUpdateCheckResult> CheckGooglePlayForUpdatesAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{AndroidStoreUrlConst}&hl=en_US&gl=US");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Mobile Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();
            var latestVersion = ExtractGooglePlayVersion(html);

            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return new AppUpdateCheckResult
                {
                    Success = false,
                    Message = "Could not read the Google Play version in the background. Use Store page to open Google Play."
                };
            }

            var installedVersion = GetNormalizedVersion();
            var normalizedLatestVersion = NormalizeVersionString(latestVersion);
            var comparison = CompareVersionStrings(normalizedLatestVersion, installedVersion);

            if (comparison > 0)
            {
                return new AppUpdateCheckResult
                {
                    Success = true,
                    Message = $"Update available. Installed: {installedVersion}. Google Play: {normalizedLatestVersion}. Use Store page to open Google Play."
                };
            }

            return new AppUpdateCheckResult
            {
                Success = true,
                Message = $"You're up to date. Installed: {installedVersion}. Google Play: {normalizedLatestVersion}."
            };
        }
        catch (Exception ex)
        {
            return new AppUpdateCheckResult
            {
                Success = false,
                Message = $"Could not check Google Play in the background: {ex.Message}"
            };
        }
    }

    private static string? ExtractGooglePlayVersion(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var patterns = new[]
        {
            @"""softwareVersion""\s*:\s*""(?<version>[^""]+)""",
            @"""versionName""\s*:\s*""(?<version>[^""]+)""",
            @"Current Version.*?<span[^>]*>(?<version>[^<]+)</span>",
            @"(?<version>\d+(?:\.\d+){1,3})"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var version = match.Groups["version"].Value?.Trim();
            if (string.IsNullOrWhiteSpace(version))
                continue;

            if (version.Any(char.IsDigit))
                return version;
        }

        return null;
    }



    private static int CompareVersionStrings(string left, string right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        var max = Math.Max(leftParts.Length, rightParts.Length);

        for (var i = 0; i < max; i++)
        {
            var leftPart = i < leftParts.Length ? leftParts[i] : 0;
            var rightPart = i < rightParts.Length ? rightParts[i] : 0;

            if (leftPart > rightPart)
                return 1;

            if (leftPart < rightPart)
                return -1;
        }

        return 0;
    }

    private static int[] ParseVersionParts(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Array.Empty<int>();

        return version
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part =>
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : 0;
            })
            .ToArray();
    }

    private static string NormalizeVersionString(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "0.0.0";

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3
            ? string.Join('.', parts.Take(3))
            : version;
    }

    private static string GetPlatformDisplayName()
    {
        var platform = DeviceInfo.Current.Platform;

        if (platform == DevicePlatform.Android)
            return "Android";

        if (platform == DevicePlatform.iOS)
            return "iPhone / iPad";

        if (platform == DevicePlatform.MacCatalyst)
            return "Mac";

        if (platform == DevicePlatform.WinUI)
            return "Windows";

        return platform.ToString();
    }

    private static string GetStoreDisplayName()
    {
        var platform = DeviceInfo.Current.Platform;

        if (platform == DevicePlatform.Android)
            return "Google Play";

        if (platform == DevicePlatform.iOS || platform == DevicePlatform.MacCatalyst)
            return "App Store";

        if (platform == DevicePlatform.WinUI)
            return "Microsoft Store";

        return "store";
    }

    private static string GetStoreUrl()
    {
        var platform = DeviceInfo.Current.Platform;

        if (platform == DevicePlatform.Android)
            return AndroidStoreUrlConst;

        if (platform == DevicePlatform.iOS || platform == DevicePlatform.MacCatalyst)
            return AppleStoreUrlConst;

        if (platform == DevicePlatform.WinUI)
            return WindowsStoreUrlConst;

        return DefaultSupportUrl;
    }

    private static string GetNormalizedVersion()
    {
        string raw;

        try
        {
            raw = AppInfo.Current.VersionString ?? string.Empty;
        }
        catch
        {
            raw = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                raw = typeof(App).Assembly.GetName().Version?.ToString() ?? string.Empty;
            }
            catch
            {
                raw = string.Empty;
            }
        }

        return NormalizeVersionString(raw);
    }
}
