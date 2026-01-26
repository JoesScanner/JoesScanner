using JoesScanner.Models;

namespace JoesScanner.Services
{
    public interface IFilterProfileStore
    {
        Task<IReadOnlyList<FilterProfile>> GetProfilesAsync(string context, CancellationToken cancellationToken = default);
        Task<FilterProfile?> GetProfileAsync(string context, string profileId, CancellationToken cancellationToken = default);

        Task SaveOrUpdateAsync(FilterProfile profile, CancellationToken cancellationToken = default);
        Task DeleteAsync(string context, string profileId, CancellationToken cancellationToken = default);
        Task RenameAsync(string context, string profileId, string newName, CancellationToken cancellationToken = default);

        // Used for future API backup. Returns a stable, versioned payload.
        Task<FilterProfileStoreEnvelope> ExportAsync(string context, CancellationToken cancellationToken = default);
        Task ImportAsync(string context, FilterProfileStoreEnvelope envelope, bool merge, CancellationToken cancellationToken = default);
    }
}
