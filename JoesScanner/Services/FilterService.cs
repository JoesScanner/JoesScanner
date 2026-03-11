using JoesScanner.Models;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;

namespace JoesScanner.Services
{
    // V2 filter engine.
    // Separates discovered hierarchy from explicit user state.
    // Discovery is additive and server-scoped.
    // State is local and server-scoped.
    internal sealed class FilterService
    {
        private const string FilterDiscoveryDbKey = "filter_discovery_v2";
        private const string FilterStateDbKey = "filter_state_v2";

        private readonly ObservableCollection<FilterRule> _rules = [];
        private readonly Lock _gate = new();
        private readonly Dictionary<string, DiscoveryNode> _discovery = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FilterRuleStateRecord> _state = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Lazy<FilterService> _instance = new(static () => new FilterService());
        public static FilterService Instance => _instance.Value;

        private string _currentServerKey = string.Empty;

        private enum DiscoveryUpdateKind
        {
            None,
            MetadataOnly,
            Structural
        }

        public ObservableCollection<FilterRule> Rules => _rules;
        public event EventHandler? RulesChanged;

        private FilterService()
        {
            LoadCurrentServerFromStorage();
            RebuildRenderedRules();
        }

        public int PruneTalkgroupRulesNotInLookups(HistoryLookupData? lookups)
        {
            return 0;
        }

        public int PruneTalkgroupRulesNotInLookups(HistoryLookupData? lookups, IReadOnlyList<ObservedTriple>? localObserved)
        {
            return 0;
        }

        public void SeedFromObservedTriples(IReadOnlyList<ObservedTriple> triples)
        {
            if (triples == null || triples.Count == 0)
                return;

            var structureChanged = false;

            lock (_gate)
            {
                foreach (var triple in triples)
                {
                    if (!TryMapObservedTriple(triple, out var node))
                        continue;

                    var change = UpsertDiscovery_NoLock(node);
                    if (change == DiscoveryUpdateKind.Structural)
                        structureChanged = true;
                }
            }

            if (!structureChanged)
                return;

            SaveDiscoveryToStorage();
            RebuildRenderedRules();
        }

        public int ReplaceFromObservedTriples(IReadOnlyList<ObservedTriple> triples)
        {
            lock (_gate)
            {
                _discovery.Clear();

                if (triples != null)
                {
                    foreach (var triple in triples)
                    {
                        if (!TryMapObservedTriple(triple, out var node))
                            continue;

                        UpsertDiscovery_NoLock(node);
                    }
                }
            }

            SaveDiscoveryToStorage();
            RebuildRenderedRules();
            return _discovery.Count;
        }

        public List<FilterRuleStateRecord> GetActiveStateRecords()
        {
            lock (_gate)
            {
                return _state.Values
                    .Where(x => x.IsMuted || x.IsDisabled)
                    .Select(x => new FilterRuleStateRecord
                    {
                        Level = x.Level,
                        Receiver = x.Receiver,
                        Site = x.Site,
                        Talkgroup = x.Talkgroup,
                        IsMuted = x.IsMuted,
                        IsDisabled = x.IsDisabled
                    })
                    .ToList();
            }
        }

        public void ApplyStateRecords(IEnumerable<FilterRuleStateRecord> records, bool resetOthers)
        {
            lock (_gate)
            {
                if (resetOthers)
                    _state.Clear();

                foreach (var rec in records ?? Enumerable.Empty<FilterRuleStateRecord>())
                {
                    if (rec == null)
                        continue;

                    var receiver = Normalize(rec.Receiver);
                    var site = Normalize(rec.Site);
                    var talkgroup = Normalize(rec.Talkgroup);
                    if (!IsRuleValid(rec.Level, receiver, site, talkgroup))
                        continue;

                    var state = new FilterRuleStateRecord
                    {
                        Level = rec.Level,
                        Receiver = receiver,
                        Site = site,
                        Talkgroup = talkgroup,
                        IsMuted = rec.IsMuted,
                        IsDisabled = rec.IsDisabled
                    };

                    var key = state.Key;
                    if (!state.IsMuted && !state.IsDisabled)
                        _state.Remove(key);
                    else
                        _state[key] = state;
                }
            }

            SaveStateToStorage();
            RebuildRenderedRules();
        }

        public void SetServerUrl(string serverUrl)
        {
            var key = NormalizeServerKey(serverUrl);
            if (string.Equals(_currentServerKey, key, StringComparison.OrdinalIgnoreCase))
                return;

            _currentServerKey = key;
            LoadCurrentServerFromStorage();
            RebuildRenderedRules();
        }

