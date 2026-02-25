using JoesScanner.Data;

namespace JoesScanner.Services;

public interface ILocalCallsRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task UpsertCallsAsync(string serverKey, IReadOnlyList<DbCallRecord> calls, CancellationToken cancellationToken = default);

    Task MarkTranscriptionUpdateNotifiedAsync(string serverKey, string backendId, CancellationToken cancellationToken = default);


    Task<string?> GetLookupCacheAsync(string serverKey, string cacheKey, CancellationToken cancellationToken = default);

    Task UpsertLookupCacheAsync(string serverKey, string cacheKey, DateTimeOffset fetchedAt, string jsonPayload, CancellationToken cancellationToken = default);

}
