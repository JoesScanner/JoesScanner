using System.Linq;

namespace JoesScanner.Services
{
    public sealed class HistoryLookupsCacheService : IHistoryLookupsCacheService
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
        private readonly ISettingsService _settingsService;
        private readonly ICallHistoryService _callHistoryService;
        private readonly IHistoryLookupsRepository _repo;
        private readonly IAuthLookupsSyncService _authLookupsSyncService;
        private readonly IAuthObservedTriplesSyncService _authObservedTriplesSyncService;

        // Prevent overlapping refreshes.
        private readonly SemaphoreSlim _lock = new(1, 1);

        public HistoryLookupsCacheService(
            ISettingsService settingsService,
            ICallHistoryService callHistoryService,
            IHistoryLookupsRepository repo,
            IAuthLookupsSyncService authLookupsSyncService,
            IAuthObservedTriplesSyncService authObservedTriplesSyncService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _callHistoryService = callHistoryService ?? throw new ArgumentNullException(nameof(callHistoryService));
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _authLookupsSyncService = authLookupsSyncService ?? throw new ArgumentNullException(nameof(authLookupsSyncService));
            _authObservedTriplesSyncService = authObservedTriplesSyncService ?? throw new ArgumentNullException(nameof(authObservedTriplesSyncService));
        }