        public void ToggleMute(FilterRule rule)
        {
            if (rule == null)
                return;

            lock (_gate)
            {
                var state = GetOrCreateExplicitState_NoLock(rule.Level, Normalize(rule.Receiver), Normalize(rule.Site), Normalize(rule.Talkgroup));
                state.IsMuted = !rule.IsMuted;
                SaveOrRemoveState_NoLock(state);
            }

            SaveStateToStorage();
            RebuildRenderedRules();
        }

        public void ToggleDisable(FilterRule rule)
        {
            if (rule == null)
                return;

            lock (_gate)
            {
                var state = GetOrCreateExplicitState_NoLock(rule.Level, Normalize(rule.Receiver), Normalize(rule.Site), Normalize(rule.Talkgroup));
                state.IsDisabled = !rule.IsDisabled;
                SaveOrRemoveState_NoLock(state);
            }

            SaveStateToStorage();
            RebuildRenderedRules();
        }

        public void ClearRule(FilterRule rule)
        {
            if (rule == null)
                return;

            lock (_gate)
            {
                ClearEffectiveStatePath_NoLock(rule);
            }

            SaveStateToStorage();
            RebuildRenderedRules();
        }

        public void EnsureRulesForCall(CallItem call)
        {
            if (call == null)
                return;

            if (IsErrorCall(call) || IsMetaOrNonTrafficCall(call))
                return;

            var receiver = Normalize(call.ReceiverName);
            if (string.IsNullOrWhiteSpace(receiver))
                return;

            var site = Normalize(call.SystemName);
            var talkgroup = Normalize(call.Talkgroup);
            var nowUtc = DateTime.UtcNow;
            var structureChanged = false;

            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(site) && !string.IsNullOrWhiteSpace(talkgroup) && !IsInvalidTalkgroupToken(talkgroup))
                {
                    var change = UpsertDiscovery_NoLock(new DiscoveryNode
                    {
                        ReceiverKey = receiver,
                        SiteKey = site,
                        TalkgroupKey = talkgroup,
                        Receiver = receiver,
                        Site = site,
                        Talkgroup = talkgroup,
                        LastSeenUtc = nowUtc
                    });

                    structureChanged = change == DiscoveryUpdateKind.Structural;
                }
                else
                {
                    var change = UpsertDiscovery_NoLock(new DiscoveryNode
                    {
                        ReceiverKey = receiver,
                        SiteKey = string.Empty,
                        TalkgroupKey = string.Empty,
                        Receiver = receiver,
                        Site = string.Empty,
                        Talkgroup = string.Empty,
                        LastSeenUtc = nowUtc
                    });

                    structureChanged = change == DiscoveryUpdateKind.Structural;
                }
            }

            if (!structureChanged)
                return;

