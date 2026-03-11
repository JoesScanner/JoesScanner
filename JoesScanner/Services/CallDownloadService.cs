using JoesScanner.Models;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace JoesScanner.Services
{
    public sealed class CallDownloadService : ICallDownloadService
    {
        private const string ServiceAuthUsername = "secappass";
        private const string ServiceAuthPassword = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";

        private readonly ISettingsService _settingsService;
        private readonly HttpClient _httpClient;

        public CallDownloadService(ISettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            var handler = new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(25),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
        }

        public async Task DownloadSingleAsync(CallItem call, CancellationToken cancellationToken = default)
        {
            if (call == null)
                return;

            var uri = TryBuildAudioUri(call);
            if (uri == null)
            {
                await ShowErrorAsync("Download failed", "No audio URL available for this call.");
                return;
            }

            var fileName = BuildSingleFileName(call, uri);
            var tempPath = Path.Combine(FileSystem.CacheDirectory, fileName);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                ApplyAuthorizationHeader(request, uri);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Add(() => $"Download: single failed. HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {uri}");
                    await ShowErrorAsync("Download failed", $"Server returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
                    return;
                }

                await using var inStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var outStream = File.Create(tempPath);
                await inStream.CopyToAsync(outStream, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: single failed. ex={ex.GetType().Name}: {ex.Message}");
                await ShowErrorAsync("Download failed", $"Could not download the audio file: {ex.Message}");
                return;
            }

            try
            {
                await SaveOrShareFileAsync(tempPath, fileName);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: share failed. ex={ex.GetType().Name}: {ex.Message}");
                await ShowErrorAsync("Save failed", $"The file was downloaded but could not be shared: {ex.Message}");
            }
        }

        public async Task DownloadRangeZipAsync(IReadOnlyList<CallItem> calls, CancellationToken cancellationToken = default)
        {
            if (calls == null || calls.Count == 0)
                return;

            var zipName = BuildZipFileName(calls);
            var zipPath = Path.Combine(FileSystem.CacheDirectory, zipName);

            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                await using var zipFileStream = File.Create(zipPath);
                using var zip = new ZipArchive(zipFileStream, ZipArchiveMode.Create, leaveOpen: false);

                var downloadedCount = 0;
                var failedCount = 0;

                for (var i = 0; i < calls.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var call = calls[i];
                    var uri = TryBuildAudioUri(call);
                    if (uri == null)
                    {
                        failedCount++;
                        continue;
                    }

                    var entryName = BuildZipEntryName(i + 1, call, uri);
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                        ApplyAuthorizationHeader(request, uri);

                        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                        if (!response.IsSuccessStatusCode)
                        {
                            AppLog.Add(() => $"Download: range item {i + 1} failed. HTTP {(int)response.StatusCode} for {uri}");
                            failedCount++;
                            continue;
                        }

                        await using var inStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                        await using var entryStream = entry.Open();
                        await inStream.CopyToAsync(entryStream, cancellationToken);
                        downloadedCount++;
                    }
                    catch (TaskCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Add(() => $"Download: range item {i + 1} failed. ex={ex.GetType().Name}: {ex.Message}");
                        failedCount++;
                    }
                }

                if (downloadedCount == 0)
                {
                    await ShowErrorAsync("Download failed", "Could not download any of the selected calls. Check your connection and server settings.");
                    return;
                }
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: range zip failed. ex={ex.GetType().Name}: {ex.Message}");
                await ShowErrorAsync("Download failed", $"Could not create the zip file: {ex.Message}");
                return;
            }

            try
            {
                await SaveOrShareFileAsync(zipPath, zipName);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: share zip failed. ex={ex.GetType().Name}: {ex.Message}");
                await ShowErrorAsync("Save failed", $"The zip was created but could not be shared: {ex.Message}");
            }
        }

        private Uri? TryBuildAudioUri(CallItem call)
        {
            var raw = (call.AudioUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // Normalize double-encoded percent signs (%25 -> %) to match playback behavior.
            // Some servers (including hosted history items) may send media URLs where
            // percent signs have been percent-encoded (e.g. %2520 instead of %20).
            raw = Regex.Replace(raw, "(?i)%25", "%");

            if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
            {
                // Strip any embedded credentials from the URL.
                var builder = new UriBuilder(absolute)
                {
                    UserName = string.Empty,
                    Password = string.Empty
                };
                return builder.Uri;
            }

            var baseUrl = NormalizeBaseUrl(_settingsService.ServerUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            var combined = baseUrl.TrimEnd('/') + "/" + raw.TrimStart('/');
            return Uri.TryCreate(combined, UriKind.Absolute, out var result) ? result : null;
        }

        /// <summary>
        /// Apply Basic Auth per-request based on the actual audio URI host,
        /// matching the same logic used by audio playback.
        /// </summary>
        private void ApplyAuthorizationHeader(HttpRequestMessage request, Uri audioUri)
        {
            try
            {
                // Hosted Joe's Scanner server: use service credentials if subscription is valid.
                if (string.Equals(audioUri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = _settingsService.SubscriptionLastStatusOk;
                    var expires = _settingsService.SubscriptionExpiresUtc;
                    var tier = _settingsService.SubscriptionTierLevel;

                    if (ok && tier >= 1 && expires.HasValue && expires.Value.ToUniversalTime() > DateTime.UtcNow)
                    {
                        var rawCreds = $"{ServiceAuthUsername}:{ServiceAuthPassword}";
                        var bytes = Encoding.ASCII.GetBytes(rawCreds);
                        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
                    }

                    return;
                }

                // Custom servers: use configured Basic Auth credentials if available.
                var username = (_settingsService.BasicAuthUsername ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(username))
                    return;

                var password = _settingsService.BasicAuthPassword ?? string.Empty;

                var rawAuth = $"{username}:{password}";
                var authBytes = Encoding.UTF8.GetBytes(rawAuth);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            }
            catch
            {
                // Auth failure should not prevent the request from being attempted.
            }
        }

        private static Task ShowErrorAsync(string title, string message)
        {
            return UiDialogs.AlertAsync(title, message, "OK");
        }

        /// <summary>
        /// On Windows, opens a FileSavePicker so the user can choose where to save.
        /// On mobile, uses the Share sheet.
        /// </summary>
        private static async Task SaveOrShareFileAsync(string tempPath, string fileName)
        {
#if WINDOWS
            await WindowsFileSaveAsync(tempPath, fileName);
#else
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Save",
                File = new ShareFile(tempPath)
            });
#endif
        }

#if WINDOWS
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private static Task WindowsFileSaveAsync(string sourcePath, string suggestedFileName)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var ext = Path.GetExtension(suggestedFileName);
                if (string.IsNullOrWhiteSpace(ext))
                    ext = ".mp3";

                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
                    SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName)
                };

                var label = ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ? "Zip archive" : "Audio file";
                picker.FileTypeChoices.Add(label, new List<string> { ext });

                var hwnd = GetActiveWindow();
                if (hwnd == IntPtr.Zero)
                    hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    hwnd = GetWindowHandle();

                if (hwnd == IntPtr.Zero)
                {
                    // Fallback: copy to Documents
                    var docsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var fallbackPath = Path.Combine(docsFolder, suggestedFileName);
                    File.Copy(sourcePath, fallbackPath, overwrite: true);
                    await UiDialogs.AlertAsync("Download complete", $"Saved to:\n{fallbackPath}", "OK");
                    return;
                }

                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await picker.PickSaveFileAsync();
                if (file == null)
                    return;

                // Copy the temp file into the user's chosen location
                using (var sourceStream = File.OpenRead(sourcePath))
                using (var destStream = await file.OpenStreamForWriteAsync())
                {
                    destStream.SetLength(0);
                    await sourceStream.CopyToAsync(destStream);
                }

                await UiDialogs.AlertAsync("Download complete", $"Saved to:\n{file.Path}", "OK");
            });
        }

        private static IntPtr GetWindowHandle()
        {
            try
            {
                var window = Application.Current?.Windows?.Count > 0
                    ? Application.Current.Windows[0]
                    : null;

                if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window platformWindow)
                {
                    return WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
                }
            }
            catch
            {
            }

            return IntPtr.Zero;
        }
