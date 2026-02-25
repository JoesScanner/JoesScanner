namespace JoesScanner.Services
{
    public sealed class HistoryLookupsCacheService : IHistoryLookupsCacheService
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
        private readonly ISettingsService _settingsService;
        private readonly ICallHistoryService _callHistoryService;
        private readonly IHistoryLookupsRepository _repo;

        // Prevent overlapping refreshes.
        private readonly SemaphoreSlim _lock = new(1, 1);

        public HistoryLookupsCacheService(ISettingsService settingsService, ICallHistoryService callHistoryService, IHistoryLookupsRepository repo)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _callHistoryService = callHistoryService ?? throw new ArgumentNullException(nameof(callHistoryService));
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
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
                    var (_, fetchedUtc) = await _repo.GetAsync(serverKey, cancellationToken);
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
    }
}
