using JoesScanner.Models;

namespace JoesScanner.Services
{
    public interface IFilterProfileStore
    {
        Task<IReadOnlyList<FilterProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
        Task<FilterProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default);

        Task SaveOrUpdateAsync(FilterProfile profile, CancellationToken cancellationToken = default);
        Task DeleteAsync(string profileId, CancellationToken cancellationToken = default);
        Task RenameAsync(string profileId, string newName, CancellationToken cancellationToken = default);

        // Used for future API backup. Returns a stable, versioned payload.
        Task<FilterProfileStoreEnvelope> ExportAsync(CancellationToken cancellationToken = default);
        Task ImportAsync(FilterProfileStoreEnvelope envelope, bool merge, CancellationToken cancellationToken = default);
    }
}