#endif

        private static string NormalizeBaseUrl(string? url)
        {
            var raw = (url ?? string.Empty).Trim();
            if (raw.Length == 0)
                return string.Empty;

            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                raw = "https://" + raw;

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return string.Empty;

            var baseUrl = uri.GetLeftPart(UriPartial.Authority);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return string.Empty;

            return baseUrl.TrimEnd('/');
        }

        private static string BuildSingleFileName(CallItem call, Uri audioUri)
        {
            var ext = Path.GetExtension(audioUri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".mp3";

            var ts = call.Timestamp == default ? DateTime.Now : call.Timestamp;
            var talkgroup = SanitizeFilePart(call.Talkgroup);
            var id = SanitizeFilePart(string.IsNullOrWhiteSpace(call.BackendId) ? "" : call.BackendId);

            var name = ts.ToString("yyyy-MM-dd_HHmmss");
            if (talkgroup.Length > 0)
                name += "_" + talkgroup;
            if (id.Length > 0)
                name += "_" + id;

            return name + ext;
        }

        private static string BuildZipFileName(IReadOnlyList<CallItem> calls)
        {
            var start = calls[0].Timestamp == default ? DateTime.Now : calls[0].Timestamp;
            var end = calls[calls.Count - 1].Timestamp == default ? start : calls[calls.Count - 1].Timestamp;

            var name = $"History_{start:yyyy-MM-dd_HHmmss}_to_{end:yyyy-MM-dd_HHmmss}.zip";
            return name;
        }

        private static string BuildZipEntryName(int index, CallItem call, Uri audioUri)
        {
            var ext = Path.GetExtension(audioUri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".mp3";

            var ts = call.Timestamp == default ? DateTime.Now : call.Timestamp;
            var talkgroup = SanitizeFilePart(call.Talkgroup);

            var prefix = index.ToString("D3");
            var name = $"{prefix}_{ts:yyyy-MM-dd_HHmmss}";
            if (talkgroup.Length > 0)
                name += "_" + talkgroup;

            return name + ext;
        }

        private static string SanitizeFilePart(string? value)
        {
            var s = (value ?? string.Empty).Trim();
            if (s.Length == 0)
                return string.Empty;

            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            while (s.Contains("__", StringComparison.Ordinal))
                s = s.Replace("__", "_", StringComparison.Ordinal);

            return s.Trim('_');
        }
    }
}
