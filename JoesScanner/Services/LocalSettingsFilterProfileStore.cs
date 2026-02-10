using JoesScanner.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace JoesScanner.Services
{
    // Uses SharedFilterProfilesStore so the same profile list is available on Settings, History, and Archive.
    public sealed class LocalSettingsFilterProfileStore : ISettingsFilterProfileStore
    {
        private readonly Lock _gate = new();

        public Task<IReadOnlyList<SettingsFilterProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                var shared = SharedFilterProfilesStore.LoadOrCreateMigrated();

                var list = shared.Profiles
                    .Select(p => new SettingsFilterProfile
                    {
                        Id = p.Id,
                        Name = p.Name,
                        UpdatedUtc = p.UpdatedUtc,
                        Rules = p.SettingsRules ?? new List<FilterRuleStateRecord>()
                    })
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Task.FromResult<IReadOnlyList<SettingsFilterProfile>>(list);
            }
        }

        public Task<SettingsFilterProfile?> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                var shared = SharedFilterProfilesStore.LoadOrCreateMigrated();
                var rec = shared.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
                if (rec is null)
                    return Task.FromResult<SettingsFilterProfile?>(null);

                return Task.FromResult<SettingsFilterProfile?>(new SettingsFilterProfile
                {
                    Id = rec.Id,
                    Name = rec.Name,
                    UpdatedUtc = rec.UpdatedUtc,
                    Rules = rec.SettingsRules ?? new List<FilterRuleStateRecord>()
                });
            }
        }

        public Task SaveOrUpdateAsync(SettingsFilterProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (profile is null)
                throw new ArgumentNullException(nameof(profile));

            lock (_gate)
            {
                var shared = SharedFilterProfilesStore.LoadOrCreateMigrated();
                var list = shared.Profiles.ToList();

                var name = (profile.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                    throw new InvalidOperationException("Profile name is required.");

                var existing = list.FirstOrDefault(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
                               ?? list.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

                var now = DateTime.UtcNow;

                SharedProfileRecord updated;
                if (existing is null)
                {
                    updated = new SharedProfileRecord
                    {
                        Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id,
                        Name = name,
                        UpdatedUtc = now,
                        SettingsRules = profile.Rules ?? new List<FilterRuleStateRecord>(),
                        HistoryFilters = new FilterProfileFilters(),
                        ArchiveFilters = new FilterProfileFilters()
                    };
                    list.Add(updated);
                }
                else
                {
                    updated = new SharedProfileRecord
                    {
                        Id = string.IsNullOrWhiteSpace(profile.Id) ? existing.Id : profile.Id,
                        Name = name,
                        UpdatedUtc = now,
                        SettingsRules = profile.Rules ?? new List<FilterRuleStateRecord>(),
                        HistoryFilters = existing.HistoryFilters,
                        ArchiveFilters = existing.ArchiveFilters
                    };

                    list.RemoveAll(p => string.Equals(p.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
                    list.Add(updated);
                }

                SharedFilterProfilesStore.Save(new SharedEnvelope
                {
                    SchemaVersion = shared.SchemaVersion,
                    UpdatedUtc = now,
                    Profiles = list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()
                });

                return Task.CompletedTask;
            }
        }

        public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                var shared = SharedFilterProfilesStore.LoadOrCreateMigrated();
                var list = shared.Profiles.ToList();
                list.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));

                SharedFilterProfilesStore.Save(new SharedEnvelope
                {
                    SchemaVersion = shared.SchemaVersion,
                    UpdatedUtc = DateTime.UtcNow,
                    Profiles = list
                });

                return Task.CompletedTask;
            }
        }

        public Task RenameAsync(string profileId, string newName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                var shared = SharedFilterProfilesStore.LoadOrCreateMigrated();
                var list = shared.Profiles.ToList();
                var rec = list.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
                if (rec is null)
                    return Task.CompletedTask;

                var name = (newName ?? string.Empty).Trim();
                if (name.Length == 0)
                    return Task.CompletedTask;

                var now = DateTime.UtcNow;
                var updated = new SharedProfileRecord
                {
                    Id = rec.Id,
                    Name = name,
                    UpdatedUtc = now,
                    SettingsRules = rec.SettingsRules,
                    HistoryFilters = rec.HistoryFilters,
                    ArchiveFilters = rec.ArchiveFilters
                };

                list.RemoveAll(p => string.Equals(p.Id, rec.Id, StringComparison.OrdinalIgnoreCase));
                list.Add(updated);

                SharedFilterProfilesStore.Save(new SharedEnvelope
                {
                    SchemaVersion = shared.SchemaVersion,
                    UpdatedUtc = now,
                    Profiles = list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()
                });

                return Task.CompletedTask;
            }
        }

        public Task<SettingsFilterProfileStoreEnvelope> ExportAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                var shared = SharedFilterProfilesStore.LoadOrCreateMigrated();
                var profiles = shared.Profiles
                    .Select(p => new SettingsFilterProfile
                    {
                        Id = p.Id,
                        Name = p.Name,
                        UpdatedUtc = p.UpdatedUtc,
                        Rules = p.SettingsRules ?? new List<FilterRuleStateRecord>()
                    })
                    .ToList();

                return Task.FromResult(new SettingsFilterProfileStoreEnvelope
                {
                    SchemaVersion = 1,
                    Context = "settings",
                    ExportedUtc = DateTime.UtcNow,
                    Profiles = profiles
                });
            }
        }

        public Task ImportAsync(SettingsFilterProfileStoreEnvelope envelope, bool merge, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (envelope is null)
                throw new ArgumentNullException(nameof(envelope));

            lock (_gate)
            {
                var shared = SharedFilterProfilesStore.LoadOrCreateMigrated();
                var list = merge ? shared.Profiles.ToList() : new List<SharedProfileRecord>();
                var now = DateTime.UtcNow;

                foreach (var p in envelope.Profiles ?? new List<SettingsFilterProfile>())
                {
                    var name = (p.Name ?? string.Empty).Trim();
                    if (name.Length == 0)
                        continue;

                    var existing = list.FirstOrDefault(x => string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase))
                                   ?? list.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

                    if (existing is null)
                    {
                        list.Add(new SharedProfileRecord
                        {
                            Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id,
                            Name = name,
                            UpdatedUtc = now,
                            SettingsRules = p.Rules ?? new List<FilterRuleStateRecord>(),
                            HistoryFilters = new FilterProfileFilters(),
                            ArchiveFilters = new FilterProfileFilters()
                        });
                    }
                    else
                    {
                        list.RemoveAll(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
                        list.Add(new SharedProfileRecord
                        {
                            Id = string.IsNullOrWhiteSpace(p.Id) ? existing.Id : p.Id,
                            Name = name,
                            UpdatedUtc = now,
                            SettingsRules = p.Rules ?? new List<FilterRuleStateRecord>(),
                            HistoryFilters = existing.HistoryFilters,
                            ArchiveFilters = existing.ArchiveFilters
                        });
                    }
                }

                SharedFilterProfilesStore.Save(new SharedEnvelope
                {
                    SchemaVersion = shared.SchemaVersion,
                    UpdatedUtc = now,
                    Profiles = list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()
                });

                return Task.CompletedTask;
            }
        }
    }
}