            SaveDiscoveryToStorage();
            RebuildRenderedRules();
        }

        public bool ShouldHide(CallItem call)
        {
            var rule = GetBestMatchRuleForCall(call);
            return rule != null && rule.IsDisabled;
        }

        public bool ShouldMute(CallItem call)
        {
            var rule = GetBestMatchRuleForCall(call);
            if (rule == null)
                return false;

            return rule.IsDisabled || rule.IsMuted;
        }

        private void LoadCurrentServerFromStorage()
        {
            lock (_gate)
            {
                _discovery.Clear();
                _state.Clear();

                foreach (var item in LoadDiscoveryDtos())
                {
                    if (!IsDiscoveryValid(item.Receiver, item.Site, item.Talkgroup))
                        continue;

                    var node = new DiscoveryNode
                    {
                        ReceiverKey = Normalize(string.IsNullOrWhiteSpace(item.ReceiverKey) ? item.Receiver : item.ReceiverKey),
                        SiteKey = Normalize(string.IsNullOrWhiteSpace(item.SiteKey) ? item.Site : item.SiteKey),
                        TalkgroupKey = Normalize(string.IsNullOrWhiteSpace(item.TalkgroupKey) ? item.Talkgroup : item.TalkgroupKey),
                        Receiver = Normalize(item.Receiver),
                        Site = Normalize(item.Site),
                        Talkgroup = Normalize(item.Talkgroup),
                        LastSeenUtc = item.LastSeenUtc == default ? DateTime.UtcNow : item.LastSeenUtc
                    };

                    UpsertDiscovery_NoLock(node);
                }

                foreach (var state in LoadStateDtos())
                {
                    if (state == null)
                        continue;

                    var receiver = Normalize(state.Receiver);
                    var site = Normalize(state.Site);
                    var talkgroup = Normalize(state.Talkgroup);
                    if (!IsRuleValid(state.Level, receiver, site, talkgroup))
                        continue;

                    state.Receiver = receiver;
                    state.Site = site;
                    state.Talkgroup = talkgroup;
                    SaveOrRemoveState_NoLock(state);
                }
            }
        }

        private List<DiscoveryNodeDto> LoadDiscoveryDtos()
        {
            try
            {
                var json = AppStateStore.GetString(GetDiscoveryStorageKey(), string.Empty);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<DiscoveryNodeDto>();

                return JsonSerializer.Deserialize<List<DiscoveryNodeDto>>(json) ?? new List<DiscoveryNodeDto>();
            }
            catch
            {
                return new List<DiscoveryNodeDto>();
            }
        }

        private List<FilterRuleStateRecord> LoadStateDtos()
        {
            try
            {
                var json = AppStateStore.GetString(GetStateStorageKey(), string.Empty);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<FilterRuleStateRecord>();

                return JsonSerializer.Deserialize<List<FilterRuleStateRecord>>(json) ?? new List<FilterRuleStateRecord>();
            }
            catch
            {
                return new List<FilterRuleStateRecord>();
            }
        }

        private void SaveDiscoveryToStorage()
        {
            try
            {
                List<DiscoveryNodeDto> rows;
                lock (_gate)
                {
                    rows = _discovery.Values
                        .OrderBy(x => x.Receiver, NaturalComparer.Instance)
                        .ThenBy(x => x.Site, NaturalComparer.Instance)
                        .ThenBy(x => x.Talkgroup, NaturalComparer.Instance)
                        .Select(x => new DiscoveryNodeDto
                        {
                            ReceiverKey = x.ReceiverKey,
                            SiteKey = x.SiteKey,
                            TalkgroupKey = x.TalkgroupKey,
                            Receiver = x.Receiver,
                            Site = x.Site,
                            Talkgroup = x.Talkgroup,
                            LastSeenUtc = x.LastSeenUtc
                        })
                        .ToList();
                }

                AppStateStore.SetString(GetDiscoveryStorageKey(), JsonSerializer.Serialize(rows));
            }
            catch
            {
            }
        }

        private void SaveStateToStorage()
        {
            try
            {
                List<FilterRuleStateRecord> rows;
                lock (_gate)
                {
                    rows = _state.Values
                        .Where(x => x.IsMuted || x.IsDisabled)
                        .Select(x => new FilterRuleStateRecord
                        {
                            Level = x.Level,
                            Receiver = x.Receiver,
                            Site = x.Site,
                            Talkgroup = x.Talkgroup,
                            IsMuted = x.IsMuted,
                            IsDisabled = x.IsDisabled
                        })
                        .ToList();
                }

                AppStateStore.SetString(GetStateStorageKey(), JsonSerializer.Serialize(rows));
            }
            catch
            {
            }
        }

        private string GetDiscoveryStorageKey()
        {
            var k = string.IsNullOrWhiteSpace(_currentServerKey) ? "none" : _currentServerKey.Trim();
            return $"{FilterDiscoveryDbKey}::{k}";
        }

        private string GetStateStorageKey()
        {
            var k = string.IsNullOrWhiteSpace(_currentServerKey) ? "none" : _currentServerKey.Trim();
            return $"{FilterStateDbKey}::{k}";
        }

        private void RebuildRenderedRules()
        {
            List<FilterRule> built;

            lock (_gate)
            {
                var receiverRows = _discovery.Values
                    .Where(x => !string.IsNullOrWhiteSpace(x.Receiver))
                    .GroupBy(x => x.ReceiverKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g
                        .OrderByDescending(x => x.LastSeenUtc)
                        .ThenByDescending(x => x.Receiver?.Length ?? 0)
                        .ThenBy(x => x.Receiver, NaturalComparer.Instance)
                        .First())
                    .OrderBy(x => x.Receiver, NaturalComparer.Instance)
                    .ToList();

                var siteRows = _discovery.Values
                    .Where(x => !string.IsNullOrWhiteSpace(x.Receiver) && !string.IsNullOrWhiteSpace(x.Site))
                    .GroupBy(x => (x.ReceiverKey, x.SiteKey), StringTupleComparer.Instance)
                    .Select(g => g
                        .OrderByDescending(x => x.LastSeenUtc)
                        .ThenByDescending(x => x.Site?.Length ?? 0)
                        .ThenBy(x => x.Site, NaturalComparer.Instance)
                        .First())
                    .OrderBy(x => x.Receiver, NaturalComparer.Instance)
                    .ThenBy(x => x.Site, NaturalComparer.Instance)
                    .ToList();

                var talkgroupRows = _discovery.Values
                    .Where(x => !string.IsNullOrWhiteSpace(x.Receiver) && !string.IsNullOrWhiteSpace(x.Site) && !string.IsNullOrWhiteSpace(x.Talkgroup))
                    .GroupBy(x => BuildDiscoveryKey(x.ReceiverKey, x.SiteKey, x.TalkgroupKey, x.Receiver, x.Site, x.Talkgroup), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g
                        .OrderByDescending(x => x.LastSeenUtc)
                        .ThenByDescending(x => x.Talkgroup?.Length ?? 0)
                        .ThenBy(x => x.Talkgroup, NaturalComparer.Instance)
                        .First())
                    .OrderBy(x => x.Receiver, NaturalComparer.Instance)
                    .ThenBy(x => x.Site, NaturalComparer.Instance)
                    .ThenBy(x => x.Talkgroup, NaturalComparer.Instance)
                    .ToList();

                built = new List<FilterRule>(receiverRows.Count + siteRows.Count + talkgroupRows.Count);

                foreach (var receiverRow in receiverRows)
                {
                    var receiverState = TryGetState_NoLock(FilterLevel.Receiver, receiverRow.Receiver, string.Empty, string.Empty);
                    built.Add(new FilterRule
                    {
                        Level = FilterLevel.Receiver,
                        Receiver = receiverRow.Receiver,
                        Site = string.Empty,
                        Talkgroup = string.Empty,
                        IsMuted = receiverState?.IsMuted ?? false,
                        IsDisabled = receiverState?.IsDisabled ?? false,
                        LastSeenUtc = receiverRow.LastSeenUtc
                    });

                    foreach (var siteRow in siteRows.Where(x => string.Equals(x.ReceiverKey, receiverRow.ReceiverKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        var siteState = TryGetState_NoLock(FilterLevel.Site, siteRow.Receiver, siteRow.Site, string.Empty);
                        built.Add(new FilterRule
                        {
                            Level = FilterLevel.Site,
                            Receiver = siteRow.Receiver,
                            Site = siteRow.Site,
                            Talkgroup = string.Empty,
                            IsMuted = siteState?.IsMuted ?? receiverState?.IsMuted ?? false,
                            IsDisabled = siteState?.IsDisabled ?? receiverState?.IsDisabled ?? false,
                            LastSeenUtc = GetLatestLastSeen_NoLock(siteRow.Receiver, siteRow.Site, string.Empty)
                        });

                        foreach (var talkgroupRow in talkgroupRows.Where(x =>
                                     string.Equals(x.ReceiverKey, siteRow.ReceiverKey, StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(x.SiteKey, siteRow.SiteKey, StringComparison.OrdinalIgnoreCase)))
                        {
                            var tgState = TryGetState_NoLock(FilterLevel.Talkgroup, talkgroupRow.Receiver, talkgroupRow.Site, talkgroupRow.Talkgroup);
                            built.Add(new FilterRule
                            {
                                Level = FilterLevel.Talkgroup,
                                Receiver = talkgroupRow.Receiver,
                                Site = talkgroupRow.Site,
                                Talkgroup = talkgroupRow.Talkgroup,
                                IsMuted = tgState?.IsMuted ?? siteState?.IsMuted ?? receiverState?.IsMuted ?? false,
                                IsDisabled = tgState?.IsDisabled ?? siteState?.IsDisabled ?? receiverState?.IsDisabled ?? false,
                                LastSeenUtc = talkgroupRow.LastSeenUtc
                            });
                        }
                    }
                }
            }

            InvokeOnMainThreadSync(() => ApplyRenderedRulesSnapshot(built));

            RulesChanged?.Invoke(this, EventArgs.Empty);
        }


        private void ApplyRenderedRulesSnapshot(List<FilterRule> built)
        {
            var existingByKey = new Dictionary<string, FilterRule>(StringComparer.OrdinalIgnoreCase);
            var duplicateIndexes = new List<int>();

            for (var i = 0; i < _rules.Count; i++)
            {
                var current = _rules[i];
                var key = BuildRuleKey(current);
                if (existingByKey.ContainsKey(key))
                {
                    duplicateIndexes.Add(i);
                    continue;
                }

                existingByKey[key] = current;
            }

            for (var i = duplicateIndexes.Count - 1; i >= 0; i--)
                _rules.RemoveAt(duplicateIndexes[i]);

            var desired = new List<FilterRule>(built.Count);
            var desiredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in built)
            {
                var key = BuildRuleKey(item);
                if (!desiredKeys.Add(key))
                    continue;

                if (existingByKey.TryGetValue(key, out var existing))
                {
                    UpdateRule(existing, item);
                    desired.Add(existing);
                }
                else
                {
                    desired.Add(item);
                }
            }

            for (var i = _rules.Count - 1; i >= 0; i--)
            {
                var current = _rules[i];
                if (!desired.Contains(current))
                    _rules.RemoveAt(i);
            }

            for (var i = 0; i < desired.Count; i++)
            {
                var desiredRule = desired[i];
                if (i < _rules.Count)
                {
                    if (ReferenceEquals(_rules[i], desiredRule))
                        continue;

                    var existingIndex = _rules.IndexOf(desiredRule);
                    if (existingIndex >= 0)
                    {
                        _rules.Move(existingIndex, i);
                    }
                    else
                    {
                        _rules.Insert(i, desiredRule);
                    }
                }
                else
                {
                    _rules.Add(desiredRule);
                }
            }

            while (_rules.Count > desired.Count)
                _rules.RemoveAt(_rules.Count - 1);
        }

        private static void UpdateRule(FilterRule target, FilterRule source)
        {
            target.Level = source.Level;
            target.Receiver = source.Receiver;
            target.Site = source.Site;
            target.Talkgroup = source.Talkgroup;
            target.IsMuted = source.IsMuted;
            target.IsDisabled = source.IsDisabled;
            target.LastSeenUtc = source.LastSeenUtc;
        }

        private static string BuildRuleKey(FilterRule rule)
        {
            return FilterRuleStateRecord.BuildKey(rule.Level, Normalize(rule.Receiver), Normalize(rule.Site), Normalize(rule.Talkgroup));
        }

        private FilterRule? GetBestMatchRuleForCall(CallItem call)
        {
            if (call == null)
                return null;

            var receiver = Normalize(call.ReceiverName);
            if (string.IsNullOrWhiteSpace(receiver))
                return null;

            var site = Normalize(call.SystemName);
            var talkgroup = Normalize(call.Talkgroup);

            FilterRule[] snapshot;
            lock (_gate)
            {
                snapshot = _rules.ToArray();
            }

            FilterRule? receiverRule = null;
            FilterRule? siteRule = null;
            FilterRule? talkgroupRule = null;

            foreach (var rule in snapshot)
            {
                if (!string.Equals(Normalize(rule.Receiver), receiver, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (rule.Level == FilterLevel.Receiver)
                {
                    receiverRule = rule;
                    continue;
                }

                if (rule.Level == FilterLevel.Site)
                {
                    if (!string.IsNullOrWhiteSpace(site) && string.Equals(Normalize(rule.Site), site, StringComparison.OrdinalIgnoreCase))
                        siteRule = rule;
                    continue;
                }

                if (rule.Level == FilterLevel.Talkgroup)
                {
                    if (!string.IsNullOrWhiteSpace(site) &&
                        !string.IsNullOrWhiteSpace(talkgroup) &&
                        string.Equals(Normalize(rule.Site), site, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Normalize(rule.Talkgroup), talkgroup, StringComparison.OrdinalIgnoreCase))
                    {
                        talkgroupRule = rule;
                    }
                }
            }

            return talkgroupRule ?? siteRule ?? receiverRule;
        }

        private static bool TryMapObservedTriple(ObservedTriple? triple, out DiscoveryNode node)
        {
            node = new DiscoveryNode();
            if (triple == null)
                return false;

            // App-side filtering and matching operate on the rendered Receiver/Site/Talkgroup text,
            // not on backend ids. Use normalized labels as the canonical keys so the Settings page
            // does not show duplicate rows that differ only by hidden value ids.
            var receiverLabel = Normalize(string.IsNullOrWhiteSpace(triple.ReceiverLabel) ? triple.ReceiverValue : triple.ReceiverLabel);
            var siteLabel = Normalize(string.IsNullOrWhiteSpace(triple.SiteLabel) ? triple.SiteValue : triple.SiteLabel);
            var talkgroupLabel = Normalize(string.IsNullOrWhiteSpace(triple.TalkgroupLabel) ? triple.TalkgroupValue : triple.TalkgroupLabel);

            var receiverKey = receiverLabel;
            var siteKey = siteLabel;
            var talkgroupKey = talkgroupLabel;

            if (!IsDiscoveryValid(receiverLabel, siteLabel, talkgroupLabel))
                return false;

            node = new DiscoveryNode
            {
                ReceiverKey = receiverKey,
                SiteKey = siteKey,
                TalkgroupKey = talkgroupKey,
                Receiver = receiverLabel,
                Site = siteLabel,
                Talkgroup = talkgroupLabel,
                LastSeenUtc = triple.LastSeenUtc == default ? DateTime.UtcNow : triple.LastSeenUtc
            };
            return true;
        }

        private DiscoveryUpdateKind UpsertDiscovery_NoLock(DiscoveryNode node)
        {
            var key = BuildDiscoveryKey(node.ReceiverKey, node.SiteKey, node.TalkgroupKey, node.Receiver, node.Site, node.Talkgroup);
            if (_discovery.TryGetValue(key, out var existing))
            {
                var metadataChanged = false;

                if (node.LastSeenUtc > existing.LastSeenUtc)
                {
                    existing.LastSeenUtc = node.LastSeenUtc;
                    metadataChanged = true;
                }

                if (PreferLabel(node.Receiver, existing.Receiver))
                {
                    existing.Receiver = node.Receiver;
                    metadataChanged = true;
                }

                if (PreferLabel(node.Site, existing.Site))
                {
                    existing.Site = node.Site;
                    metadataChanged = true;
                }

                if (PreferLabel(node.Talkgroup, existing.Talkgroup))
                {
                    existing.Talkgroup = node.Talkgroup;
                    metadataChanged = true;
                }

                return metadataChanged ? DiscoveryUpdateKind.MetadataOnly : DiscoveryUpdateKind.None;
            }

            _discovery[key] = node;
            return DiscoveryUpdateKind.Structural;
        }

        private static string BuildDiscoveryKey(string receiverKey, string siteKey, string talkgroupKey, string receiverLabel, string siteLabel, string talkgroupLabel)
        {
            var receiver = Normalize(receiverKey);
            var site = Normalize(siteKey);
            var talkgroup = Normalize(talkgroupKey);

            if (string.IsNullOrWhiteSpace(receiver))
                receiver = Normalize(receiverLabel);
            if (string.IsNullOrWhiteSpace(site))
                site = Normalize(siteLabel);
            if (string.IsNullOrWhiteSpace(talkgroup))
                talkgroup = Normalize(talkgroupLabel);

            return $"{receiver}\n{site}\n{talkgroup}".ToUpperInvariant();
        }

        private static bool PreferLabel(string candidate, string existing)
        {
            candidate = Normalize(candidate);
            existing = Normalize(existing);

            if (string.IsNullOrWhiteSpace(candidate))
                return false;
            if (string.IsNullOrWhiteSpace(existing))
                return true;
            if (string.Equals(candidate, existing, StringComparison.OrdinalIgnoreCase))
                return false;

            var candidateHasLetters = candidate.Any(char.IsLetter);
            var existingHasLetters = existing.Any(char.IsLetter);
            if (candidateHasLetters != existingHasLetters)
                return candidateHasLetters;

            if (candidate.Length != existing.Length)
                return candidate.Length > existing.Length;

            return string.Compare(candidate, existing, StringComparison.OrdinalIgnoreCase) < 0;
        }

        private FilterRuleStateRecord GetOrCreateExplicitState_NoLock(FilterLevel level, string receiver, string site, string talkgroup)
        {
            var key = FilterRuleStateRecord.BuildKey(level, receiver, site, talkgroup);
            if (_state.TryGetValue(key, out var existing))
                return new FilterRuleStateRecord
                {
                    Level = existing.Level,
                    Receiver = existing.Receiver,
                    Site = existing.Site,
                    Talkgroup = existing.Talkgroup,
                    IsMuted = existing.IsMuted,
                    IsDisabled = existing.IsDisabled
                };

            return new FilterRuleStateRecord
            {
                Level = level,
                Receiver = receiver,
                Site = site,
                Talkgroup = talkgroup,
                IsMuted = false,
                IsDisabled = false
            };
        }

        private void SaveOrRemoveState_NoLock(FilterRuleStateRecord state)
        {
            if (!state.IsMuted && !state.IsDisabled)
            {
                _state.Remove(state.Key);
                return;
            }

            _state[state.Key] = new FilterRuleStateRecord
            {
                Level = state.Level,
                Receiver = state.Receiver,
                Site = state.Site,
                Talkgroup = state.Talkgroup,
                IsMuted = state.IsMuted,
                IsDisabled = state.IsDisabled
            };
        }

        private void ClearEffectiveStatePath_NoLock(FilterRule rule)
        {
            var receiver = Normalize(rule.Receiver);
            var site = Normalize(rule.Site);
            var talkgroup = Normalize(rule.Talkgroup);

            // Clear must fully remove the active filter effect for the tapped row.
            // Remove explicit state at this row and every inherited parent level that can
            // still make the row appear muted or disabled. This avoids the common case where
            // a row still looks unchanged because an ancestor state remained active.
            switch (rule.Level)
            {
                case FilterLevel.Talkgroup:
                    TryRemoveState_NoLock(FilterLevel.Talkgroup, receiver, site, talkgroup);
                    TryRemoveState_NoLock(FilterLevel.Site, receiver, site, string.Empty);
                    TryRemoveState_NoLock(FilterLevel.Receiver, receiver, string.Empty, string.Empty);
                    break;

                case FilterLevel.Site:
                    TryRemoveState_NoLock(FilterLevel.Site, receiver, site, string.Empty);
                    TryRemoveState_NoLock(FilterLevel.Receiver, receiver, string.Empty, string.Empty);
                    break;

                default:
                    TryRemoveState_NoLock(FilterLevel.Receiver, receiver, string.Empty, string.Empty);
                    break;
            }
        }

        private bool TryRemoveState_NoLock(FilterLevel level, string receiver, string site, string talkgroup)
        {
            return _state.Remove(FilterRuleStateRecord.BuildKey(level, receiver, site, talkgroup));
        }

        private FilterRuleStateRecord? TryGetState_NoLock(FilterLevel level, string receiver, string site, string talkgroup)
        {
            _state.TryGetValue(FilterRuleStateRecord.BuildKey(level, receiver, site, talkgroup), out var record);
            return record;
        }

        private DateTime GetLatestLastSeen_NoLock(string receiver, string site, string talkgroup)
        {
            receiver = Normalize(receiver);
            site = Normalize(site);
            talkgroup = Normalize(talkgroup);

            var latest = DateTime.MinValue;
            foreach (var item in _discovery.Values)
            {
                if (!string.Equals(item.Receiver, receiver, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(site) && !string.Equals(item.Site, site, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(talkgroup) && !string.Equals(item.Talkgroup, talkgroup, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (item.LastSeenUtc > latest)
                    latest = item.LastSeenUtc;
            }

            return latest == DateTime.MinValue ? DateTime.UtcNow : latest;
        }

        private static string NormalizeServerKey(string? serverUrl)
        {
            var raw = (serverUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            raw = raw.TrimEnd('/');

            try
            {
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                    return raw;

                var builder = new UriBuilder(uri)
                {
                    Path = string.Empty,
                    Query = string.Empty,
                    Fragment = string.Empty,
                    Port = uri.IsDefaultPort ? -1 : uri.Port
                };

                return builder.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return raw;
            }
        }

        private static void InvokeOnMainThreadSync(Action action)
        {
            if (MainThread.IsMainThread)
            {
                action();
                return;
            }

            Exception? captured = null;
            using var gate = new ManualResetEventSlim(false);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
                finally { gate.Set(); }
            });
            gate.Wait();
            if (captured != null)
                throw captured;
        }

        private static string Normalize(string? value)
        {
            var v = (value ?? string.Empty).Trim();
            if (v.Length == 0)
                return string.Empty;

            var hasRun = false;
            var prevWs = false;
            for (var i = 0; i < v.Length; i++)
            {
                var ws = char.IsWhiteSpace(v[i]);
                if (ws && prevWs)
                {
                    hasRun = true;
                    break;
                }
                prevWs = ws;
            }

            if (!hasRun)
                return v;

            var sb = new StringBuilder(v.Length);
            prevWs = false;
            for (var i = 0; i < v.Length; i++)
            {
                var ch = v[i];
                var ws = char.IsWhiteSpace(ch);
                if (ws)
                {
                    if (!prevWs)
                        sb.Append(' ');
                    prevWs = true;
                    continue;
                }

                sb.Append(ch);
                prevWs = false;
            }

            return sb.ToString().Trim();
        }

        private static bool IsErrorCall(CallItem call)
        {
            var receiverEmpty = string.IsNullOrWhiteSpace(call.ReceiverName);
            var siteEmpty = string.IsNullOrWhiteSpace(call.SystemName);
            var talkgroup = (call.Talkgroup ?? string.Empty).Trim();

            if (!receiverEmpty || !siteEmpty)
                return false;

            if (talkgroup.Length == 0)
                return false;

            var upper = talkgroup.ToUpperInvariant();
            return upper == "ERROR" || upper == ">> ERROR" || upper.Contains(" ERROR", StringComparison.Ordinal);
        }

        private static bool IsMetaOrNonTrafficCall(CallItem call)
        {
            var receiverEmpty = string.IsNullOrWhiteSpace(call.ReceiverName);
            var siteEmpty = string.IsNullOrWhiteSpace(call.SystemName);
            if (!receiverEmpty || !siteEmpty)
                return false;

            var tg = (call.Talkgroup ?? string.Empty).Trim();
            if (tg.Length == 0)
                return true;

            var upper = tg.ToUpperInvariant();
            if (upper == "HEARTBEAT" || upper == ">> HEARTBEAT")
                return true;
            if (upper.StartsWith(">>", StringComparison.Ordinal))
                return true;
            return IsInvalidTalkgroupToken(tg);
        }

        private static bool IsInvalidTalkgroupToken(string talkgroup)
        {
            if (string.IsNullOrWhiteSpace(talkgroup))
                return true;

            var stripped = talkgroup.Replace(">", string.Empty, StringComparison.Ordinal).Trim();
            return stripped.Length == 0;
        }

        private static bool IsDiscoveryValid(string receiver, string site, string talkgroup)
        {
            if (string.IsNullOrWhiteSpace(receiver))
                return false;

            if (string.IsNullOrWhiteSpace(site) || string.IsNullOrWhiteSpace(talkgroup))
                return true;

            return IsRuleValid(FilterLevel.Talkgroup, receiver, site, talkgroup);
        }

        private static bool IsRuleValid(FilterLevel level, string receiver, string site, string talkgroup)
        {
            if (string.IsNullOrWhiteSpace(receiver))
                return false;

            if (level == FilterLevel.Site && string.IsNullOrWhiteSpace(site))
                return false;

            if (level != FilterLevel.Talkgroup)
                return true;

            if (string.IsNullOrWhiteSpace(site))
                return false;
            if (string.IsNullOrWhiteSpace(talkgroup) || IsInvalidTalkgroupToken(talkgroup))
                return false;

            var upper = talkgroup.Trim().ToUpperInvariant();
            if (upper == "ERROR" || upper == ">> ERROR" || upper.Contains(" ERROR", StringComparison.Ordinal))
                return false;
            if (upper == "HEARTBEAT" || upper == ">> HEARTBEAT")
                return false;
            if (upper.StartsWith(">>", StringComparison.Ordinal))
                return false;

            return true;
        }

        private sealed class DiscoveryNode
        {
            public string ReceiverKey { get; set; } = string.Empty;
            public string SiteKey { get; set; } = string.Empty;
            public string TalkgroupKey { get; set; } = string.Empty;
            public string Receiver { get; set; } = string.Empty;
            public string Site { get; set; } = string.Empty;
            public string Talkgroup { get; set; } = string.Empty;
            public DateTime LastSeenUtc { get; set; }
        }

        private sealed class DiscoveryNodeDto
        {
            public string ReceiverKey { get; set; } = string.Empty;
            public string SiteKey { get; set; } = string.Empty;
            public string TalkgroupKey { get; set; } = string.Empty;
            public string Receiver { get; set; } = string.Empty;
            public string Site { get; set; } = string.Empty;
            public string Talkgroup { get; set; } = string.Empty;
            public DateTime LastSeenUtc { get; set; }
        }

        private sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new();
            public int Compare(string? x, string? y) => NaturalCompareIgnoreCase(x ?? string.Empty, y ?? string.Empty);
        }

        private sealed class StringTupleComparer : IEqualityComparer<(string Receiver, string Site)>
        {
            public static readonly StringTupleComparer Instance = new();

            public bool Equals((string Receiver, string Site) x, (string Receiver, string Site) y)
            {
                return string.Equals(x.Receiver, y.Receiver, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Site, y.Site, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode((string Receiver, string Site) obj)
            {
                return HashCode.Combine(obj.Receiver.ToUpperInvariant(), obj.Site.ToUpperInvariant());
            }
        }

        private static int NaturalCompareIgnoreCase(string a, string b)
        {
            if (ReferenceEquals(a, b))
                return 0;

            int ia = 0, ib = 0;
            while (ia < a.Length && ib < b.Length)
            {
                var ca = a[ia];
                var cb = b[ib];
                var da = char.IsDigit(ca);
                var db = char.IsDigit(cb);

                if (da && db)
                {
                    int startA = ia;
                    int startB = ib;
                    while (startA < a.Length && a[startA] == '0') startA++;
                    while (startB < b.Length && b[startB] == '0') startB++;
                    int endA = startA;
                    int endB = startB;
                    while (endA < a.Length && char.IsDigit(a[endA])) endA++;
                    while (endB < b.Length && char.IsDigit(b[endB])) endB++;
                    int lenA = endA - startA;
                    int lenB = endB - startB;
                    if (lenA != lenB)
                        return lenA < lenB ? -1 : 1;
                    for (int k = 0; k < lenA; k++)
                    {
                        char x = a[startA + k];
                        char y = b[startB + k];
                        if (x != y)
                            return x < y ? -1 : 1;
                    }
                    int runA = 0;
                    int runB = 0;
                    while (ia + runA < a.Length && char.IsDigit(a[ia + runA])) runA++;
                    while (ib + runB < b.Length && char.IsDigit(b[ib + runB])) runB++;
                    if (runA != runB)
                        return runA < runB ? -1 : 1;
                    ia += runA;
                    ib += runB;
                    continue;
                }

                var ua = char.ToUpperInvariant(ca);
                var ub = char.ToUpperInvariant(cb);
                if (ua != ub)
                    return ua < ub ? -1 : 1;

                ia++;
                ib++;
            }

            if (ia == a.Length && ib == b.Length)
                return 0;
            return ia == a.Length ? -1 : 1;
        }
    }
}
