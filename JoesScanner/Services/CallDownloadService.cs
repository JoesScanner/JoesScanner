using JoesScanner.Models;
using JoesScanner.Helpers;
using System.IO.Compression;
using System.Net;
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
        private readonly ISettingsService _settingsService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly HttpClient _httpClient;
        private readonly TimeProvider _clock;

        public CallDownloadService(ISettingsService settingsService, ISubscriptionService subscriptionService)
            : this(settingsService, subscriptionService, null, TimeProvider.System)
        {
        }

        internal CallDownloadService(ISettingsService settingsService, ISubscriptionService subscriptionService,
            HttpClient? httpClient, TimeProvider clock)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _subscriptionService = subscriptionService;
            _clock = clock;

            _httpClient = httpClient ?? ServerAuthenticationHandler.CreateClient(_settingsService, new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate
            });
            _httpClient.Timeout = TimeSpan.FromSeconds(25);
            _httpClient.DefaultRequestVersion = HttpVersion.Version11;
            _httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
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
                using var response = await AcquireRecordingAsync(call, uri, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Add(() => $"Download: single failed. HTTP {(int)response.StatusCode}.");
                    await ShowErrorAsync("Download failed", $"Server returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
                    return;
                }

                await using var inStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var outStream = File.Create(tempPath);
                await inStream.CopyToAsync(outStream, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RecordingAccessException ex)
            {
                await ShowErrorAsync("Download failed", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: single failed ({ex.GetType().Name}).");
                await ShowErrorAsync("Download failed", ex is FirewallAuthenticationException ? ex.Message : "Could not download the audio file.");
                return;
            }

            try
            {
                await SaveOrShareFileAsync(tempPath, fileName);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: share failed. ex={DiagnosticRedaction.Describe(ex)}");
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
                string? accessError = null;

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

                    try
                    {
                        using var response = await AcquireRecordingAsync(call, uri, cancellationToken);

                        if (!response.IsSuccessStatusCode)
                        {
                            AppLog.Add(() => $"Download: range item {i + 1} failed. HTTP {(int)response.StatusCode}.");
                            failedCount++;
                            continue;
                        }

                        var entryName = BuildZipEntryName(i + 1, call, uri);
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        await using var inStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                        await using var entryStream = entry.Open();
                        await inStream.CopyToAsync(entryStream, cancellationToken);
                        downloadedCount++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is FirewallAuthenticationException or RecordingAccessException)
                    {
                        accessError ??= ex.Message;
                        failedCount++;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Add(() => $"Download: range item {i + 1} failed ({ex.GetType().Name}).");
                        failedCount++;
                    }
                }

                if (downloadedCount == 0)
                {
                    await ShowErrorAsync("Download failed", accessError ??
                        "Could not download any of the selected calls. Check your connection and server settings.");
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: range zip failed. ex={DiagnosticRedaction.Describe(ex)}");
                await ShowErrorAsync("Download failed", $"Could not create the zip file: {ex.Message}");
                return;
            }

            try
            {
                await SaveOrShareFileAsync(zipPath, zipName);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: share zip failed. ex={DiagnosticRedaction.Describe(ex)}");
                await ShowErrorAsync("Save failed", $"The zip was created but could not be shared: {ex.Message}");
            }
        }

        private async Task<HttpResponseMessage> AcquireRecordingAsync(CallItem call, Uri uri, CancellationToken token)
        {
            var server = call.RecordingServer ?? call.LiveState?.ServerUrl ?? _settingsService.ServerUrl ?? "";
            var access = call.RecordingAccess ?? new RecordingAccessContext(RecordingOrigin.History, false);
            if (HostedServerRules.RequiresApiFirewallCredentials(_settingsService, uri) &&
                !HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server))
                throw new RecordingAccessException("The recording's hosted server could not be verified.");

            await RecordingPlaybackAccess.VerifyAsync(_settingsService, _subscriptionService, server,
                call.RecordingTimeUtc, access, currentLive: false, requireAccount: access.Protected, token, _clock, independent: call.ServerId.HasValue)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (call.ServerId.HasValue && Uri.TryCreate(server, UriKind.Absolute, out var origin))
                request.Options.Set(ServerAuthenticationHandler.Recorder, new RecorderRequestScope(origin, call.ServerId));
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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

            var baseUrl = NormalizeBaseUrl(call.RecordingServer ?? call.LiveState?.ServerUrl ?? _settingsService.ServerUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            var combined = baseUrl.TrimEnd('/') + "/" + raw.TrimStart('/');
            return Uri.TryCreate(combined, UriKind.Absolute, out var result) ? result : null;
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
