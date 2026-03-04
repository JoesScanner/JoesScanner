namespace JoesScanner.Services;

public interface IAuthObservedTriplesSyncService
{
    // Best-effort seed fetch from the Auth API. Returns null on any failure.
    Task<IReadOnlyList<ObservedTriple>?> TryFetchSeedAsync(string serverKey, CancellationToken cancellationToken);

    // Best-effort daily report of locally observed receiver/site/talkgroup associations.
    // When force is true, rate limiting is bypassed (still telemetry gated).
    Task TryReportAsync(string serverKey, bool force, CancellationToken cancellationToken);
}
