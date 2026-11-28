using System.Linq;
using JoesScanner.Helpers;

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
            var selected = CallHistoryService.SelectedSavedRecorder(_settingsService);
            if (selected == null)
                return null;
            var access = CallHistoryService.CaptureRecorderAccess(_settingsService, selected);
            using var lifetime = access.Link(cancellationToken);
            var (data, _) = await _repo.GetAsync(access.LookupCacheKey, lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            if (access.AccountVersion is { } version && _settingsService.ReadAccountSession().AccountVersion != version)
                return null;
            return data == null ? null : CallHistoryService.ScopeLookups(data, access);
        }

        public async Task PreloadAsync(bool forceReload, string reason, CancellationToken cancellationToken = default)
        {
            try
            {
                var selected = CallHistoryService.SelectedSavedRecorder(_settingsService);
                if (selected == null)
                    return;
                var access = CallHistoryService.CaptureRecorderAccess(_settingsService, selected);
                using var lifetime = access.Link(cancellationToken);
                cancellationToken = lifetime.Token;
                var serverKey = selected.Endpoint;
                var sharedCatalog = access.AllowsSharedCatalog;
                await _lock.WaitAsync(cancellationToken);
                try
                {
                    var (cached, fetchedUtc) = await _repo.GetAsync(access.LookupCacheKey, cancellationToken);

                    // If we have no local lookup data yet, attempt to seed only from the lookup catalog API.
                    // Discovery sync is now handled separately by the observed-triples pipeline and should not
                    // overwrite or masquerade as lookup catalog state.
                    if (sharedCatalog && (cached == null || (cached.Receivers?.Count ?? 0) == 0 || (cached.Sites?.Count ?? 0) == 0 || (cached.Talkgroups?.Count ?? 0) == 0))
                    {
                        try
                        {
                            var accountSession = WordPressSession.Capture(_settingsService);
                            var identity = DirectoryRecorderPolicy.For(_settingsService).CaptureHostedAccount(_settingsService, accountSession.AccountVersion);
                            var seeded = await _authLookupsSyncService.TryFetchSeedAsync(serverKey, cancellationToken).ConfigureAwait(false);
                            if (seeded != null && accountSession.IsCurrent)
                            {
                                await _repo.UpsertAsync(access.LookupCacheKey, seeded, DateTime.UtcNow, cancellationToken,
                                    commit => access.TryPublish(identity, commit)).ConfigureAwait(false);
                                if (!accountSession.IsCurrent)
                                    return;
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
                    var data = await _callHistoryService.GetRecorderLookupsAsync(selected, refresh: true, cancellationToken);
                    if (data.Access != access || !sharedCatalog || access.Retired)
                        return;

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
                    // IMPORTANT:
                    // Manual talkgroup sync performs a dedicated observed-triples exchange and applies the
                    // authoritative list to the UI. Avoid double-posting observed rows during that flow.
                    if (!string.Equals(reason, "manual_filters_sync", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            await _authObservedTriplesSyncService.TryReportAsync(access, force: forceReload, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
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
                AppLog.Add(() => $"History: lookup preload failed. reason={reason} ex={DiagnosticRedaction.Describe(ex)}");
            }
        }

    }
}
