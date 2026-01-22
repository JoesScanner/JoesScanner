using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using JoesScanner.Models;

namespace JoesScanner.Services
{
    public sealed class CommunicationsService : ICommunicationsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;

        public CommunicationsService(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<CommsSyncResult> SyncAsync(
            string authServerBaseUrl,
            string sessionToken,
            long sinceSeq,
            bool forceSnapshot,
            CancellationToken cancellationToken = default)
        {
            authServerBaseUrl = (authServerBaseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(authServerBaseUrl))
                authServerBaseUrl = "https://joesscanner.com";

            if (!authServerBaseUrl.EndsWith("/", StringComparison.Ordinal))
                authServerBaseUrl += "/";

            if (string.IsNullOrWhiteSpace(sessionToken))
                return new CommsSyncResult { Ok = false, Message = "Not authenticated yet." };

            try
            {
                // Do not mutate HttpClient properties (BaseAddress, DefaultRequestHeaders, Timeout) after the
                // first request. The communications page polls repeatedly, and changing BaseAddress on a
                // reused HttpClient will throw: "This instance has already started one or more requests".
                var endpoint = new Uri(
                    new Uri(authServerBaseUrl, UriKind.Absolute),
                    "wp-json/joes-scanner/v1/comms/changes");

                var payload = new
                {
                    session_token = sessionToken,
                    since_seq = sinceSeq,
                    force_snapshot = forceSnapshot ? 1 : 0
                };

                using var response = await _httpClient.PostAsJsonAsync(
                    endpoint,
                    payload,
                    JsonOptions,
                    cancellationToken);

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!response.IsSuccessStatusCode)
                {
                    return new CommsSyncResult
                    {
                        Ok = false,
                        Message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                    };
                }

                if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    return new CommsSyncResult { Ok = false, Message = "Unexpected response type." };
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var root = doc.RootElement;

                var ok = TryGetBool(root, "ok");
                var msg = TryGetString(root, "message");

                var result = new CommsSyncResult
                {
                    Ok = ok,
                    Message = msg,
                    NextSeq = TryGetLong(root, "next_seq")
                };

                if (!ok)
                    return result;

                // Snapshot (optional)
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("snapshot", out var snapArr) &&
                    snapArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in snapArr.EnumerateArray())
                    {
                        var m = ParseMessage(el);
                        if (m != null)
                            result.Snapshot.Add(m);
                    }
                }

                // Changes (optional)
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("changes", out var changesArr) &&
                    changesArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ch in changesArr.EnumerateArray())
                    {
                        if (ch.ValueKind != JsonValueKind.Object)
                            continue;

                        var type = TryGetString(ch, "type") ?? string.Empty;
                        var mid = TryGetLong(ch, "message_id");

                        if (mid <= 0)
                            continue;

                        CommsMessage? message = null;
                        if (type.Equals("upsert", StringComparison.OrdinalIgnoreCase) &&
                            ch.TryGetProperty("message", out var msgObj))
                        {
                            message = ParseMessage(msgObj);
                        }

                        result.Changes.Add(new CommsChange
                        {
                            Type = type,
                            MessageId = mid,
                            Message = message
                        });
                    }
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                return new CommsSyncResult { Ok = false, Message = "Canceled." };
            }
            catch (Exception ex)
            {
                return new CommsSyncResult { Ok = false, Message = ex.Message };
            }
        }

        private static CommsMessage? ParseMessage(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object)
                return null;

            var id = TryGetLong(el, "id");
            var createdUtc = TryGetDateTimeUtc(el, "created_at_utc");
            var updatedUtc = TryGetDateTimeUtcNullable(el, "updated_at_utc");

            var author = TryGetString(el, "author_label");
            var text = TryGetString(el, "message_text");

            if (id <= 0 || createdUtc == null)
                return null;

            return new CommsMessage
            {
                Id = id,
                CreatedAtUtc = createdUtc.Value,
                UpdatedAtUtc = updatedUtc,
                AuthorLabel = author ?? string.Empty,
                MessageText = text ?? string.Empty
            };
        }

        private static bool TryGetBool(JsonElement root, string name)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
                if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
            }
            return false;
        }

        private static string? TryGetString(JsonElement root, string name)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.String) return el.GetString();
                return el.ToString();
            }
            return null;
        }

        private static long TryGetLong(JsonElement root, string name)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)) return v;
                if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
            }
            return 0;
        }

        private static DateTime? TryGetDateTimeUtc(JsonElement root, string name)
        {
            var s = TryGetString(root, name);
            if (string.IsNullOrWhiteSpace(s))
                return null;

            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                return dto.UtcDateTime;

            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);

            return null;
        }

        private static DateTime? TryGetDateTimeUtcNullable(JsonElement root, string name)
        {
            return TryGetDateTimeUtc(root, name);
        }
    }
}
