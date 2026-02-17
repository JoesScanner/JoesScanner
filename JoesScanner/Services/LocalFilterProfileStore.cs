using JoesScanner.Models;
using System.Text.Json;

namespace JoesScanner.Services
{
    // Single shared profile store used by History, Archive, and Settings.
    // This intentionally does not read any older profile files.
    public sealed class LocalFilterProfileStore : IFilterProfileStore
    {
        private const string ProfilesFileName = "filter_profiles_v2.json";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

        private readonly SemaphoreSlim _gate = new(1, 1);

        private string FilePath => Path.Combine(FileSystem.AppDataDirectory, ProfilesFileName);

        public async Task<IReadOnlyList<FilterProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
        {
            var envelope = await ReadEnvelopeAsync(cancellationToken);
            return envelope.Profiles
                .Where(p => p != null)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<FilterProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return null;

            var envelope = await ReadEnvelopeAsync(cancellationToken);
            return envelope.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
        }

        public async Task SaveOrUpdateAsync(FilterProfile profile, CancellationToken cancellationToken = default)
        {
            if (profile == null)
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var envelope = await ReadEnvelopeInternal_NoLockAsync(cancellationToken);
                var list = envelope.Profiles ?? new List<FilterProfile>();

                var id = (profile.Id ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                    id = Guid.NewGuid().ToString("N");

                var now = DateTime.UtcNow;

                var existingIndex = list.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
                if (existingIndex >= 0)
                {
                    var existing = list[existingIndex];
                    var created = existing?.CreatedUtc ?? now;

                    list[existingIndex] = new FilterProfile
                    {
                        Id = id,
                        Name = (profile.Name ?? string.Empty).Trim(),
                        Filters = profile.Filters ?? new FilterProfileFilters(),
                        Rules = profile.Rules ?? new List<FilterRuleStateRecord>(),
                        CreatedUtc = created,
                        UpdatedUtc = now
                    };
                }
                else
                {
                    list.Add(new FilterProfile
                    {
                        Id = id,
                        Name = (profile.Name ?? string.Empty).Trim(),
                        Filters = profile.Filters ?? new FilterProfileFilters(),
                        Rules = profile.Rules ?? new List<FilterRuleStateRecord>(),
                        CreatedUtc = now,
                        UpdatedUtc = now
                    });
                }

                var updated = new FilterProfileStoreEnvelope
                {
                    SchemaVersion = 2,
                    Profiles = list
                        .Where(p => p != null)
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                await WriteEnvelopeInternal_NoLockAsync(updated, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var envelope = await ReadEnvelopeInternal_NoLockAsync(cancellationToken);
                var list = envelope.Profiles ?? new List<FilterProfile>();

                list.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));

                var updated = new FilterProfileStoreEnvelope
                {
                    SchemaVersion = 2,
                    Profiles = list
                        .Where(p => p != null)
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                await WriteEnvelopeInternal_NoLockAsync(updated, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RenameAsync(string profileId, string newName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return;

            var name = (newName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var envelope = await ReadEnvelopeInternal_NoLockAsync(cancellationToken);
                var list = envelope.Profiles ?? new List<FilterProfile>();

                var idx = list.FindIndex(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
                if (idx < 0)
                    return;

                var existing = list[idx];
                var now = DateTime.UtcNow;

                list[idx] = new FilterProfile
                {
                    Id = existing.Id,
                    Name = name,
                    Filters = existing.Filters ?? new FilterProfileFilters(),
                    Rules = existing.Rules ?? new List<FilterRuleStateRecord>(),
                    CreatedUtc = existing.CreatedUtc,
                    UpdatedUtc = now
                };

                var updated = new FilterProfileStoreEnvelope
                {
                    SchemaVersion = 2,
                    Profiles = list
                        .Where(p => p != null)
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                await WriteEnvelopeInternal_NoLockAsync(updated, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<FilterProfileStoreEnvelope> ExportAsync(CancellationToken cancellationToken = default)
        {
            var envelope = await ReadEnvelopeAsync(cancellationToken);
            return new FilterProfileStoreEnvelope
            {
                SchemaVersion = 2,
                Profiles = envelope.Profiles
                    .Where(p => p != null)
                    .Select(p => new FilterProfile
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Filters = p.Filters ?? new FilterProfileFilters(),
                        Rules = p.Rules ?? new List<FilterRuleStateRecord>(),
                        CreatedUtc = p.CreatedUtc,
                        UpdatedUtc = p.UpdatedUtc
                    })
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        public async Task ImportAsync(FilterProfileStoreEnvelope envelope, bool merge, CancellationToken cancellationToken = default)
        {
            if (envelope == null)
                return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var incoming = envelope.Profiles ?? new List<FilterProfile>();
                var current = await ReadEnvelopeInternal_NoLockAsync(cancellationToken);

                var list = merge
                    ? (current.Profiles ?? new List<FilterProfile>())
                    : new List<FilterProfile>();

                foreach (var p in incoming)
                {
                    if (p == null)
                        continue;

                    var id = (p.Id ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(id))
                        id = Guid.NewGuid().ToString("N");

                    var existingIndex = list.FindIndex(x => string.Equals(x.Id, id, StringComparison.Ordinal));
                    var now = DateTime.UtcNow;

                    var normalized = new FilterProfile
                    {
                        Id = id,
                        Name = (p.Name ?? string.Empty).Trim(),
                        Filters = p.Filters ?? new FilterProfileFilters(),
                        Rules = p.Rules ?? new List<FilterRuleStateRecord>(),
                        CreatedUtc = p.CreatedUtc == default ? now : p.CreatedUtc,
                        UpdatedUtc = now
                    };

                    if (existingIndex >= 0)
                        list[existingIndex] = normalized;
                    else
                        list.Add(normalized);
                }

                var updated = new FilterProfileStoreEnvelope
                {
                    SchemaVersion = 2,
                    Profiles = list
                        .Where(p => p != null)
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                await WriteEnvelopeInternal_NoLockAsync(updated, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<FilterProfileStoreEnvelope> ReadEnvelopeAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await ReadEnvelopeInternal_NoLockAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<FilterProfileStoreEnvelope> ReadEnvelopeInternal_NoLockAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new FilterProfileStoreEnvelope { SchemaVersion = 2, Profiles = new List<FilterProfile>() };

                var json = await File.ReadAllTextAsync(FilePath, cancellationToken);
                if (string.IsNullOrWhiteSpace(json))
                    return new FilterProfileStoreEnvelope { SchemaVersion = 2, Profiles = new List<FilterProfile>() };

                var parsed = JsonSerializer.Deserialize<FilterProfileStoreEnvelope>(json, JsonOptions);
                if (parsed == null)
                    return new FilterProfileStoreEnvelope { SchemaVersion = 2, Profiles = new List<FilterProfile>() };

                // Enforce our schema, ignore anything else.
                return new FilterProfileStoreEnvelope
                {
                    SchemaVersion = 2,
                    Profiles = (parsed.Profiles ?? new List<FilterProfile>())
                        .Where(p => p != null)
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                AppLog.Add($"Filter profiles read failed: {ex.Message} ({FilePath})");
                return new FilterProfileStoreEnvelope { SchemaVersion = 2, Profiles = new List<FilterProfile>() };
            }
        }

        private async Task WriteEnvelopeInternal_NoLockAsync(FilterProfileStoreEnvelope envelope, CancellationToken cancellationToken)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(envelope ?? new FilterProfileStoreEnvelope(), JsonOptions);
                await File.WriteAllTextAsync(FilePath, json, cancellationToken);
                AppLog.Add($"Filter profiles saved ({(envelope?.Profiles?.Count ?? 0)}) -> {FilePath}");
            }
            catch (Exception ex)
            {
                AppLog.Add($"Filter profiles write failed: {ex.Message} ({FilePath})");
                throw;
            }
        }
    }
}
