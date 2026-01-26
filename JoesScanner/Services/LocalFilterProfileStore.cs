using JoesScanner.Models;
using System.Text.Json;

namespace JoesScanner.Services
{
    // Local, device-only persistence for filter profiles.
    // Implementation is intentionally versioned and envelope-based so it can be backed up to the WordPress API later.
    public sealed class LocalFilterProfileStore : IFilterProfileStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public async Task<IReadOnlyList<FilterProfile>> GetProfilesAsync(string context, CancellationToken cancellationToken = default)
        {
            var env = await LoadAsync(context, cancellationToken);
            return env.Profiles
                .OrderByDescending(p => p.UpdatedUtc)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<FilterProfile?> GetProfileAsync(string context, string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return null;

            var env = await LoadAsync(context, cancellationToken);
            return env.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
        }

        public async Task SaveOrUpdateAsync(FilterProfile profile, CancellationToken cancellationToken = default)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            if (string.IsNullOrWhiteSpace(profile.Context))
                throw new ArgumentException("Profile context is required", nameof(profile));

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var env = await LoadUnsafeAsync(profile.Context, cancellationToken);
                var list = env.Profiles;

                var now = DateTime.UtcNow;
                var id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id;

                var existingIndex = list.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                if (existingIndex >= 0)
                {
                    var existing = list[existingIndex];
                    list[existingIndex] = new FilterProfile
                    {
                        Id = id,
                        Name = profile.Name,
                        Context = profile.Context,
                        Filters = profile.Filters,
                        CreatedUtc = existing.CreatedUtc,
                        UpdatedUtc = now
                    };
                }
                else
                {
                    list.Add(new FilterProfile
                    {
                        Id = id,
                        Name = profile.Name,
                        Context = profile.Context,
                        Filters = profile.Filters,
                        CreatedUtc = now,
                        UpdatedUtc = now
                    });
                }

                await SaveUnsafeAsync(profile.Context, env, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task DeleteAsync(string context, string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var env = await LoadUnsafeAsync(context, cancellationToken);
                env.Profiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
                await SaveUnsafeAsync(context, env, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RenameAsync(string context, string profileId, string newName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return;

            newName = (newName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName))
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var env = await LoadUnsafeAsync(context, cancellationToken);
                var index = env.Profiles.FindIndex(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
                if (index < 0)
                    return;

                var existing = env.Profiles[index];
                env.Profiles[index] = new FilterProfile
                {
                    Id = existing.Id,
                    Name = newName,
                    Context = existing.Context,
                    Filters = existing.Filters,
                    CreatedUtc = existing.CreatedUtc,
                    UpdatedUtc = DateTime.UtcNow
                };

                await SaveUnsafeAsync(context, env, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<FilterProfileStoreEnvelope> ExportAsync(string context, CancellationToken cancellationToken = default)
        {
            return await LoadAsync(context, cancellationToken);
        }

        public async Task ImportAsync(string context, FilterProfileStoreEnvelope envelope, bool merge, CancellationToken cancellationToken = default)
        {
            if (envelope == null)
                throw new ArgumentNullException(nameof(envelope));

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!merge)
                {
                    await SaveUnsafeAsync(context, NormalizeEnvelope(context, envelope), cancellationToken);
                    return;
                }

                var current = await LoadUnsafeAsync(context, cancellationToken);
                var incoming = NormalizeEnvelope(context, envelope);

                foreach (var p in incoming.Profiles)
                {
                    var existingIndex = current.Profiles.FindIndex(x => string.Equals(x.Id, p.Id, StringComparison.Ordinal));
                    if (existingIndex >= 0)
                    {
                        var existing = current.Profiles[existingIndex];
                        var updated = p.UpdatedUtc > existing.UpdatedUtc ? p : existing;
                        current.Profiles[existingIndex] = updated;
                    }
                    else
                    {
                        current.Profiles.Add(p);
                    }
                }

                await SaveUnsafeAsync(context, current, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private static FilterProfileStoreEnvelope NormalizeEnvelope(string context, FilterProfileStoreEnvelope envelope)
        {
            var env = new FilterProfileStoreEnvelope
            {
                SchemaVersion = envelope.SchemaVersion <= 0 ? 1 : envelope.SchemaVersion,
                Profiles = new List<FilterProfile>()
            };

            foreach (var p in envelope.Profiles ?? new List<FilterProfile>())
            {
                if (p == null)
                    continue;

                var id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id;
                var name = (p.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                env.Profiles.Add(new FilterProfile
                {
                    Id = id,
                    Name = name,
                    Context = context,
                    Filters = p.Filters ?? new FilterProfileFilters(),
                    CreatedUtc = p.CreatedUtc == default ? DateTime.UtcNow : p.CreatedUtc,
                    UpdatedUtc = p.UpdatedUtc == default ? DateTime.UtcNow : p.UpdatedUtc
                });
            }

            return env;
        }

        private async Task<FilterProfileStoreEnvelope> LoadAsync(string context, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await LoadUnsafeAsync(context, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<FilterProfileStoreEnvelope> LoadUnsafeAsync(string context, CancellationToken cancellationToken)
        {
            try
            {
                var path = GetPath(context);
                if (!File.Exists(path))
                    return new FilterProfileStoreEnvelope();

                var json = await File.ReadAllTextAsync(path, cancellationToken);
                if (string.IsNullOrWhiteSpace(json))
                    return new FilterProfileStoreEnvelope();

                var env = JsonSerializer.Deserialize<FilterProfileStoreEnvelope>(json, JsonOptions);
                return NormalizeEnvelope(context, env ?? new FilterProfileStoreEnvelope());
            }
            catch
            {
                return new FilterProfileStoreEnvelope();
            }
        }

        private async Task SaveUnsafeAsync(string context, FilterProfileStoreEnvelope envelope, CancellationToken cancellationToken)
        {
            var path = GetPath(context);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            await File.WriteAllTextAsync(tmp, json, cancellationToken);

            if (File.Exists(path))
                File.Delete(path);

            File.Move(tmp, path);
        }

        private static string GetPath(string context)
        {
            var safe = (context ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(safe))
                safe = "default";

            return Path.Combine(FileSystem.AppDataDirectory, $"filter_profiles_{safe.ToLowerInvariant()}.json");
        }
    }
}
