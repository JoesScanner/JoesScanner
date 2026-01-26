using JoesScanner.Models;

namespace JoesScanner.Services
{
    public interface ISettingsFilterProfileStore
    {
        Task<IReadOnlyList<SettingsFilterProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);
        Task<SettingsFilterProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default);

        Task SaveOrUpdateAsync(SettingsFilterProfile profile, CancellationToken cancellationToken = default);
        Task DeleteAsync(string profileId, CancellationToken cancellationToken = default);
        Task RenameAsync(string profileId, string newName, CancellationToken cancellationToken = default);

        // Used for future API backup. Returns a stable, versioned payload.
        Task<SettingsFilterProfileStoreEnvelope> ExportAsync(CancellationToken cancellationToken = default);
        Task ImportAsync(SettingsFilterProfileStoreEnvelope envelope, bool merge, CancellationToken cancellationToken = default);
    }
}
