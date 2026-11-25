using JoesScanner.Models;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Linq;
using JoesScanner.Data;
using System.Threading;
using System.Net.Http;
using JoesScanner.Helpers;

namespace JoesScanner.Services
{
    // History lookups and searches against a Trunking Recorder server.
    // Lookup lists are fetched from dedicated JSON endpoints:
    // receiversjson, sitesjson, talkgroupsjson.
    public sealed partial class CallHistoryService : ICallHistoryService
    {
        private readonly ISettingsService _settingsService;
        private readonly ILocalCallsRepository _localCallsRepository;
        private readonly IHistoryLookupsRepository _lookupsRepository;
        private readonly ISubscriptionService _subscriptionService;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly TimeProvider _clock;

        private int _draw;

        
        private static readonly SemaphoreSlim _persistSemaphore = new SemaphoreSlim(1, 1);
        public CallHistoryService(
            ISettingsService settingsService,
            ILocalCallsRepository localCallsRepository,
            IHistoryLookupsRepository lookupsRepository,
            ISubscriptionService subscriptionService)
            : this(settingsService, localCallsRepository, lookupsRepository, subscriptionService, null, TimeProvider.System)
        {
        }

        internal CallHistoryService(ISettingsService settingsService, ILocalCallsRepository localCallsRepository,
            IHistoryLookupsRepository lookupsRepository, ISubscriptionService subscriptionService, HttpClient? httpClient,
            TimeProvider clock)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _localCallsRepository = localCallsRepository ?? throw new ArgumentNullException(nameof(localCallsRepository));
            _lookupsRepository = lookupsRepository ?? throw new ArgumentNullException(nameof(lookupsRepository));
            _subscriptionService = subscriptionService;
            _clock = clock;

            // Important: some mobile stacks can behave poorly with Brotli auto-decompression.
            // We explicitly keep this to gzip/deflate to avoid hangs on certain Android builds.
            _httpClient = httpClient ?? ServerAuthenticationHandler.CreateClient(_settingsService, new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate
            });
            _httpClient.Timeout = TimeSpan.FromSeconds(12);
            _httpClient.DefaultRequestVersion = HttpVersion.Version11;
            _httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            _httpClient.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            _httpClient.DefaultRequestHeaders.Add("DNT", "1");
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");

            _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public async Task<HistoryLookupData> GetLookupDataAsync(HistorySearchFilters? currentFilters,
            CancellationToken cancellationToken = default)
        {
            var endpoint = NormalizeBaseUrl(_settingsService.ServerUrl);
            if (endpoint.Length == 0)
                return new HistoryLookupData();

            var data = await ReadLookupsAsync(endpoint, null, cancellationToken).ConfigureAwait(false);
            await SaveLookupCacheAsync(ServerKeyHelper.Normalize(endpoint), data, cancellationToken).ConfigureAwait(false);
            return data;
        }