        public async Task<HistoryLookupData?> GetCachedAsync(CancellationToken cancellationToken = default)
        {
            var (serverKey, legacyKey) = GetServerKeys(_settingsService.ServerUrl);

            AppLog.Add(() => $"History: GetCachedAsync keys. rawUrl='{_settingsService.ServerUrl}' serverKey='{serverKey}' legacyKey='{legacyKey}'");
            if (string.IsNullOrWhiteSpace(serverKey))
                return null;

            // First try normalized key (no default :443/:80). If missing, also try the legacy
            // key format that accidentally included explicit default ports.
            var (data, fetchedUtc) = await _repo.GetAsync(serverKey, cancellationToken);
            if (data == null && !string.IsNullOrWhiteSpace(legacyKey) && !string.Equals(serverKey, legacyKey, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Add(() => $"History: lookup cache miss on normalized key; trying legacy key. normalized={serverKey} legacy={legacyKey}");
                (data, fetchedUtc) = await _repo.GetAsync(legacyKey, cancellationToken);
            }
            if (data == null)
                return null;

            AppLog.Add(() => $"History: lookup cache hit. server={serverKey} fetchedUtc={(fetchedUtc?.ToString("O") ?? "") } receivers={data.Receivers?.Count ?? 0} sites={data.Sites?.Count ?? 0} talkgroups={data.Talkgroups?.Count ?? 0}");
            return data;
        }

        public async Task PreloadAsync(bool forceReload, string reason, CancellationToken cancellationToken = default)
        {
            var (serverKey, _) = GetServerKeys(_settingsService.ServerUrl);

            AppLog.Add(() => $"History: PreloadAsync keys. rawUrl='{_settingsService.ServerUrl}' serverKey='{serverKey}' forceReload={forceReload} reason={reason}");
            if (string.IsNullOrWhiteSpace(serverKey))
                return;

            try
            {
                await _lock.WaitAsync(cancellationToken);
                try
                {
                    var (cached, fetchedUtc) = await _repo.GetAsync(serverKey, cancellationToken);

                    // If we have no local lookup data yet, attempt to seed from the Auth API.
                    // This is best-effort and must not block connecting.
                    if (cached == null || (cached.Receivers?.Count ?? 0) == 0 || (cached.Sites?.Count ?? 0) == 0 || (cached.Talkgroups?.Count ?? 0) == 0)
                    {
                        try
                        {
                            // Prefer observed-triples seed because it includes reliable associations.
                            var observed = await _authObservedTriplesSyncService.TryFetchSeedAsync(serverKey, cancellationToken).ConfigureAwait(false);
                            if (observed != null && observed.Count > 0)
                            {
                                var seededFromObserved = BuildLookupDataFromObserved(observed);
                                await _repo.UpsertAsync(serverKey, seededFromObserved, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
                                cached = seededFromObserved;
                                fetchedUtc = DateTime.UtcNow;
                                AppLog.Add(() => $"History: seeded lookups from Auth Observed. server={serverKey} receivers={seededFromObserved.Receivers?.Count ?? 0} sites={seededFromObserved.Sites?.Count ?? 0} talkgroups={seededFromObserved.Talkgroups?.Count ?? 0}");
                            }

                            // Fall back to raw lookup catalogs seed.
                            var seeded = await _authLookupsSyncService.TryFetchSeedAsync(serverKey, cancellationToken).ConfigureAwait(false);
                            if (seeded != null)
                            {
                                await _repo.UpsertAsync(serverKey, seeded, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
                                cached = seeded;
                                fetchedUtc = DateTime.UtcNow;
                                AppLog.Add(() => $"History: seeded lookups from Auth API. server={serverKey} receivers={seeded.Receivers?.Count ?? 0} sites={seeded.Sites?.Count ?? 0} talkgroups={seeded.Talkgroups?.Count ?? 0}");
                            }
                        }
                        catch
                        {
                        }
                    }

                    var isStale = !fetchedUtc.HasValue || (DateTime.UtcNow - fetchedUtc.Value.ToUniversalTime()) > RefreshInterval;

                    if (!forceReload && !isStale)
                    {
                        AppLog.Add(() => $"History: lookup preload skipped (fresh). server={serverKey} reason={reason}");
                        return;
                    }

                    AppLog.Add(() => $"History: lookup preload start. server={serverKey} reason={reason} force={forceReload} stale={isStale}");

                    // Network fetch. This may fail if the server is down. That should not block app connection.
                    var data = await _callHistoryService.GetLookupDataAsync(currentFilters: null, cancellationToken);
                    await _repo.UpsertAsync(serverKey, data, DateTime.UtcNow, cancellationToken);

                    // Best-effort daily report of lookup data back to the Auth API.
                    // This is telemetry-gated and silently skipped when disabled.
                    try
                    {
                        await _authLookupsSyncService.TryReportAsync(serverKey, data, cancellationToken, force: forceReload).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    // Best-effort daily report of observed receiver/site/talkgroup associations.
                    try
                    {
                        await _authObservedTriplesSyncService.TryReportAsync(serverKey, force: forceReload, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    AppLog.Add(() => $"History: lookup preload ok. server={serverKey} receivers={data.Receivers?.Count ?? 0} sites={data.Sites?.Count ?? 0} talkgroups={data.Talkgroups?.Count ?? 0}");
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"History: lookup preload failed. reason={reason} ex={ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string NormalizeServerKey(string? serverUrl)
        {
            return GetServerKeys(serverUrl).Normalized;
        }

        private static (string Normalized, string Legacy) GetServerKeys(string? serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                return (string.Empty, string.Empty);

            var raw = serverUrl.Trim().TrimEnd('/');

            try
            {
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                    return (raw, raw);

                // Normalized key: scheme://host[:port if non-default]
                var normalizedBuilder = new UriBuilder(uri)
                {
                    Path = string.Empty,
                    Query = string.Empty,
                    Fragment = string.Empty,
                    Port = uri.IsDefaultPort ? -1 : uri.Port
                };
                var normalized = normalizedBuilder.Uri.ToString().TrimEnd('/');

                // Legacy key: scheme://host:port (even for default ports)
                var legacyBuilder = new UriBuilder(uri)
                {
                    Path = string.Empty,
                    Query = string.Empty,
                    Fragment = string.Empty,
                    Port = uri.Port
                };
                var legacy = legacyBuilder.Uri.ToString().TrimEnd('/');

                return (normalized, legacy);
            }
            catch
            {
                return (raw, raw);
            }
        }

        private static HistoryLookupData BuildLookupDataFromObserved(IReadOnlyList<ObservedTriple> observed)
        {
            var receivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var talkgroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, List<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in observed)
            {
                if (row == null)
                    continue;

                var rxV = (row.ReceiverValue ?? string.Empty).Trim();
                var rxL = (row.ReceiverLabel ?? string.Empty).Trim();
                var siteV = (row.SiteValue ?? string.Empty).Trim();
                var siteL = (row.SiteLabel ?? string.Empty).Trim();
                var tgV = (row.TalkgroupValue ?? string.Empty).Trim();
                var tgL = (row.TalkgroupLabel ?? string.Empty).Trim();

                if (!string.IsNullOrWhiteSpace(rxV))
                    receivers[rxV] = string.IsNullOrWhiteSpace(rxL) ? rxV : rxL;
                if (!string.IsNullOrWhiteSpace(siteV))
                    sites[siteV] = string.IsNullOrWhiteSpace(siteL) ? siteV : siteL;
                if (!string.IsNullOrWhiteSpace(tgV))
                    talkgroups[tgV] = string.IsNullOrWhiteSpace(tgL) ? tgV : tgL;

                // Group talkgroups by site label to preserve association.
                var groupName = string.IsNullOrWhiteSpace(siteL) ? siteV : siteL;
                if (!string.IsNullOrWhiteSpace(groupName) && !string.IsNullOrWhiteSpace(tgV))
                {
                    if (!groups.TryGetValue(groupName, out var list))
                    {
                        list = new List<HistoryLookupItem>();
                        groups[groupName] = list;
                    }

                    if (!list.Any(x => string.Equals(x.Value, tgV, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new HistoryLookupItem { Value = tgV, Label = string.IsNullOrWhiteSpace(tgL) ? tgV : tgL });
                }
            }

            static List<HistoryLookupItem> ToSortedList(Dictionary<string, string> map)
            {
                var list = new List<HistoryLookupItem>(map.Count);
                foreach (var kv in map)
                    list.Add(new HistoryLookupItem { Value = kv.Key, Label = kv.Value });
                list.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
                return list;
            }

            var finalGroups = new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in groups)
            {
                kv.Value.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
                finalGroups[kv.Key] = kv.Value;
            }

            return new HistoryLookupData
            {
                Receivers = ToSortedList(receivers),
                Sites = ToSortedList(sites),
                Talkgroups = ToSortedList(talkgroups),
                TalkgroupGroups = finalGroups
            };
        }
    }
}
