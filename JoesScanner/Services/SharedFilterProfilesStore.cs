using JoesScanner.Models;
using Microsoft.Maui.Storage;
using System.Text;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JoesScanner.Services
{
    // Shared, local-only profiles store used by Settings/History/Archive.
    // One shared JSON file so profiles appear the same across all three pages.
    internal static class SharedFilterProfilesStore
    {
        private const string SharedProfilesFileName = "shared_filter_profiles_v1.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // In-memory cache to keep UI stable across pages and avoid repeated disk reads.
        private static readonly Lock Gate = new();
        private static SharedEnvelope? _cached;
        private static DateTime _cachedFileWriteUtc;
        private static long _cachedFileSize;

        public static SharedEnvelope LoadOrCreateMigrated()
        {
            lock (Gate)
            {
                var path = GetSharedProfilesPath();
                var state = GetFileState(path);

                // IMPORTANT:
                // Some platforms have coarse file timestamp resolution (often 1 second).
                // Relying on "last write time" alone can cause us to miss an update that happens
                // within the same timestamp tick. Use (writeUtc + size) as the cache key.
                if (_cached != null && state.Exists && state.WriteUtc == _cachedFileWriteUtc && state.Size == _cachedFileSize)
                {
                    return Clone(_cached);
                }

                var loaded = TryLoadFromDisk(path);
                if (loaded != null)
                {
                    // Refresh file state after read (defensive).
                    var after = GetFileState(path);
                    _cached = loaded;
                    _cachedFileWriteUtc = after.WriteUtc;
                    _cachedFileSize = after.Size;
                    return Clone(loaded);
                }

                // No shared file yet: attempt to migrate from any legacy per-page stores.
                var migrated = MigrateFromLegacyFiles();
                Save(migrated);
                return Clone(migrated);
            }
        }

        public static void Save(SharedEnvelope env)
        {
            lock (Gate)
            {
                env.SchemaVersion = 1;
                env.UpdatedUtc = DateTime.UtcNow;

                // IMPORTANT:
                // Never let a disk write crash the app.
                // iOS can throw IO exceptions (transient locks, low storage, OS-level interruptions)
                // and we cannot allow Settings navigation to hard-fail because a local JSON save failed.
                var path = GetSharedProfilesPath();
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);

                    // Atomic-ish write: write temp then replace.
                    var tmp = path + ".tmp";
                    var json = JsonSerializer.Serialize(env, JsonOptions);
                    File.WriteAllText(tmp, json, Encoding.UTF8);

                    try
                    {
                        if (File.Exists(path))
                        {
                            try
                            {
                                File.Delete(path);
                            }
                            catch
                            {
                                // If delete fails, we'll try overwrite via copy.
                            }
                        }

                        try
                        {
                            File.Move(tmp, path);
                        }
                        catch
                        {
                            // Move can fail if destination exists or is locked.
                            // Fall back to copy-overwrite then delete temp.
                            File.Copy(tmp, path, overwrite: true);
                        }
                    }
                    finally
                    {
                        if (File.Exists(tmp))
                        {
                            try { File.Delete(tmp); } catch { }
                        }
                    }
                }
                catch
                {
                    // Swallow all IO/serialization exceptions. The in-memory cache will still be updated.
                }

                _cached = Clone(env);

                var state = GetFileState(path);
                _cachedFileWriteUtc = state.WriteUtc;
                _cachedFileSize = state.Size;
            }
        }

        private static string GetSharedProfilesPath()
        {
            // MAUI-safe persistent app data directory.
            var dir = FileSystem.AppDataDirectory;
            return Path.Combine(dir, SharedProfilesFileName);
        }

        private readonly record struct FileState(bool Exists, DateTime WriteUtc, long Size);

        private static FileState GetFileState(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new FileState(false, DateTime.MinValue, 0);

                var fi = new FileInfo(path);
                return new FileState(true, fi.LastWriteTimeUtc, fi.Length);
            }
            catch
            {
                return new FileState(false, DateTime.MinValue, 0);
            }
        }

        private static SharedEnvelope? TryLoadFromDisk(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                var json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                    return null;

                var env = JsonSerializer.Deserialize<SharedEnvelope>(json, JsonOptions);
                if (env == null)
                    return null;

                env.SchemaVersion = 1;
                env.Profiles ??= new List<SharedProfileRecord>();

                // Normalize / de-dupe by Id
                var map = new Dictionary<string, SharedProfileRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in env.Profiles)
                {
                    if (string.IsNullOrWhiteSpace(p.Id))
                        continue;

                    map[p.Id] = p;
                }

                env.Profiles = map.Values
                    .OrderByDescending(p => p.UpdatedUtc)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return env;
            }
            catch
            {
                return null;
            }
        }

        private static SharedEnvelope MigrateFromLegacyFiles()
        {
            // This migration is intentionally defensive:
            // if a legacy file can't be parsed, we ignore it rather than failing startup.
            var merged = new Dictionary<string, SharedProfileRecord>(StringComparer.OrdinalIgnoreCase);

            // Settings legacy (stores rules list)
            var settingsEnv = TryLoadSettingsEnvelope();
            if (settingsEnv?.Profiles != null)
            {
                foreach (var p in settingsEnv.Profiles)
                {
                    if (string.IsNullOrWhiteSpace(p.Id))
                        continue;

                    if (!merged.TryGetValue(p.Id, out var cur))
                    {
                        cur = new SharedProfileRecord
                        {
                            Id = p.Id,
                            Name = p.Name,
                            UpdatedUtc = p.UpdatedUtc,
                            SettingsRules = p.Rules?.ToList() ?? new List<FilterRuleStateRecord>(),
                            HistoryFilters = new FilterProfileFilters(),
                            ArchiveFilters = new FilterProfileFilters()
                        };
                    }
                    else
                    {
                        // prefer newer name/rules if newer
                        if (p.UpdatedUtc >= cur.UpdatedUtc)
                        {
                            cur = new SharedProfileRecord
                            {
                                Id = cur.Id,
                                Name = string.IsNullOrWhiteSpace(p.Name) ? cur.Name : p.Name,
                                UpdatedUtc = p.UpdatedUtc,
                                SettingsRules = p.Rules?.ToList() ?? cur.SettingsRules.ToList(),
                                HistoryFilters = cur.HistoryFilters,
                                ArchiveFilters = cur.ArchiveFilters
                            };
                        }
                    }

                    merged[p.Id] = cur;
                }
            }

            // History/Archive legacy (stores filter selections)
            var historyEnv = TryLoadProfileFiltersEnvelope("history");
            if (historyEnv != null)
                MergeFilterEnvelopeInto(merged, historyEnv, isHistory: true);

            var archiveEnv = TryLoadProfileFiltersEnvelope("archive");
            if (archiveEnv != null)
                MergeFilterEnvelopeInto(merged, archiveEnv, isHistory: false);

            return new SharedEnvelope
            {
                SchemaVersion = 1,
                UpdatedUtc = DateTime.UtcNow,
                Profiles = merged.Values
                    .OrderByDescending(p => p.UpdatedUtc)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        private static void MergeFilterEnvelopeInto(Dictionary<string, SharedProfileRecord> merged, FilterProfileStoreEnvelope env, bool isHistory)
        {
            foreach (var p in env.Profiles ?? new List<FilterProfile>())
            {
                if (string.IsNullOrWhiteSpace(p.Id))
                    continue;

                merged.TryGetValue(p.Id, out var cur);

                var history = cur?.HistoryFilters ?? new FilterProfileFilters();
                var archive = cur?.ArchiveFilters ?? new FilterProfileFilters();

                if (isHistory)
                    history = CloneFilters(p.Filters) ?? new FilterProfileFilters();
                else
                    archive = CloneFilters(p.Filters) ?? new FilterProfileFilters();

                var updated = cur?.UpdatedUtc ?? DateTime.MinValue;
                if (p.UpdatedUtc > updated)
                    updated = p.UpdatedUtc;

                merged[p.Id] = new SharedProfileRecord
                {
                    Id = p.Id,
                    Name = string.IsNullOrWhiteSpace(p.Name) ? (cur?.Name ?? p.Id) : p.Name,
                    UpdatedUtc = updated,
                    SettingsRules = cur?.SettingsRules?.ToList() ?? new List<FilterRuleStateRecord>(),
                    HistoryFilters = history,
                    ArchiveFilters = archive
                };
            }
        }

        private static SharedEnvelope Clone(SharedEnvelope src)
        {
            return new SharedEnvelope
            {
                SchemaVersion = src.SchemaVersion,
                UpdatedUtc = src.UpdatedUtc,
                Profiles = src.Profiles.Select(p => new SharedProfileRecord
                {
                    Id = p.Id,
                    Name = p.Name,
                    UpdatedUtc = p.UpdatedUtc,
                    SettingsRules = p.SettingsRules?.ToList() ?? new List<FilterRuleStateRecord>(),
                    HistoryFilters = CloneFilters(p.HistoryFilters) ?? new FilterProfileFilters(),
                    ArchiveFilters = CloneFilters(p.ArchiveFilters) ?? new FilterProfileFilters()
                }).ToList()
            };
        }

        private static FilterProfileFilters? CloneFilters(FilterProfileFilters? f)
        {
            if (f == null)
                return null;

            return new FilterProfileFilters
            {
                ReceiverValue = f.ReceiverValue,
                ReceiverLabel = f.ReceiverLabel,
                SiteValue = f.SiteValue,
                SiteLabel = f.SiteLabel,
                TalkgroupValue = f.TalkgroupValue,
                TalkgroupLabel = f.TalkgroupLabel,
                SelectedDateLocal = f.SelectedDateLocal,
                SelectedTime = f.SelectedTime
            };
        }

        private static FilterProfileStoreEnvelope? TryLoadProfileFiltersEnvelope(string context)
        {
            try
            {
                // Legacy file names used by History/Archive pages.
                // We search for any json file in AppDataDirectory that contains the context and "profiles".
                var dir = FileSystem.AppDataDirectory;
                if (!Directory.Exists(dir))
                    return null;

                var candidates = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(p => p.IndexOf(context, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                foreach (var path in candidates)
                {
                    try
                    {
                        var json = File.ReadAllText(path, Encoding.UTF8);
                        if (string.IsNullOrWhiteSpace(json))
                            continue;

                        var env = JsonSerializer.Deserialize<FilterProfileStoreEnvelope>(json, JsonOptions);
                        if (env?.Profiles != null && env.Profiles.Count > 0)
                            return env;
                    }
                    catch
                    {
                        // ignore candidate
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static SettingsFilterProfileStoreEnvelope? TryLoadSettingsEnvelope()
        {
            try
            {
                // Settings legacy file names.
                var dir = FileSystem.AppDataDirectory;
                if (!Directory.Exists(dir))
                    return null;

                var candidates = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(p => p.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                foreach (var path in candidates)
                {
                    try
                    {
                        var json = File.ReadAllText(path, Encoding.UTF8);
                        if (string.IsNullOrWhiteSpace(json))
                            continue;

                        var env = JsonSerializer.Deserialize<SettingsFilterProfileStoreEnvelope>(json, JsonOptions);
                        if (env?.Profiles != null && env.Profiles.Count > 0)
                            return env;

                        // Some older exports may not match envelope exactly; attempt loose parse.
                        var loose = TryParseLegacySettingsJson(json);
                        if (loose?.Profiles != null && loose.Profiles.Count > 0)
                            return loose;
                    }
                    catch
                    {
                        // ignore candidate
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static SettingsFilterProfileStoreEnvelope? TryParseLegacySettingsJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("profiles", out var profilesEl) || profilesEl.ValueKind != JsonValueKind.Array)
                    return null;

                var list = new List<SettingsFilterProfile>();
                foreach (var item in profilesEl.EnumerateArray())
                {
                    var id = GetString(item, "id");
                    var name = GetString(item, "name");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    list.Add(new SettingsFilterProfile
                    {
                        Id = id ?? string.Empty,
                        Name = name ?? string.Empty,
                        UpdatedUtc = GetDateTime(item, "updatedUtc") ?? GetDateTime(item, "updated_utc") ?? DateTime.UtcNow,
                        Rules = ReadRules(item)
                    });
                }

                return new SettingsFilterProfileStoreEnvelope
                {
                    SchemaVersion = GetInt(doc.RootElement, "schemaVersion") ?? GetInt(doc.RootElement, "schema_version") ?? 1,
                    Context = "settings",
                    ExportedUtc = DateTime.UtcNow,
                    Profiles = list
                };
            }
            catch
            {
                return null;
            }
        }

        private static List<FilterRuleStateRecord> ReadRules(JsonElement profileEl)
        {
            if (!profileEl.TryGetProperty("rules", out var rulesEl) || rulesEl.ValueKind != JsonValueKind.Array)
                return new List<FilterRuleStateRecord>();

            try
            {
                var rules = JsonSerializer.Deserialize<List<FilterRuleStateRecord>>(rulesEl.GetRawText(), JsonOptions);
                return rules ?? new List<FilterRuleStateRecord>();
            }
            catch
            {
                return new List<FilterRuleStateRecord>();
            }
        }

        private static string? GetString(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v))
                return null;

            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static int? GetInt(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v))
                return null;

            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;

            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j))
                return j;

            return null;
        }

        private static DateTime? GetDateTime(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v))
                return null;

            if (v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var dt))
                return dt;

            return null;
        }

        private static DateTime MaxUtc(DateTime a, DateTime b) => a >= b ? a : b;
    }

    internal sealed class SharedEnvelope
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("updated_utc")]
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("profiles")]
        public List<SharedProfileRecord> Profiles { get; set; } = new();
    }

    internal sealed class SharedProfileRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("updated_utc")]
        public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;

        [JsonPropertyName("settings_rules")]
        public List<FilterRuleStateRecord> SettingsRules { get; init; } = new();

        [JsonPropertyName("history_filters")]
        public FilterProfileFilters HistoryFilters { get; init; } = new();

        [JsonPropertyName("archive_filters")]
        public FilterProfileFilters ArchiveFilters { get; init; } = new();
    }
}
