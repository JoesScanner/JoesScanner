using JoesScanner.Models;
using System.Text.Json;

namespace JoesScanner.Services
{
    // Local device storage for Settings filter profiles.
    // File format is versioned and ready to be exported to a server later.
    public sealed class LocalSettingsFilterProfileStore : ISettingsFilterProfileStore
    {
        private const string FileName = "filter_profiles_settings.json";

        private readonly Lock _gate = new();
        private readonly JsonSerializerOptions _jsonOptions;

        public LocalSettingsFilterProfileStore()
        {
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };
        }

        public Task<IReadOnlyList<SettingsFilterProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
        {
            var env = LoadEnvelope();
            var list = env.Profiles
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult<IReadOnlyList<SettingsFilterProfile>>(list);
        }

        public Task<SettingsFilterProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Task.FromResult<SettingsFilterProfile?>(null);

            var env = LoadEnvelope();
            var p = env.Profiles.FirstOrDefault(x => string.Equals(x.Id, profileId, StringComparison.Ordinal));
            return Task.FromResult(p);
        }

        public Task SaveOrUpdateAsync(SettingsFilterProfile profile, CancellationToken cancellationToken = default)
        {
            if (profile == null)
                return Task.CompletedTask;

            lock (_gate)
            {
                var env = LoadEnvelope_NoLock();

                if (string.IsNullOrWhiteSpace(profile.Id))
                    profile.Id = Guid.NewGuid().ToString("N");

                profile.Name = (profile.Name ?? string.Empty).Trim();
                profile.UpdatedUtc = DateTime.UtcNow;

                var existingIndex = env.Profiles.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.Ordinal));
                if (existingIndex >= 0)
                {
                    env.Profiles[existingIndex] = profile;
                }
                else
                {
                    // If another profile has the same name, replace it.
                    var nameIndex = env.Profiles.FindIndex(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
                    if (nameIndex >= 0)
                        env.Profiles[nameIndex] = profile;
                    else
                        env.Profiles.Add(profile);
                }

                SaveEnvelope_NoLock(env);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Task.CompletedTask;

            lock (_gate)
            {
                var env = LoadEnvelope_NoLock();
                env.Profiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
                SaveEnvelope_NoLock(env);
            }

            return Task.CompletedTask;
        }

        public Task RenameAsync(string profileId, string newName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Task.CompletedTask;

            newName = (newName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName))
                return Task.CompletedTask;

            lock (_gate)
            {
                var env = LoadEnvelope_NoLock();
                var p = env.Profiles.FirstOrDefault(x => string.Equals(x.Id, profileId, StringComparison.Ordinal));
                if (p == null)
                    return Task.CompletedTask;

                p.Name = newName;
                p.UpdatedUtc = DateTime.UtcNow;

                SaveEnvelope_NoLock(env);
            }

            return Task.CompletedTask;
        }

        public Task<SettingsFilterProfileStoreEnvelope> ExportAsync(CancellationToken cancellationToken = default)
        {
            var env = LoadEnvelope();
            env.ExportedUtc = DateTime.UtcNow;
            return Task.FromResult(env);
        }

        public Task ImportAsync(SettingsFilterProfileStoreEnvelope envelope, bool merge, CancellationToken cancellationToken = default)
        {
            if (envelope == null)
                return Task.CompletedTask;

            lock (_gate)
            {
                var existing = LoadEnvelope_NoLock();

                if (!merge)
                {
                    existing.Profiles = envelope.Profiles ?? new List<SettingsFilterProfile>();
                    SaveEnvelope_NoLock(existing);
                    return Task.CompletedTask;
                }

                foreach (var incoming in envelope.Profiles ?? new List<SettingsFilterProfile>())
                {
                    if (string.IsNullOrWhiteSpace(incoming.Id))
                        incoming.Id = Guid.NewGuid().ToString("N");

                    incoming.Name = (incoming.Name ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(incoming.Name))
                        continue;

                    var idx = existing.Profiles.FindIndex(p => string.Equals(p.Id, incoming.Id, StringComparison.Ordinal));
                    if (idx >= 0)
                    {
                        existing.Profiles[idx] = incoming;
                        continue;
                    }

                    var nameIdx = existing.Profiles.FindIndex(p => string.Equals(p.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
                    if (nameIdx >= 0)
                    {
                        existing.Profiles[nameIdx] = incoming;
                        continue;
                    }

                    existing.Profiles.Add(incoming);
                }

                SaveEnvelope_NoLock(existing);
            }

            return Task.CompletedTask;
        }

        private SettingsFilterProfileStoreEnvelope LoadEnvelope()
        {
            lock (_gate)
            {
                return LoadEnvelope_NoLock();
            }
        }

        private SettingsFilterProfileStoreEnvelope LoadEnvelope_NoLock()
        {
            try
            {
                var path = GetFilePath();
                if (!File.Exists(path))
                    return new SettingsFilterProfileStoreEnvelope();

                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new SettingsFilterProfileStoreEnvelope();

                var env = JsonSerializer.Deserialize<SettingsFilterProfileStoreEnvelope>(json, _jsonOptions);
                return env ?? new SettingsFilterProfileStoreEnvelope();
            }
            catch
            {
                return new SettingsFilterProfileStoreEnvelope();
            }
        }

        private void SaveEnvelope_NoLock(SettingsFilterProfileStoreEnvelope envelope)
        {
            try
            {
                var path = GetFilePath();
                var json = JsonSerializer.Serialize(envelope ?? new SettingsFilterProfileStoreEnvelope(), _jsonOptions);
                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }

        private static string GetFilePath() => Path.Combine(FileSystem.AppDataDirectory, FileName);
    }
}