        public async Task<HistoryLookupData> GetRecorderLookupsAsync(HistoryRecorder recorder, bool refresh,
            CancellationToken cancellationToken = default)
        {
            var access = ReadRecorderAccess(recorder);
            var key = recorder.LookupCacheKey(access.Account);
            HistoryLookupData? cached = null;
            DateTime? fetched = null;
            try { (cached, fetched) = await _lookupsRepository.GetAsync(key, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Add(() => $"History lookup cache read failed ({ex.GetType().Name}).");
            }

            ValidateRecorderAccess(access, cancellationToken);
            if (!refresh && cached != null && fetched >= _clock.GetUtcNow().UtcDateTime.AddHours(-6))
                return ScopeLookups(cached, recorder.Id);

            var latest = await ReadLookupsAsync(recorder.Endpoint, recorder.RequestScope, cancellationToken).ConfigureAwait(false);
            ValidateRecorderAccess(access, cancellationToken);
            var hasLookups = latest.Receivers.Any(item => item.Value.Length != 0) ||
                latest.Sites.Any(item => item.Value.Length != 0) || latest.Talkgroups.Any(item => item.Value.Length != 0);
            if (hasLookups)
                await SaveLookupCacheAsync(key, latest, cancellationToken).ConfigureAwait(false);

            ValidateRecorderAccess(access, cancellationToken);
            return ScopeLookups(hasLookups || cached == null ? latest : cached, recorder.Id);
        }

        private async Task<HistoryLookupData> ReadLookupsAsync(string endpoint, RecorderRequestScope? scope, CancellationToken token)
        {
            var receivers = await FetchLookupAsync(new Uri(endpoint + "/receiversjson"), scope, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var sites = await FetchLookupAsync(new Uri(endpoint + "/sitesjson"), scope, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var (talkgroups, groups) = await FetchTalkgroupsAsync(new Uri(endpoint + "/talkgroupsjson"), scope, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new HistoryLookupData
            {
                Receivers = PrependAll(receivers),
                Sites = PrependAll(sites),
                Talkgroups = PrependAll(talkgroups),
                TalkgroupGroups = groups
            };
        }

        private async Task SaveLookupCacheAsync(string key, HistoryLookupData data, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            try { await _lookupsRepository.UpsertAsync(key, data, _clock.GetUtcNow().UtcDateTime, token).ConfigureAwait(false); }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                AppLog.Add(() => $"History lookup cache write failed ({ex.GetType().Name}).");
            }
        }

        private static HistoryLookupData ScopeLookups(HistoryLookupData data, Guid id)
        {
            HistoryLookupItem[] Scoped(IEnumerable<HistoryLookupItem> items) => items.Select(item => item with { RecorderId = id }).ToArray();
            return new HistoryLookupData
            {
                Receivers = Scoped(data.Receivers),
                Sites = Scoped(data.Sites),
                Talkgroups = Scoped(data.Talkgroups),
                TalkgroupGroups = data.TalkgroupGroups.ToDictionary(pair => pair.Key,
                    pair => (IReadOnlyList<HistoryLookupItem>)Scoped(pair.Value), StringComparer.OrdinalIgnoreCase)
            };
        }
        private async Task<CallsPage> FetchCallsPageAsync(HistorySearchSnapshot snapshot,
            RecordingPlaybackAccess.HistoryGrant grant, int start, int length, CancellationToken cancellationToken)
        {
            ValidateSearch(snapshot.Access, grant, cancellationToken);
            var recorder = snapshot.Access.Recorder;
            var dateStart = FormatSearchDate(snapshot.FromUtc);
            var dateEnd = FormatSearchDate(snapshot.UntilUtc);
            var draw = Interlocked.Increment(ref _draw);
            var payload = BuildDataTablesPayload(draw, start, length, snapshot.Filters, dateStart + "|" + dateEnd);
            using var request = CreateRecorderRequest(HttpMethod.Post, new Uri(recorder.Endpoint + "/calljson"), recorder.RequestScope);
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            request.Headers.ConnectionClose = true;
            request.Headers.Referrer = new Uri($"{recorder.Endpoint}/?DateStart={WebUtility.UrlEncode(dateStart)}&DateEnd={WebUtility.UrlEncode(dateEnd)}");

            // Match the recorder's web client, which posts JSON under the form content type.
            request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8,
                "application/x-www-form-urlencoded");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            ValidateSearch(snapshot.Access, grant, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new RecordingAccessException($"The recorder denied History access (HTTP {(int)response.StatusCode}).");
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
            ValidateSearch(snapshot.Access, grant, cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetPropertyIgnoreCase(root, "recordsFiltered", out var count) ||
                !int.TryParse(count.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) || total < 0 ||
                !TryGetPropertyIgnoreCase(root, "data", out var data) || data.ValueKind != JsonValueKind.Array ||
                TryGetPropertyIgnoreCase(root, "draw", out var echoedDraw) && echoedDraw.ToString() != draw.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("The recorder did not return a valid History page.");

            return new CallsPage(total, data.EnumerateArray().Select(ParseRow).ToArray());
        }

        private void QueueHistoryPersistence(string endpoint, IReadOnlyList<DbCallRecord> records)
        {
            if (records.Count == 0)
                return;

            _ = Task.Run(async () =>
            {
                using var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await _persistSemaphore.WaitAsync(gateTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try
                {
                    using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _localCallsRepository.UpsertCallsAsync(endpoint, records, writeTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Add(() => $"History cache write failed ({ex.GetType().Name}).");
                }
                finally { _persistSemaphore.Release(); }
            });
        }

        private static HttpRequestMessage CreateRecorderRequest(HttpMethod method, Uri uri, RecorderRequestScope? recorder)
        {
            var request = new HttpRequestMessage(method, uri);
            if (recorder is { } scope)
                request.Options.Set(ServerAuthenticationHandler.Recorder, scope);
            return request;
        }
        private static string NormalizeBaseUrl(string? url)
        {
            var baseUrl = (url ?? string.Empty).Trim();
            return baseUrl.TrimEnd('/');
        }

        private static IReadOnlyList<HistoryLookupItem> PrependAll(IReadOnlyList<HistoryLookupItem> items)
        {
            var list = items?.ToList() ?? new List<HistoryLookupItem>();

            if (list.Count == 0 || list[0].Value.Length != 0)
                list.Insert(0, new HistoryLookupItem { Label = "All", Value = string.Empty });

            return list;
        }

        private async Task<IReadOnlyList<HistoryLookupItem>> FetchLookupAsync(Uri uri, RecorderRequestScope? scope, CancellationToken cancellationToken)
        {
            try
            {
                using var request = CreateRecorderRequest(HttpMethod.Get, uri, scope);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Add(() => $"History: lookup failed. status={(int)response.StatusCode}");
                    return Array.Empty<HistoryLookupItem>();
                }

                if (string.IsNullOrWhiteSpace(body))
                    return Array.Empty<HistoryLookupItem>();

                var items = ParseLookupBody(body);

                try { AppLog.Add(() => $"History: lookup ok. url={uri} count={items.Count}"); } catch { }

                return items;
            }
            catch (Exception ex)
            {
            AppLog.Add(() => $"History: lookup threw. url={uri} ex={DiagnosticRedaction.Describe(ex)}");
                return Array.Empty<HistoryLookupItem>();
            }
        }

        private async Task<(IReadOnlyList<HistoryLookupItem> Talkgroups, Dictionary<string, IReadOnlyList<HistoryLookupItem>> Groups)> FetchTalkgroupsAsync(
            Uri uri, RecorderRequestScope? scope,
            CancellationToken cancellationToken)
        {
            try
            {
                using var request = CreateRecorderRequest(HttpMethod.Get, uri, scope);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Add(() => $"History: talkgroups lookup failed. status={(int)response.StatusCode}");
                    return (Array.Empty<HistoryLookupItem>(), new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase));
                }
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
            catch (Exception ex)
            {
            AppLog.Add(() => $"History: talkgroups lookup threw. url={uri} ex={DiagnosticRedaction.Describe(ex)}");
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
                    // Select2 style group wrappers: { text, id:null, children:[{id,text}...] }
                    if (el.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                    {
                        items.AddRange(ParseJsonArray(children));
                        continue;
                    }

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


        private static string EscapeUrlPathSegments(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
                return url.Replace(" ", "%20");

            var b = new UriBuilder(u);

            var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString);

            b.Path = "/" + string.Join("/", segments);

            return b.Uri.ToString();
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

        private static DataTablesPayload BuildDataTablesPayload(int draw, int start, int length, HistorySearchFilters filters, string dateRange)
        {
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

            columns[2].Search.Value = dateRange;
            // Trunking Recorder beta 3.7.6039.9360 reads a comma-separated
            // string of complete talkgroup lookup IDs in Target.search.value.
            columns[3].Search.Value = string.Join(',', filters.Talkgroups.Select(item => item.Value));
            columns[12].Search.Value = filters.Site?.Value ?? string.Empty;
            columns[20].Search.Value = filters.Receiver?.Value ?? string.Empty;

            return new DataTablesPayload
            {
                Draw = draw,
                Start = start,
                Length = length,
                Order = [new() { Column = 2, Dir = "desc" }, new() { Column = 1, Dir = "desc" }],
                Columns = columns.ToArray(),
                SmartSort = false
            };
        }

        private static DataTablesColumn Col(string? data, string name, bool orderable) =>
            new() { Data = data, Name = name, Orderable = orderable, Searchable = true, Search = new() };

        private sealed record CallsPage(int TotalMatches, IReadOnlyList<CallRow> Rows);
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

        private sealed class CallRow : TrunkingRecorderCallFields
        {
            public string? DT_RowId { get; set; }
            public string? StartTime { get; set; }
            public string? StartTimeUTC { get; set; }
            public string? Time { get; set; }
            public string? Date { get; set; }
            public string? AudioStartPos { get; set; }
            public string? CallText { get; set; }
            public string? Transcript { get; set; }
            public string? Transcription { get; set; }
            public string? AudioFilename { get; set; }
        }

        private static CallRow ParseRow(JsonElement e)
        {
            var row = new CallRow
            {
                DT_RowId = GetString(e, "DT_RowId"),
                StartTime = GetString(e, "StartTime"),
                StartTimeUTC = GetString(e, "StartTimeUTC"),
                Time = GetString(e, "Time"),
                Date = GetString(e, "Date"),
                AudioStartPos = GetString(e, "AudioStartPos"),
                CallText = GetString(e, "CallText"),
                Transcript = GetString(e, "Transcript"),
                Transcription = GetString(e, "Transcription"),
                AudioFilename = GetStringAny(e, "AudioFilename", "AudioFileName", "AudioFile", "Filename", "FileName", "CallAudioFilename", "CallAudioFile")
            };
            row.ReadCardFields(e, GetString);
            return row;
        }


        private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
        {
            // Fast path: exact match
            if (obj.TryGetProperty(name, out value))
                return true;

            // Slow path: case-insensitive match, needed for some TR servers that emit lowercase or mixed-case keys.
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static string? GetString(JsonElement obj, string name)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var prop))
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

        private static string? CapTextRaw(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var s = text.Trim();
            const int maxChars = 20000;
            if (s.Length > maxChars)
                s = s.Substring(0, maxChars);
            return s;
        }

        private static string? CanonicalTranscription(CallRow r)
        {
            // Phase 1 rule: Transcription > Transcript > CallText
            var s = (r.Transcription ?? r.Transcript ?? r.CallText ?? string.Empty);
            var sanitized = TranscriptionSanitizer.Sanitize(s);
            return CapTextRaw(sanitized);
        }

        private static DbCallRecord? MapToDbRecord(CallRow r, string serverKey)
        {
            var rawId = r.DT_RowId?.Trim();
            if (string.IsNullOrWhiteSpace(rawId))
                return null;

            int? lcn = null;
            if (!string.IsNullOrWhiteSpace(r.LCN) && int.TryParse(r.LCN, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lcnValue))
                lcn = lcnValue;

            double? frequency = null;
            if (!string.IsNullOrWhiteSpace(r.Frequency) && double.TryParse(r.Frequency, NumberStyles.Float, CultureInfo.InvariantCulture, out var freqValue))
                frequency = freqValue;

            double? audioStartPos = null;
            if (!string.IsNullOrWhiteSpace(r.AudioStartPos) && double.TryParse(r.AudioStartPos, NumberStyles.Float, CultureInfo.InvariantCulture, out var posValue))
                audioStartPos = posValue;

            double? durationSeconds = null;
            if (!string.IsNullOrWhiteSpace(r.CallDuration) && double.TryParse(r.CallDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                durationSeconds = dur;

            var startUtc = !string.IsNullOrWhiteSpace(r.StartTimeUTC) ? r.StartTimeUTC : r.StartTime;
            var now = DateTimeOffset.UtcNow;

            return new DbCallRecord
            {
                ServerKey = (serverKey ?? string.Empty).Trim().TrimEnd('/'),
                BackendId = rawId,
                StartTimeUtc = CapTextRaw(startUtc),
                TimeText = CapTextRaw(r.Time),
                DateText = CapTextRaw(r.Date),
                TargetId = CapTextRaw(r.TargetID),
                TargetLabel = CapTextRaw(r.TargetLabel),
                TargetTag = CapTextRaw(r.TargetTag),
                SourceId = CapTextRaw(r.SourceID),
                SourceLabel = CapTextRaw(r.SourceLabel),
                SourceTag = CapTextRaw(r.SourceTag),
                Lcn = lcn,
                Frequency = frequency,
                CallAudioType = CapTextRaw(r.CallAudioType),
                CallType = CapTextRaw(r.CallType),
                SystemId = CapTextRaw(r.SystemID),
                SystemLabel = CapTextRaw(r.SystemLabel),
                SystemType = CapTextRaw(r.SystemType),
                SiteId = CapTextRaw(r.SiteID),
                SiteLabel = CapTextRaw(r.SiteLabel),
                VoiceReceiver = CapTextRaw(r.VoiceReceiver),
                AudioFilename = RecordingStorageReference.ForCache(ExtractFirstUrlOrPath(r.AudioFilename)),
                AudioStartPos = audioStartPos,
                CallDurationSeconds = durationSeconds,
                CallText = CapTextRaw(r.CallText),
                Transcription = CanonicalTranscription(r),
                ReceivedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        private static CallItem? ConvertRowToCallItem(CallRow r, HistoryRecorderAccess access, DateTimeOffset recordedAt,
            int archiveOffset)
        {
            try
            {
                var baseUrl = access.Recorder.Endpoint;
                var metadata = r.ToCardMetadata(recordedAt.LocalDateTime, omitUnknownSite: true);

                var transcript = (r.CallText ?? r.Transcription ?? r.Transcript ?? string.Empty).Trim();

                var audioUrl = string.Empty;
                var audioToken = ExtractFirstUrlOrPath(r.AudioFilename);
                if (audioToken.StartsWith("//", StringComparison.Ordinal) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var server))
                    audioToken = server.Scheme + ":" + audioToken;
                if (!string.IsNullOrWhiteSpace(audioToken))
                {
                    try
                    {
                        if (audioToken.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            audioToken.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            // Ensure any spaces/special chars in TR-generated paths are properly escaped.
                            audioUrl = EscapeUrlPathSegments(audioToken);
                        }
                        else
                        {
                            var baseUri = new Uri(baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : (baseUrl + "/"));
                            audioUrl = EscapeUrlPathSegments(new Uri(baseUri, audioToken.TrimStart('/')).ToString());
                        }
                    }
                    catch
                    {
                        // Fallback to best-effort string concat.
                        if (audioToken.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            audioToken.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            audioUrl = audioToken;
                        else
                            audioUrl = $"{baseUrl}/{audioToken.TrimStart('/')}";
                    }
                }

                return new CallItem
                {
                    BackendId = r.DT_RowId!.Trim(),
                    ServerId = access.Recorder.Id,
                    RecordingServer = baseUrl,
                    RecordingTimeUtc = recordedAt,
                    HistoryArchiveOffset = archiveOffset,
                    RecordingAccess = new RecordingAccessContext(RecordingOrigin.History, access.Protected, access.Account),
                    Timestamp = metadata.Timestamp ?? DateTime.Now,
                    CallDurationSeconds = metadata.DurationSeconds ?? 0,
                    Talkgroup = metadata.Talkgroup ?? string.Empty,
                    Source = metadata.Radio ?? string.Empty,
                    Site = metadata.Site ?? string.Empty,
                    VoiceReceiver = metadata.Receiver ?? string.Empty,
                    CardMetadata = metadata,
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
