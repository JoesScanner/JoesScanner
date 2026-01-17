using JoesScanner.Models;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JoesScanner.Services
{
    // History lookups and searches against a Trunking Recorder server.
    // Lookup lists are fetched from dedicated JSON endpoints:
    // receiversjson, sitesjson, talkgroupsjson.
    public sealed class CallHistoryService : ICallHistoryService
    {
        private readonly ISettingsService _settingsService;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;

        private int _draw = 1;

        private const string ServiceAuthUsername = "secappass";
        private const string ServiceAuthPassword = "7a65vBLeqLjdRut5bSav4eMYGUJPrmjHhgnPmEji3q3S7tZ3K5aadFZz2EZtbaE7";

        public CallHistoryService(ISettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            var handler = new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };

            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            _httpClient.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            _httpClient.DefaultRequestHeaders.Add("DNT", "1");
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

            _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public async Task<HistoryLookupData> GetLookupDataAsync(HistorySearchFilters? currentFilters, CancellationToken cancellationToken = default)
        {
            // User preference: always return the full list for each dropdown.
            // Talkgroups also provide a grouped view (TalkgroupGroups) so the UI can
            // optionally filter children by the selected Site.

            var baseUrl = NormalizeBaseUrl(_settingsService.ServerUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return new HistoryLookupData
                {
                    Receivers = PrependAll(Array.Empty<HistoryLookupItem>()),
                    Sites = PrependAll(Array.Empty<HistoryLookupItem>()),
                    Talkgroups = PrependAll(Array.Empty<HistoryLookupItem>()),
                    TalkgroupGroups = new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase)
                };
            }

            UpdateAuthorizationHeader();

            var receiversEndpoint = new Uri(baseUrl + "/receiversjson");
            var sitesEndpoint = new Uri(baseUrl + "/sitesjson");
            var talkgroupsEndpoint = new Uri(baseUrl + "/talkgroupsjson");

            var receivers = await FetchLookupAsync(receiversEndpoint, cancellationToken);
            var sites = await FetchLookupAsync(sitesEndpoint, cancellationToken);
            var (talkgroups, groups) = await FetchTalkgroupsAsync(talkgroupsEndpoint, cancellationToken);

            return new HistoryLookupData
            {
                Receivers = PrependAll(receivers),
                Sites = PrependAll(sites),
                Talkgroups = PrependAll(talkgroups),
                TalkgroupGroups = groups
            };
        }

        public async Task<HistorySearchResult> SearchAroundAsync(
            DateTime targetLocalTime,
            HistorySearchFilters filters,
            int windowSize,
            CancellationToken cancellationToken = default)
        {
            windowSize = Math.Clamp(windowSize, 5, 100);

            var head = await FetchCallsPageAsync(
                start: 0,
                length: 1,
                filters: filters,
                cancellationToken: cancellationToken);

            var total = head.TotalMatches;
            if (total <= 0)
            {
                return new HistorySearchResult
                {
                    Calls = Array.Empty<CallItem>(),
                    AnchorIndex = 0,
                    TotalMatches = 0
                };
            }

            // calljson is newest first. Index increases as calls get older.
            var low = 0;
            var high = total - 1;

            while (low <= high)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mid = low + ((high - low) / 2);
                var midCall = await FetchCallAtIndexAsync(mid, filters, cancellationToken);
                if (midCall == null)
                    break;

                var ts = midCall.Timestamp;
                if (ts > targetLocalTime)
                {
                    low = mid + 1;
                }
                else if (ts < targetLocalTime)
                {
                    high = mid - 1;
                }
                else
                {
                    low = mid;
                    break;
                }
            }

            var candidateA = low;
            var candidateB = low - 1;

            CallItem? callA = null;
            CallItem? callB = null;

            if (candidateA >= 0 && candidateA < total)
                callA = await FetchCallAtIndexAsync(candidateA, filters, cancellationToken);

            if (candidateB >= 0 && candidateB < total)
                callB = await FetchCallAtIndexAsync(candidateB, filters, cancellationToken);

            var bestIndex = 0;
            if (callA != null && callB != null)
            {
                var deltaA = Math.Abs((callA.Timestamp - targetLocalTime).TotalSeconds);
                var deltaB = Math.Abs((callB.Timestamp - targetLocalTime).TotalSeconds);
                bestIndex = deltaA <= deltaB ? candidateA : candidateB;
            }
            else if (callA != null)
            {
                bestIndex = candidateA;
            }
            else if (callB != null)
            {
                bestIndex = candidateB;
            }

            var aboveCount = windowSize / 2;
            var start = bestIndex - aboveCount;
            if (start < 0)
                start = 0;

            var maxStart = Math.Max(0, total - windowSize);
            if (start > maxStart)
                start = maxStart;

            var page = await FetchCallsPageAsync(start, windowSize, filters, cancellationToken);
            var anchorIndex = bestIndex - start;
            if (anchorIndex < 0)
                anchorIndex = 0;
            if (anchorIndex >= page.Calls.Count)
                anchorIndex = Math.Max(0, page.Calls.Count - 1);

            return new HistorySearchResult
            {
                Calls = page.Calls,
                StartIndex = start,
                AnchorGlobalIndex = bestIndex,
                AnchorIndex = anchorIndex,
                TotalMatches = total
            };
        }

        public async Task<HistoryCallsPage> GetCallsPageAsync(
            int start,
            int length,
            HistorySearchFilters filters,
            CancellationToken cancellationToken = default)
        {
            var page = await FetchCallsPageAsync(start, length, filters, cancellationToken);
            return new HistoryCallsPage
            {
                Calls = page.Calls,
                TotalMatches = page.TotalMatches
            };
        }

        private sealed class CallsPage
        {
            public List<CallItem> Calls { get; init; } = new();
            public int TotalMatches { get; init; }
        }

        private async Task<CallItem?> FetchCallAtIndexAsync(int index, HistorySearchFilters filters, CancellationToken cancellationToken)
        {
            var page = await FetchCallsPageAsync(index, 1, filters, cancellationToken);
            return page.Calls.Count > 0 ? page.Calls[0] : null;
        }

        private async Task<CallsPage> FetchCallsPageAsync(int start, int length, HistorySearchFilters filters, CancellationToken cancellationToken)
        {
            start = Math.Max(0, start);
            length = Math.Clamp(length, 0, 200);

            var baseUrl = NormalizeBaseUrl(_settingsService.ServerUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return new CallsPage();

            UpdateAuthorizationHeader();

            var callsUrl = baseUrl + "/calljson";
            var payload = BuildDataTablesPayload(_draw, start, length, filters);
            _draw++;

            var jsonPayload = JsonSerializer.Serialize(payload, _jsonOptions);
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await _httpClient.PostAsync(callsUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var statusInt = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    throw new InvalidOperationException($"History auth failed (HTTP {statusInt} {response.ReasonPhrase}).");

                var raw = string.Empty;
                try { raw = await response.Content.ReadAsStringAsync(cancellationToken); } catch { }

                throw new InvalidOperationException(
                    $"History calljson failed (HTTP {statusInt} {response.ReasonPhrase}). Body={Truncate(raw, 400)}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return new CallsPage();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var totalMatches = 0;
            if (root.TryGetProperty("recordsFiltered", out var rf))
            {
                if (rf.ValueKind == JsonValueKind.Number)
                    totalMatches = rf.GetInt32();
                else
                    int.TryParse(rf.ToString(), out totalMatches);
            }

            var result = new List<CallItem>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in data.EnumerateArray())
                {
                    var row = ParseRow(e);
                    var call = ConvertRowToCallItem(row, baseUrl);
                    if (call != null)
                        result.Add(call);
                }
            }

            return new CallsPage
            {
                Calls = result,
                TotalMatches = totalMatches
            };
        }

        private static string NormalizeBaseUrl(string? url)
        {
            var baseUrl = (url ?? string.Empty).Trim();
            return baseUrl.TrimEnd('/');
        }

        private void UpdateAuthorizationHeader()
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;

            var serverUrl = _settingsService.ServerUrl ?? string.Empty;
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
                return;

            var isJoesScannerHost = serverUri.Host.EndsWith("app.joesscanner.com", StringComparison.OrdinalIgnoreCase);

            string username;
            string password;

            if (isJoesScannerHost)
            {
                username = ServiceAuthUsername;
                password = ServiceAuthPassword;
            }
            else
            {
                username = _settingsService.BasicAuthUsername;
                password = _settingsService.BasicAuthPassword ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(username))
                return;

            var raw = $"{username}:{password}";
            var bytes = Encoding.ASCII.GetBytes(raw);
            var base64 = Convert.ToBase64String(bytes);

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64);
        }

        private static string Truncate(string? value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (maxChars <= 0)
                return string.Empty;

            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        private static IReadOnlyList<HistoryLookupItem> PrependAll(IReadOnlyList<HistoryLookupItem> items)
        {
            var list = items?.ToList() ?? new List<HistoryLookupItem>();

            if (list.Count == 0 || !string.Equals(list[0].Label, "All", StringComparison.OrdinalIgnoreCase))
                list.Insert(0, new HistoryLookupItem { Label = "All", Value = string.Empty });

            return list;
        }

        private async Task<IReadOnlyList<HistoryLookupItem>> FetchLookupAsync(Uri uri, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(uri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<HistoryLookupItem>();

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(body))
                    return Array.Empty<HistoryLookupItem>();

                return ParseLookupBody(body);
            }
            catch
            {
                return Array.Empty<HistoryLookupItem>();
            }
        }

        private async Task<(IReadOnlyList<HistoryLookupItem> Talkgroups, Dictionary<string, IReadOnlyList<HistoryLookupItem>> Groups)> FetchTalkgroupsAsync(
            Uri uri,
            CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(uri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return (Array.Empty<HistoryLookupItem>(), new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase));

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(body))
                    return (Array.Empty<HistoryLookupItem>(), new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase));

                // talkgroupsjson is typically Select2 style: { results: [ { text, children:[{id,text}] } ] }
                body = body.Trim();
                if (body.StartsWith("{", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                    {
                        var all = new List<HistoryLookupItem>();
                        var groups = new Dictionary<string, List<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);

                        foreach (var g in results.EnumerateArray())
                        {
                            if (g.ValueKind != JsonValueKind.Object)
                                continue;

                            var groupText = GetFirstString(g, "text", "Text", "label", "Label", "name", "Name") ?? string.Empty;
                            var groupKey = NormalizeGroupKey(groupText);

                            if (g.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                            {
                                var childItems = new List<HistoryLookupItem>();
                                foreach (var c in children.EnumerateArray())
                                {
                                    if (c.ValueKind != JsonValueKind.Object)
                                        continue;

                                    var id = (GetFirstString(c, "id", "Id", "value", "Value") ?? string.Empty).Trim();
                                    var text = (GetFirstString(c, "text", "Text", "label", "Label", "name", "Name") ?? string.Empty).Trim();
                                    text = CleanDisplayLabel(text);

                                    if (string.IsNullOrWhiteSpace(text))
                                        continue;

                                    if (string.IsNullOrWhiteSpace(id))
                                        id = text;

                                    var item = new HistoryLookupItem { Label = text, Value = id };
                                    childItems.Add(item);
                                    all.Add(item);
                                }

                                if (childItems.Count > 0)
                                {
                                    childItems = DedupAndSort(childItems);

                                    if (!string.IsNullOrWhiteSpace(groupKey))
                                        groups[groupKey] = childItems;
                                }

                                continue;
                            }

                            // Some endpoints include leaf items in results.
                            var leafId = (GetFirstString(g, "id", "Id", "value", "Value") ?? string.Empty).Trim();
                            var leafText = CleanDisplayLabel((GetFirstString(g, "text", "Text") ?? string.Empty).Trim());
                            if (!string.IsNullOrWhiteSpace(leafText))
                            {
                                if (string.IsNullOrWhiteSpace(leafId))
                                    leafId = leafText;

                                all.Add(new HistoryLookupItem { Label = leafText, Value = leafId });
                            }
                        }

                        return (DedupAndSort(all), groups.ToDictionary(k => k.Key, v => (IReadOnlyList<HistoryLookupItem>)v.Value, StringComparer.OrdinalIgnoreCase));
                    }
                }

                // Fallback to the generic parser.
                return (ParseLookupBody(body), new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                return (Array.Empty<HistoryLookupItem>(), new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase));
            }
        }

        private static IReadOnlyList<HistoryLookupItem> ParseLookupBody(string body)
        {
            body = body.Trim();

            // JSON
            if ((body.StartsWith("[", StringComparison.Ordinal) && body.EndsWith("]", StringComparison.Ordinal)) ||
                (body.StartsWith("{", StringComparison.Ordinal) && body.EndsWith("}", StringComparison.Ordinal)))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    return ExtractLookupItemsFromJson(doc.RootElement);
                }
                catch
                {
                    // fall through
                }
            }

            // HTML select options
            if (body.Contains("<option", StringComparison.OrdinalIgnoreCase))
            {
                var items = new List<HistoryLookupItem>();
                var parts = body.Split(new[] { "<option", "</option>" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    var idx = p.IndexOf('>');
                    if (idx < 0)
                        continue;

                    var text = WebUtility.HtmlDecode(p.Substring(idx + 1)).Trim();
                    text = CleanDisplayLabel(text);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    items.Add(new HistoryLookupItem { Label = text, Value = text });
                }
                return DedupAndSort(items);
            }

            // Plain text, one item per line
            var lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 1)
            {
                var items = lines
                    .Select(x => CleanDisplayLabel(x.Trim()))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => new HistoryLookupItem { Label = x, Value = x })
                    .ToList();

                return DedupAndSort(items);
            }

            // Single line fallback
            if (!string.IsNullOrWhiteSpace(body))
            {
                var text = CleanDisplayLabel(body);
                if (!string.IsNullOrWhiteSpace(text))
                    return DedupAndSort(new List<HistoryLookupItem> { new HistoryLookupItem { Label = text, Value = text } });
            }

            return Array.Empty<HistoryLookupItem>();
        }

        private static IReadOnlyList<HistoryLookupItem> ExtractLookupItemsFromJson(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array)
                return DedupAndSort(ParseJsonArray(root));

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "data", "Data", "aaData", "results", "items", "Talkgroups", "Sites", "Receivers" })
                {
                    if (root.TryGetProperty(key, out var v))
                    {
                        if (v.ValueKind == JsonValueKind.Array)
                            return DedupAndSort(ParseJsonArray(v));

                        if (v.ValueKind == JsonValueKind.Object)
                            return DedupAndSort(ParseJsonDictionary(v));
                    }
                }

                // Some endpoints return a dictionary at the root.
                return DedupAndSort(ParseJsonDictionary(root));
            }

            return Array.Empty<HistoryLookupItem>();
        }

        private static List<HistoryLookupItem> ParseJsonDictionary(JsonElement obj)
        {
            var items = new List<HistoryLookupItem>();

            foreach (var prop in obj.EnumerateObject())
            {
                var key = (prop.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var v = prop.Value;

                string label;
                string value;

                if (v.ValueKind == JsonValueKind.String)
                {
                    label = CleanDisplayLabel((v.GetString() ?? string.Empty).Trim());
                    value = key;
                }
                else if (v.ValueKind == JsonValueKind.Number)
                {
                    label = CleanDisplayLabel(v.ToString().Trim());
                    value = key;
                }
                else if (v.ValueKind == JsonValueKind.Array)
                {
                    var parts = v.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()
                                   : x.ValueKind == JsonValueKind.Number ? x.ToString()
                                   : null)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => CleanDisplayLabel(x!.Trim()))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();

                    if (parts.Count == 1)
                    {
                        label = parts[0];
                        value = parts[0];
                    }
                    else if (parts.Count >= 2)
                    {
                        value = parts[0];
                        label = parts[1];
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (v.ValueKind == JsonValueKind.Object)
                {
                    label = CleanDisplayLabel((GetFirstString(v,
                                "label", "Label", "text", "Text", "name", "Name",
                                "TargetLabel", "Talkgroup", "TalkGroup", "TalkgroupLabel", "TalkgroupName", "Description"
                            ) ?? string.Empty).Trim());

                    value = (GetFirstString(v,
                                "value", "Value", "id", "Id",
                                "TargetID", "TargetId", "TalkgroupID", "TalkgroupId", "TalkGroupID", "TGID", "tgid"
                            ) ?? string.Empty).Trim();

                    if (string.IsNullOrWhiteSpace(value))
                        value = key;

                    if (string.IsNullOrWhiteSpace(label))
                        label = CleanDisplayLabel(value);
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(label))
                    continue;

                if (string.IsNullOrWhiteSpace(value))
                    value = label;

                items.Add(new HistoryLookupItem { Label = label, Value = value });
            }

            return items;
        }

        private static List<HistoryLookupItem> ParseJsonArray(JsonElement array)
        {
            var items = new List<HistoryLookupItem>();

            foreach (var el in array.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = CleanDisplayLabel((el.GetString() ?? string.Empty).Trim());
                    if (!string.IsNullOrWhiteSpace(s))
                        items.Add(new HistoryLookupItem { Label = s, Value = s });
                    continue;
                }

                if (el.ValueKind == JsonValueKind.Number)
                {
                    var s = CleanDisplayLabel(el.ToString().Trim());
                    if (!string.IsNullOrWhiteSpace(s))
                        items.Add(new HistoryLookupItem { Label = s, Value = s });
                    continue;
                }

                // Support array rows like [1016,"G-Event channel"]
                if (el.ValueKind == JsonValueKind.Array)
                {
                    var parts = el.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()
                                   : x.ValueKind == JsonValueKind.Number ? x.ToString()
                                   : null)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => CleanDisplayLabel(x!.Trim()))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();

                    if (parts.Count == 1)
                        items.Add(new HistoryLookupItem { Label = parts[0], Value = parts[0] });
                    else if (parts.Count >= 2)
                        items.Add(new HistoryLookupItem { Label = parts[1], Value = parts[0] });

                    continue;
                }

                if (el.ValueKind == JsonValueKind.Object)
                {
                    var label = CleanDisplayLabel((GetFirstString(el,
                            "label", "Label", "text", "Text", "name", "Name",
                            "SystemName", "SiteLabel", "TargetLabel", "Talkgroup", "TalkGroup", "TalkgroupLabel", "TalkgroupName", "Description"
                        ) ?? string.Empty).Trim());

                    var value = (GetFirstString(el,
                            "value", "Value", "id", "Id", "SystemID", "SystemId", "SiteID", "SiteId",
                            "TargetID", "TargetId", "TalkgroupID", "TalkgroupId", "TalkGroupID", "TGID", "tgid"
                        ) ?? string.Empty).Trim();

                    if (string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(value))
                        label = CleanDisplayLabel(value);
                    if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(label))
                        value = label;

                    if (!string.IsNullOrWhiteSpace(label))
                        items.Add(new HistoryLookupItem { Label = label, Value = value });
                }
            }

            return items;
        }

        private static string? GetFirstString(JsonElement obj, params string[] names)
        {
            foreach (var n in names)
            {
                if (obj.TryGetProperty(n, out var p))
                {
                    if (p.ValueKind == JsonValueKind.String)
                        return p.GetString();

                    if (p.ValueKind == JsonValueKind.Number)
                        return p.ToString();

                    if (p.ValueKind == JsonValueKind.Null)
                        return null;
                }
            }

            return null;
        }

        private static string NormalizeGroupKey(string input)
        {
            var s = CleanDisplayLabel(input);
            return s.Trim();
        }

        private static string CleanDisplayLabel(string input)
        {
            var s = (input ?? string.Empty).Trim();
            if (s.Length == 0)
                return s;

            // Allow parentheses to display in dropdown labels.

            // Collapse multiple spaces.
            while (s.Contains("  ", StringComparison.Ordinal))
                s = s.Replace("  ", " ");

            return s.Trim();
        }

        private static List<HistoryLookupItem> DedupAndSort(List<HistoryLookupItem> items)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<HistoryLookupItem>();

            foreach (var i in items)
            {
                var label = CleanDisplayLabel(i.Label ?? string.Empty);
                var value = (i.Value ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(label))
                    continue;

                if (string.IsNullOrWhiteSpace(value))
                    value = label;

                var key = label + "|" + value;
                if (seen.Add(key))
                    result.Add(new HistoryLookupItem { Label = label, Value = value });
            }

            return result
                .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static DataTablesPayload BuildDataTablesPayload(int draw, int start, int length, HistorySearchFilters filters)
        {
            // Match the Trunking Recorder /calljson DataTables column schema.
            // Filters are applied via per-column search values, which is what TR expects.

            var receiverValue = (filters.Receiver?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(receiverValue))
                receiverValue = (filters.Receiver?.Label ?? string.Empty).Trim();

            var siteValue = (filters.Site?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(siteValue))
                siteValue = (filters.Site?.Label ?? string.Empty).Trim();

            var globalSearch = string.Empty;

            var talkgroupValue = (filters.Talkgroup?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(talkgroupValue))
                talkgroupValue = (filters.Talkgroup?.Label ?? string.Empty).Trim();

            var columns = new List<DataTablesColumn>
            {
                Col(null, "Details", false),
                Col("Time", "Time", false),
                Col("Date", "Date", false),
                Col("TargetID", "Target", false),
                Col("TargetLabel", "TargetLabel", false),
                Col("TargetTag", "TargetTag", false),
                Col("SourceID", "Source", false),
                Col("SourceLabel", "SourceLabel", false),
                Col("SourceTag", "SourceTag", false),
                Col("CallDuration", "CallLength", false),
                Col("CallAudioType", "VoiceType", false),
                Col("CallType", "CallType", false),
                Col("SiteID", "Site", false),
                Col("SiteLabel", "SiteLabel", false),
                Col("SystemID", "System", false),
                Col("SystemLabel", "SystemLabel", false),
                Col("SystemType", "SystemType", false),
                Col("AudioStartPos", "AudioStartPos", false),
                Col("LCN", "LCN", false),
                Col("Frequency", "Frequency", false),
                Col("VoiceReceiver", "Receiver", false),
                Col("CallText", "CallText", false),
                Col("AudioFilename", "Filename", false)
            };

            // Apply dropdown filters via column search values.
            // Talkgroup: TR example uses TargetID search = "14410-ICAWIN".
            if (!string.IsNullOrWhiteSpace(talkgroupValue))
                columns[3].Search.Value = talkgroupValue;

            // Receiver: filter by VoiceReceiver.
            if (!string.IsNullOrWhiteSpace(receiverValue))
                columns[20].Search.Value = receiverValue;

            // Site: some TR builds filter on SiteID, others on SiteLabel.
            // Set both so the dropdown works across backends.
            if (!string.IsNullOrWhiteSpace(siteValue))
            {
                var siteId = (filters.Site?.Value ?? string.Empty).Trim();
                var siteLabel = (filters.Site?.Label ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(siteId))
                    siteId = siteValue;
                if (string.IsNullOrWhiteSpace(siteLabel))
                    siteLabel = siteValue;

                columns[12].Search.Value = siteId;
                columns[13].Search.Value = siteLabel;

                // If Value and Label differ, also allow either to match.
                if (!string.Equals(siteId, siteLabel, StringComparison.OrdinalIgnoreCase))
                {
                    columns[12].Search.Value = siteId;
                    columns[13].Search.Value = siteLabel;
                }

                // Fallback: some Trunking Recorder builds ignore per-column site search.
                // Also set the global DataTables search value to the site label so the Site dropdown still constrains results.
                globalSearch = !string.IsNullOrWhiteSpace(siteLabel) ? siteLabel : siteId;
            }

            return new DataTablesPayload
            {
                Draw = draw,
                Start = start,
                Length = length,
                Search = new DataTablesSearch { Value = globalSearch, Regex = false },
                Order = Array.Empty<DataTablesOrder>(),
                Columns = columns.ToArray(),
                SmartSort = false
            };

            static DataTablesColumn Col(object? data, string name, bool orderable)
            {
                return new DataTablesColumn
                {
                    Data = data,
                    Name = name,
                    Searchable = true,
                    Orderable = orderable,
                    Search = new DataTablesSearch { Value = string.Empty, Regex = false }
                };
            }
        }

        private sealed class DataTablesPayload
        {
            [JsonPropertyName("draw")]
            public int Draw { get; set; }

            [JsonPropertyName("start")]
            public int Start { get; set; }

            [JsonPropertyName("length")]
            public int Length { get; set; }

            [JsonPropertyName("search")]
            public DataTablesSearch Search { get; set; } = new();

            [JsonPropertyName("order")]
            public DataTablesOrder[] Order { get; set; } = Array.Empty<DataTablesOrder>();

            [JsonPropertyName("columns")]
            public DataTablesColumn[] Columns { get; set; } = Array.Empty<DataTablesColumn>();

            // Trunking Recorder includes this field in some builds.
            [JsonPropertyName("SmartSort")]
            public bool SmartSort { get; set; }
        }

        private sealed class DataTablesSearch
        {
            [JsonPropertyName("value")]
            public string Value { get; set; } = string.Empty;

            [JsonPropertyName("regex")]
            public bool Regex { get; set; }
        }

        private sealed class DataTablesOrder
        {
            [JsonPropertyName("column")]
            public int Column { get; set; }

            [JsonPropertyName("dir")]
            public string Dir { get; set; } = "desc";
        }

        private sealed class DataTablesColumn
        {
            [JsonPropertyName("data")]
            public object? Data { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("searchable")]
            public bool Searchable { get; set; }

            [JsonPropertyName("orderable")]
            public bool Orderable { get; set; }

            [JsonPropertyName("search")]
            public DataTablesSearch Search { get; set; } = new();
        }

        private sealed class CallRow
        {
            public string? DT_RowId { get; set; }
            public string? StartTime { get; set; }
            public string? StartTimeUTC { get; set; }
            public string? TargetID { get; set; }
            public string? TargetLabel { get; set; }
            public string? SourceID { get; set; }
            public string? SourceLabel { get; set; }
            public string? SiteID { get; set; }
            public string? SiteLabel { get; set; }
            public string? VoiceReceiver { get; set; }
            public string? CallText { get; set; }
            public string? Transcript { get; set; }
            public string? Transcription { get; set; }
            public string? AudioFilename { get; set; }
            public string? CallDuration { get; set; }
        }

        private static CallRow ParseRow(JsonElement e)
        {
            return new CallRow
            {
                DT_RowId = GetString(e, "DT_RowId"),
                StartTime = GetString(e, "StartTime"),
                StartTimeUTC = GetString(e, "StartTimeUTC"),
                TargetID = GetString(e, "TargetID"),
                TargetLabel = GetString(e, "TargetLabel"),
                SourceID = GetString(e, "SourceID"),
                SourceLabel = GetString(e, "SourceLabel"),
                SiteID = GetString(e, "SiteID"),
                SiteLabel = GetString(e, "SiteLabel"),
                VoiceReceiver = GetString(e, "VoiceReceiver"),
                CallText = GetString(e, "CallText"),
                Transcript = GetString(e, "Transcript"),
                Transcription = GetString(e, "Transcription"),
                AudioFilename = GetStringAny(e, "AudioFilename", "AudioFileName", "AudioFile", "Filename", "FileName", "CallAudioFilename", "CallAudioFile") ,
                CallDuration = GetString(e, "CallDuration")
            };
        }

        private static string? GetString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var prop))
                return null;

            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.ToString(),
                JsonValueKind.True or JsonValueKind.False => prop.GetBoolean().ToString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => prop.ToString()
            };
        }

        private static string? GetStringAny(JsonElement obj, params string[] names)
        {
            foreach (var n in names)
            {
                var v = GetString(obj, n);
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return null;
        }

        private static string ExtractFirstUrlOrPath(string? raw)
        {
            var s = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            // Try href/src attributes first (Select2 and DataTables often return HTML snippets).
            var m = Regex.Match(s, @"(href|src)\s*=\s*['""](?<u>[^'""]+)['""]", RegexOptions.IgnoreCase);
            if (m.Success)
                return WebUtility.HtmlDecode(m.Groups["u"].Value).Trim();

            // Otherwise, pull the first http(s) URL if present.
            m = Regex.Match(s, @"https?://[^\s'""<>]+", RegexOptions.IgnoreCase);
            if (m.Success)
                return WebUtility.HtmlDecode(m.Value).Trim();

            // Strip any remaining tags and return the raw text/path.
            s = Regex.Replace(s, "<.*?>", string.Empty, RegexOptions.Singleline).Trim();
            return WebUtility.HtmlDecode(s).Trim();
        }

        private static DateTime ParseTimestamp(string? startTime, string? startTimeUtc)
        {
            var value = !string.IsNullOrWhiteSpace(startTimeUtc) ? startTimeUtc : startTime;
            if (string.IsNullOrWhiteSpace(value))
                return DateTime.Now;

            try
            {
                if (value.Contains("T", StringComparison.Ordinal))
                {
                    var dto = DateTimeOffset.Parse(value.Replace("Z", "+00:00"), null);
                    return dto.ToLocalTime().DateTime;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    var dto = DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToLocalTime();
                    return dto.DateTime;
                }
            }
            catch
            {
            }

            return DateTime.Now;
        }

        private static CallItem? ConvertRowToCallItem(CallRow r, string baseUrl)
        {
            try
            {
                var rawId = r.DT_RowId?.Trim();
                if (string.IsNullOrWhiteSpace(rawId))
                    rawId = Guid.NewGuid().ToString("N");

                var timestamp = ParseTimestamp(r.StartTime, r.StartTimeUTC);

                var talkgroup = !string.IsNullOrWhiteSpace(r.TargetLabel)
                    ? r.TargetLabel
                    : r.TargetID ?? string.Empty;

                var source = !string.IsNullOrWhiteSpace(r.SourceLabel)
                    ? r.SourceLabel
                    : r.SourceID ?? string.Empty;

                var site = !string.IsNullOrWhiteSpace(r.SiteLabel)
                    ? r.SiteLabel
                    : r.SiteID ?? string.Empty;

                // User preference: do not show the word "Unknown" in the Site column.
                if (!string.IsNullOrWhiteSpace(site) && site.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                    site = string.Empty;

                var voiceReceiver = r.VoiceReceiver?.Trim() ?? string.Empty;

                double durationSeconds = 0;
                if (!string.IsNullOrWhiteSpace(r.CallDuration) &&
                    double.TryParse(r.CallDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                {
                    durationSeconds = dur;
                }

                var transcript = (r.CallText ?? r.Transcription ?? r.Transcript ?? string.Empty).Trim();

                var audioUrl = string.Empty;
                var audioToken = ExtractFirstUrlOrPath(r.AudioFilename);
                if (!string.IsNullOrWhiteSpace(audioToken))
                {
                    if (audioToken.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        audioToken.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        audioUrl = audioToken;
                    }
                    else
                    {
                        audioUrl = $"{baseUrl}/{audioToken.TrimStart('/')}";
                    }
                }

                return new CallItem
                {
                    BackendId = rawId,
                    Timestamp = timestamp,
                    CallDurationSeconds = durationSeconds,
                    Talkgroup = talkgroup,
                    Source = source,
                    Site = site,
                    VoiceReceiver = voiceReceiver,
                    Transcription = transcript,
                    AudioUrl = audioUrl,
                    DebugInfo = string.Empty,
                    IsHistory = true
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
