namespace JoesScanner.Services;

public interface IAuthObservedTriplesSyncService
{
    // Best-effort seed fetch from the Auth API. Returns null on any failure.
    Task<IReadOnlyList<ObservedTriple>?> TryFetchSeedAsync(string serverKey, CancellationToken cancellationToken);

    // Best-effort daily report of locally observed receiver/site/talkgroup associations.
    // When force is true, rate limiting is bypassed and manual sync is allowed even when telemetry is disabled.
    Task TryReportAsync(string serverKey, bool force, CancellationToken cancellationToken);

    // User-initiated sync exchange: report local observed triples and return the authoritative
    // server-scoped observed list in the same call (no separate GET required).
    // Returns null on failure.
    Task<IReadOnlyList<ObservedTriple>?> TrySyncExchangeAsync(string serverKey, bool force, CancellationToken cancellationToken);

    // Returns locally observed receiver/site/talkgroup associations from the calls table.
    // Used to make manual sync pruning conservative when lookup catalogs are incomplete.
    Task<IReadOnlyList<ObservedTriple>> GetLocalObservedAsync(string serverKey, DateTime sinceUtc, CancellationToken cancellationToken);
}
