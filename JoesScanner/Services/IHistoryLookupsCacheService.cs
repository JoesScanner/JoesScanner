namespace JoesScanner.Services
{
    public interface IHistoryLookupsCacheService
    {
        // Returns cached lookup data for the current server, if available.
        Task<HistoryLookupData?> GetCachedAsync(CancellationToken cancellationToken = default);

        // Best-effort: refreshes cached lookups for the current server if stale or missing.
        // Never throws.
        Task PreloadAsync(bool forceReload, string reason, CancellationToken cancellationToken = default);
    }
}
