namespace JoesScanner.Services
{
    public interface IAuthLookupsSyncService
    {
        // Attempts to seed local lookup data for the given serverKey using the Auth API.
        // Returns null when no seed is available or when the call fails.
        Task<HistoryLookupData?> TryFetchSeedAsync(string serverKey, CancellationToken cancellationToken);

        // Attempts to report lookup data for the given serverKey to the Auth API.
        // This is best-effort and must never throw.
        Task TryReportAsync(string serverKey, HistoryLookupData data, CancellationToken cancellationToken, bool force = false);
    }
}
