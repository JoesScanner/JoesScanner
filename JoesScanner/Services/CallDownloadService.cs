using JoesScanner.Models;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace JoesScanner.Services
{
    public sealed class CallDownloadService : ICallDownloadService
    {
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
                return;

            UpdateAuthorizationHeader();

            var fileName = BuildSingleFileName(call, uri);
            var tempPath = Path.Combine(FileSystem.CacheDirectory, fileName);

            try
            {
                using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var inStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var outStream = File.Create(tempPath);
                await inStream.CopyToAsync(outStream, cancellationToken);

                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = "Save call",
                    File = new ShareFile(tempPath)
                });
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: single failed. ex={ex.GetType().Name}: {ex.Message}");
            }
        }

        public async Task DownloadRangeZipAsync(IReadOnlyList<CallItem> calls, CancellationToken cancellationToken = default)
        {
            if (calls == null || calls.Count == 0)
                return;

            UpdateAuthorizationHeader();

            var zipName = BuildZipFileName(calls);
            var zipPath = Path.Combine(FileSystem.CacheDirectory, zipName);

            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                await using var zipFileStream = File.Create(zipPath);
                using var zip = new ZipArchive(zipFileStream, ZipArchiveMode.Create, leaveOpen: false);

                for (var i = 0; i < calls.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var call = calls[i];
                    var uri = TryBuildAudioUri(call);
                    if (uri == null)
                        continue;

                    var entryName = BuildZipEntryName(i + 1, call, uri);
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

                    using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    await using var inStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var entryStream = entry.Open();
                    await inStream.CopyToAsync(entryStream, cancellationToken);
                }

                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = "Save calls",
                    File = new ShareFile(zipPath)
                });
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Download: range zip failed. ex={ex.GetType().Name}: {ex.Message}");
            }
        }

        private Uri? TryBuildAudioUri(CallItem call)
        {
            var raw = (call.AudioUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
                return absolute;

            var baseUrl = NormalizeBaseUrl(_settingsService.ServerUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            var combined = baseUrl.TrimEnd('/') + "/" + raw.TrimStart('/');
            return Uri.TryCreate(combined, UriKind.Absolute, out var result) ? result : null;
        }

        private void UpdateAuthorizationHeader()
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;

            var serverUrlRaw = _settingsService.ServerUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(serverUrlRaw))
                return;

            var serverKey = NormalizeBaseUrl(serverUrlRaw);
            if (string.IsNullOrWhiteSpace(serverKey))
                return;

            var hostedJoe = string.Equals(serverKey.TrimEnd('/'), "https://app.joesscanner.com", StringComparison.OrdinalIgnoreCase);
            var useServiceCreds = false;

            if (hostedJoe)
            {
                var ok = _settingsService.SubscriptionLastStatusOk;
                var expires = _settingsService.SubscriptionExpiresUtc;
                var tier = _settingsService.SubscriptionTierLevel;

                if (ok && tier >= 1 && expires.HasValue && expires.Value.ToUniversalTime() > DateTime.UtcNow)
                {
                    useServiceCreds = true;
                }
                else
                {
                    return;
                }
            }

            const string serviceUser = "secappass";
            const string servicePass = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";

            var username = useServiceCreds ? serviceUser : (_settingsService.BasicAuthUsername ?? string.Empty).Trim();
            var password = useServiceCreds ? servicePass : (_settingsService.BasicAuthPassword ?? string.Empty);

            if (string.IsNullOrWhiteSpace(username))
                return;

            var raw = $"{username}:{password}";
            var bytes = Encoding.ASCII.GetBytes(raw);
            var base64 = Convert.ToBase64String(bytes);

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64);
        }

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
