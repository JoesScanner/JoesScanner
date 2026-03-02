using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using JoesScanner.Models;
using Microsoft.Maui.ApplicationModel;

namespace JoesScanner.Services
{
    // Resolves ///word.word.word to coordinates using the what3words Convert To Coordinates API.
    // No geocoding is performed here.
    public sealed class What3WordsService : IWhat3WordsService
    {
        private readonly ISettingsService _settings;
        private readonly HttpClient _http;

        // In-memory cache keyed by normalized words (no leading ///).
        private readonly ConcurrentDictionary<string, (double Lat, double Lng)> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.OrdinalIgnoreCase);

        public What3WordsService(ISettingsService settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
        }

        public async Task ResolveCoordinatesIfNeededAsync(CallItem item, CancellationToken cancellationToken)
        {
            if (item == null)
                return;

            // Setting disabled or no words.
            if (!_settings.What3WordsLinksEnabled)
                return;

            var apiKey = (_settings.What3WordsApiKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                return;

            var w3w = (item.What3WordsAddress ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(w3w))
                return;

            var normalized = NormalizeWords(w3w);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            // Already resolved for this item.
            if (item.HasWhat3WordsCoordinates && string.Equals(item.What3WordsNormalized, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            // Cache hit.
            if (_cache.TryGetValue(normalized, out var cached))
            {
                Apply(item, normalized, cached.Lat, cached.Lng);
                return;
            }

            // Avoid a thundering herd when multiple calls contain the same w3w.
            if (!_inflight.TryAdd(normalized, 0))
                return;

            try
            {
                var uri = BuildConvertToCoordinatesUri(normalized, apiKey);
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    if (AppLog.IsEnabled)
                        AppLog.Add(() => $"W3W: convert-to-coordinates failed {(int)resp.StatusCode} for '{normalized}'");
                    return;
                }

                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(json))
                    return;

                if (!TryParseLatLng(json, out var lat, out var lng))
                {
                    if (AppLog.IsEnabled)
                        AppLog.Add(() => $"W3W: convert-to-coordinates parse failed for '{normalized}'");
                    return;
                }

                _cache[normalized] = (lat, lng);
                Apply(item, normalized, lat, lng);

                if (AppLog.IsEnabled)
                    AppLog.Add(() => $"W3W: resolved '{normalized}' to {lat:F6},{lng:F6}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (AppLog.IsEnabled)
                    AppLog.Add(() => $"W3W: convert-to-coordinates error for '{normalized}'. {ex.Message}");
            }
            finally
            {
                _inflight.TryRemove(normalized, out _);
            }
        }

        private static void Apply(CallItem item, string normalized, double lat, double lng)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    item.What3WordsNormalized = normalized;
                    item.What3WordsLatitude = lat;
                    item.What3WordsLongitude = lng;
                }
                catch
                {
                }
            });
        }

        private static string NormalizeWords(string words)
        {
            var s = (words ?? string.Empty).Trim();
            if (s.StartsWith("///", StringComparison.Ordinal))
                s = s.Substring(3);
            s = s.Trim();

            // Must be exactly 3 dot-separated parts.
            var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 3)
                return string.Empty;

            return $"{parts[0]}.{parts[1]}.{parts[2]}".ToLowerInvariant();
        }

        private static Uri BuildConvertToCoordinatesUri(string normalizedWords, string apiKey)
        {
            var encoded = Uri.EscapeDataString(normalizedWords);
            var encodedKey = Uri.EscapeDataString(apiKey);
            return new Uri($"https://api.what3words.com/v3/convert-to-coordinates?words={encoded}&key={encodedKey}");
        }

        private static bool TryParseLatLng(string json, out double lat, out double lng)
        {
            lat = 0;
            lng = 0;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("coordinates", out var coords))
                    return false;
                if (!coords.TryGetProperty("lat", out var latEl))
                    return false;
                if (!coords.TryGetProperty("lng", out var lngEl))
                    return false;

                lat = latEl.GetDouble();
                lng = lngEl.GetDouble();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
