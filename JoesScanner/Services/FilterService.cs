using JoesScanner.Models;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JoesScanner.Helpers;

namespace JoesScanner.Services
{
    internal readonly record struct FilterCatalogRow(
        FilterLevel Level, string Receiver, string Site, string Talkgroup,
        bool IsMuted, bool IsDisabled, DateTime LastSeenUtc);

    // V2 filter engine.
    // Separates discovered hierarchy from explicit user state.
    // Discovery is additive and server-scoped.
    // State is local and server-scoped.
    internal sealed class FilterService
    {
        private const string FilterDiscoveryDbKey = "filter_discovery_v2";
        private const string FilterStateDbKey = "filter_state_v2";

        private readonly Lock _gate = new();
        private readonly HashSet<string> _discoveredRules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> _pendingWrites = new(StringComparer.OrdinalIgnoreCase);
        private bool _discoveryDirty;
        private bool _writerRunning;
        private readonly Dictionary<string, DiscoveryNode> _discovery = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FilterRuleStateRecord> _state = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Lazy<FilterService> _instance = new(static () => new FilterService());
        public static FilterService Instance => _instance.Value;

        private string _currentServerKey = string.Empty;

        private enum DiscoveryUpdateKind
        {
            None,
            MetadataOnly,
            Catalog,
            Structural
        }

        public event EventHandler? RulesChanged;
        public event EventHandler? CatalogChanged;

        private FilterService()
        {
            LoadCurrentServerFromStorage();
        }

        public int PruneTalkgroupRulesNotInLookups(HistoryLookupData? lookups)
        {
            return 0;
        }

        public int PruneTalkgroupRulesNotInLookups(HistoryLookupData? lookups, IReadOnlyList<ObservedTriple>? localObserved)
        {
            return 0;
        }

        public void SeedFromObservedTriples(IReadOnlyList<ObservedTriple> triples, string? expectedServerUrl = null)
        {
            var change = DiscoveryUpdateKind.None;
            bool affectsFiltering;
            lock (_gate)
            {
                if (expectedServerUrl != null && !IsCurrentServer_NoLock(expectedServerUrl))
                    return;

                MergeObservedTriples_NoLock(triples, ref change);
                if (change == DiscoveryUpdateKind.None)
                    return;

                affectsFiltering = change == DiscoveryUpdateKind.Structural && _state.Count != 0;
                QueueDiscoveryWrite_NoLock();
            }

            if (change >= DiscoveryUpdateKind.Catalog)
                CatalogChanged?.Invoke(this, EventArgs.Empty);
            if (affectsFiltering)
                RulesChanged?.Invoke(this, EventArgs.Empty);
        }

        public int ReplaceFromObservedTriples(IReadOnlyList<ObservedTriple> triples, string? expectedServerUrl = null)
        {
            int count;
            lock (_gate)
            {
                if (expectedServerUrl != null && !IsCurrentServer_NoLock(expectedServerUrl))
                    return 0;

                _discovery.Clear();
                _discoveredRules.Clear();
                var change = DiscoveryUpdateKind.None;
                MergeObservedTriples_NoLock(triples, ref change);
                count = _discovery.Count;
                QueueDiscoveryWrite_NoLock();
            }

            NotifyRulesChanged();
            return count;
        }

        private void MergeObservedTriples_NoLock(IReadOnlyList<ObservedTriple> triples, ref DiscoveryUpdateKind change)
        {
            if (triples == null)
                return;

            foreach (var triple in triples)
            {
                if (TryMapObservedTriple(triple, out var node))
                {
                    var update = UpsertDiscovery_NoLock(node);
                    if (update > change)
                        change = update;
                }
            }
        }

        private bool IsCurrentServer_NoLock(string serverUrl) =>
            string.Equals(_currentServerKey, ServerKeyHelper.Normalize(serverUrl), StringComparison.OrdinalIgnoreCase);

        private void NotifyRulesChanged()
        {
            RulesChanged?.Invoke(this, EventArgs.Empty);
            CatalogChanged?.Invoke(this, EventArgs.Empty);
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
                var previous = new Dictionary<string, FilterRuleStateRecord>(_state, StringComparer.OrdinalIgnoreCase);
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
                if (_state.Count == previous.Count && _state.All(entry =>
                    previous.TryGetValue(entry.Key, out var old) &&
                    old.IsMuted == entry.Value.IsMuted && old.IsDisabled == entry.Value.IsDisabled))
                {
                    return;
                }

                QueueStateWrite_NoLock();
            }

            NotifyRulesChanged();
        }

        public void SetServerUrl(string serverUrl)
        {
            lock (_gate)
            {
                if (IsCurrentServer_NoLock(serverUrl))
                    return;

                CapturePendingDiscovery_NoLock();
                _currentServerKey = ServerKeyHelper.Normalize(serverUrl);
                LoadCurrentServerFromStorage();
            }

            NotifyRulesChanged();
        }

        public void ToggleMute(FilterRule rule)
        {
            if (rule == null)
                return;

            lock (_gate)
            {
                var state = GetOrCreateExplicitState_NoLock(rule.Level, Normalize(rule.Receiver), Normalize(rule.Site), Normalize(rule.Talkgroup));
                state.IsMuted = GetEffectiveState_NoLock(state.Receiver, state.Site, state.Talkgroup)?.IsMuted != true;
                SaveOrRemoveState_NoLock(state);
                QueueStateWrite_NoLock();
            }

            NotifyRulesChanged();
        }

        public void ToggleDisable(FilterRule rule)
        {
            if (rule == null)
                return;

            lock (_gate)
            {
                var state = GetOrCreateExplicitState_NoLock(rule.Level, Normalize(rule.Receiver), Normalize(rule.Site), Normalize(rule.Talkgroup));
                state.IsDisabled = GetEffectiveState_NoLock(state.Receiver, state.Site, state.Talkgroup)?.IsDisabled != true;
                SaveOrRemoveState_NoLock(state);
                QueueStateWrite_NoLock();
            }

            NotifyRulesChanged();
        }

        public void ClearRule(FilterRule rule)
        {
            if (rule == null)
                return;

            lock (_gate)
            {
                ClearEffectiveStatePath_NoLock(rule);
                QueueStateWrite_NoLock();
            }

            NotifyRulesChanged();
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
            var hasTalkgroup = !string.IsNullOrWhiteSpace(site) &&
                !string.IsNullOrWhiteSpace(talkgroup) && !IsInvalidTalkgroupToken(talkgroup);
            var node = new DiscoveryNode
            {
                ReceiverKey = receiver,
                SiteKey = hasTalkgroup ? site : string.Empty,
                TalkgroupKey = hasTalkgroup ? talkgroup : string.Empty,
                Receiver = receiver,
                Site = hasTalkgroup ? site : string.Empty,
                Talkgroup = hasTalkgroup ? talkgroup : string.Empty,
                LastSeenUtc = nowUtc
            };

            DiscoveryUpdateKind change;
            bool affectsFiltering;
            lock (_gate)
            {
                var priorState = GetEffectiveState_NoLock(receiver, site, talkgroup);
                change = UpsertDiscovery_NoLock(node);
                if (change == DiscoveryUpdateKind.None)
                    return;

                affectsFiltering = change == DiscoveryUpdateKind.Structural &&
                    priorState != GetEffectiveState_NoLock(receiver, site, talkgroup);
                QueueDiscoveryWrite_NoLock();
            }

            if (change >= DiscoveryUpdateKind.Catalog)
                CatalogChanged?.Invoke(this, EventArgs.Empty);
            if (affectsFiltering)
                RulesChanged?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<ObservedTriple> GetDiscoveredTriplesSnapshot()
        {
            DiscoveryNode[] nodes;
            lock (_gate)
                nodes = _discovery.Values.ToArray();

            return nodes
                .Where(x => x.Site.Length != 0 && x.Talkgroup.Length != 0)
                .Select(x => new ObservedTriple
                {
                    ReceiverValue = x.ReceiverKey,
                    ReceiverLabel = x.Receiver,
                    SiteValue = x.SiteKey,
                    SiteLabel = x.Site,
                    TalkgroupValue = x.TalkgroupKey,
                    TalkgroupLabel = x.Talkgroup,
                    LastSeenUtc = x.LastSeenUtc
                })
                .OrderBy(x => x.ReceiverLabel, NaturalComparer.Instance)
                .ThenBy(x => x.SiteLabel, NaturalComparer.Instance)
                .ThenBy(x => x.TalkgroupLabel, NaturalComparer.Instance)
                .ToList();
        }

        public bool ShouldHide(CallItem call)
        {
            var rule = GetEffectiveState(call);
            return rule != null && rule.IsDisabled;
        }

        public bool ShouldMute(CallItem call)
        {
            var rule = GetEffectiveState(call);
            if (rule == null)
                return false;

            return rule.IsDisabled || rule.IsMuted;
        }

        private void LoadCurrentServerFromStorage()
        {
            _discovery.Clear();
            _discoveredRules.Clear();
            _state.Clear();

            var discoveryKey = GetDiscoveryStorageKey();
            var discovery = _pendingWrites.GetValueOrDefault(discoveryKey) as DiscoveryNode[]
                ?? LoadDiscovery(discoveryKey);

            foreach (var node in discovery)
            {
                if (!IsDiscoveryValid(node.Receiver, node.Site, node.Talkgroup))
                    continue;

                UpsertDiscovery_NoLock(new DiscoveryNode
                {
                    ReceiverKey = Normalize(string.IsNullOrWhiteSpace(node.ReceiverKey) ? node.Receiver : node.ReceiverKey),
                    SiteKey = Normalize(string.IsNullOrWhiteSpace(node.SiteKey) ? node.Site : node.SiteKey),
                    TalkgroupKey = Normalize(string.IsNullOrWhiteSpace(node.TalkgroupKey) ? node.Talkgroup : node.TalkgroupKey),
                    Receiver = Normalize(node.Receiver),
                    Site = Normalize(node.Site),
                    Talkgroup = Normalize(node.Talkgroup),
                    LastSeenUtc = node.LastSeenUtc == default ? DateTime.UtcNow : node.LastSeenUtc
                });
            }

            foreach (var state in LoadState())
            {
                if (state == null)
                    continue;

                var receiver = Normalize(state.Receiver);
                var site = Normalize(state.Site);
                var talkgroup = Normalize(state.Talkgroup);
                if (!IsRuleValid(state.Level, receiver, site, talkgroup))
                    continue;

                SaveOrRemoveState_NoLock(new FilterRuleStateRecord
                {
                    Level = state.Level,
                    Receiver = receiver,
                    Site = site,
                    Talkgroup = talkgroup,
                    IsMuted = state.IsMuted,
                    IsDisabled = state.IsDisabled
                });
            }
        }

        private static DiscoveryNode[] LoadDiscovery(string key)
        {
            try
            {
                var json = AppStateStore.GetString(key, string.Empty);
                return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<DiscoveryNode[]>(json) ?? [];
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Filters: failed to read discovery ({ex.GetType().Name}).");
                return [];
            }
        }

        private FilterRuleStateRecord[] LoadState()
        {
            var key = GetStateStorageKey();
            if (_pendingWrites.GetValueOrDefault(key) is FilterRuleStateRecord[] pending)
                return pending;

            try
            {
                var json = AppStateStore.GetString(key, string.Empty);
                return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<FilterRuleStateRecord[]>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }

        private void QueueDiscoveryWrite_NoLock()
        {
            _discoveryDirty = true;
            StartWriter_NoLock();
        }

        private void QueueStateWrite_NoLock()
        {
            _pendingWrites[GetStateStorageKey()] = _state.Values.ToArray();
            StartWriter_NoLock();
        }

        private void StartWriter_NoLock()
        {
            if (_writerRunning)
                return;

            _writerRunning = true;
            _ = Task.Run(WritePendingFilters);
        }

        private void CapturePendingDiscovery_NoLock()
        {
            if (!_discoveryDirty)
                return;

            _pendingWrites[GetDiscoveryStorageKey()] = _discovery.Values.ToArray();
            _discoveryDirty = false;
        }

        private void WritePendingFilters()
        {
            while (true)
            {
                KeyValuePair<string, object> write;
                lock (_gate)
                {
                    CapturePendingDiscovery_NoLock();
                    if (_pendingWrites.Count == 0)
                    {
                        _writerRunning = false;
                        return;
                    }

                    // Persist explicit user edits before background discovery during busy streams.
                    write = _pendingWrites.FirstOrDefault(entry => entry.Value is FilterRuleStateRecord[]);
                    if (write.Key == null)
                        write = _pendingWrites.First();
                }

                try
                {
                    var started = Stopwatch.GetTimestamp();
                    AppStateStore.SetString(write.Key, JsonSerializer.Serialize(write.Value));
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    if (elapsed.TotalMilliseconds >= 100)
                        AppLog.Add(() => $"Filters: storage write took {elapsed.TotalMilliseconds:F0} ms, rows={((Array)write.Value).Length}.");
                }
                catch (Exception ex)
                {
                    AppLog.Add(() => $"Filters: storage write failed ({ex.GetType().Name}).");
                }

                lock (_gate)
                {
                    // Keep in-flight snapshots available if the user switches away and back.
                    // A newer snapshot for the same server must remain queued.
                    if (_pendingWrites.TryGetValue(write.Key, out var pending) && ReferenceEquals(pending, write.Value))
                        _pendingWrites.Remove(write.Key);
                }
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

        public IReadOnlyList<FilterCatalogRow> GetCatalogSnapshot()
        {
            DiscoveryNode[] nodes;
            Dictionary<string, FilterRuleStateRecord> states;
            lock (_gate)
            {
                nodes = _discovery.Values.ToArray();
                states = new Dictionary<string, FilterRuleStateRecord>(_state, StringComparer.OrdinalIgnoreCase);
            }

            FilterRuleStateRecord? FindState(FilterLevel level, string receiver, string site, string talkgroup) =>
                states.GetValueOrDefault(FilterRuleStateRecord.BuildKey(level, receiver, site, talkgroup));

            var receivers = nodes
                .GroupBy(x => x.ReceiverKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.LastSeenUtc)
                    .ThenByDescending(x => x.Receiver.Length)
                    .ThenBy(x => x.Receiver, NaturalComparer.Instance).First())
                .OrderBy(x => x.Receiver, NaturalComparer.Instance);

            var siteNodes = nodes.Where(x => x.Site.Length != 0).ToArray();
            var sitesByReceiver = siteNodes.GroupBy(x => (x.ReceiverKey, x.SiteKey), StringTupleComparer.Instance)
                .Select(g => g.OrderByDescending(x => x.LastSeenUtc)
                    .ThenByDescending(x => x.Site.Length)
                    .ThenBy(x => x.Site, NaturalComparer.Instance).First())
                .OrderBy(x => x.Site, NaturalComparer.Instance)
                .ToLookup(x => x.ReceiverKey, StringComparer.OrdinalIgnoreCase);

            var talkgroupsBySite = siteNodes.Where(x => x.Talkgroup.Length != 0)
                .OrderBy(x => x.Talkgroup, NaturalComparer.Instance)
                .ToLookup(x => (x.ReceiverKey, x.SiteKey), StringTupleComparer.Instance);

            var rules = new List<FilterCatalogRow>(nodes.Length);
            foreach (var receiver in receivers)
            {
                var receiverState = FindState(FilterLevel.Receiver, receiver.Receiver, string.Empty, string.Empty);
                rules.Add(CreateRule(FilterLevel.Receiver, receiver, receiverState));

                foreach (var site in sitesByReceiver[receiver.ReceiverKey])
                {
                    var siteState = FindState(FilterLevel.Site, site.Receiver, site.Site, string.Empty) ?? receiverState;
                    rules.Add(CreateRule(FilterLevel.Site, site, siteState));

                    foreach (var talkgroup in talkgroupsBySite[(site.ReceiverKey, site.SiteKey)])
                    {
                        var state = FindState(FilterLevel.Talkgroup, talkgroup.Receiver, talkgroup.Site, talkgroup.Talkgroup) ?? siteState;
                        rules.Add(CreateRule(FilterLevel.Talkgroup, talkgroup, state));
                    }
                }
            }

            return rules;
        }

        private static FilterCatalogRow CreateRule(FilterLevel level, DiscoveryNode node, FilterRuleStateRecord? state) =>
            new(level, node.Receiver, level == FilterLevel.Receiver ? string.Empty : node.Site,
                level == FilterLevel.Talkgroup ? node.Talkgroup : string.Empty, state?.IsMuted ?? false,
                state?.IsDisabled ?? false, node.LastSeenUtc);

        private FilterRuleStateRecord? GetEffectiveState(CallItem call)
        {
            if (call == null)
                return null;

            var receiver = Normalize(call.ReceiverName);
            if (receiver.Length == 0)
                return null;

            lock (_gate)
                return GetEffectiveState_NoLock(receiver, Normalize(call.SystemName), Normalize(call.Talkgroup));
        }

        private FilterRuleStateRecord? GetEffectiveState_NoLock(string receiver, string site, string talkgroup)
        {
            return FindDiscoveredState_NoLock(FilterLevel.Talkgroup, receiver, site, talkgroup) ??
                FindDiscoveredState_NoLock(FilterLevel.Site, receiver, site, string.Empty) ??
                FindDiscoveredState_NoLock(FilterLevel.Receiver, receiver, string.Empty, string.Empty);
        }

        private FilterRuleStateRecord? FindDiscoveredState_NoLock(FilterLevel level, string receiver, string site, string talkgroup)
        {
            var key = FilterRuleStateRecord.BuildKey(level, receiver, site, talkgroup);
            return _discoveredRules.Contains(key) ? _state.GetValueOrDefault(key) : null;
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
                var updated = existing with
                {
                    Receiver = PreferLabel(node.Receiver, existing.Receiver) ? node.Receiver : existing.Receiver,
                    Site = PreferLabel(node.Site, existing.Site) ? node.Site : existing.Site,
                    Talkgroup = PreferLabel(node.Talkgroup, existing.Talkgroup) ? node.Talkgroup : existing.Talkgroup,
                    LastSeenUtc = node.LastSeenUtc > existing.LastSeenUtc ? node.LastSeenUtc : existing.LastSeenUtc
                };
                if (updated == existing)
                    return DiscoveryUpdateKind.None;

                _discovery[key] = updated;
                return updated.Receiver != existing.Receiver || updated.Site != existing.Site || updated.Talkgroup != existing.Talkgroup
                    ? DiscoveryUpdateKind.Catalog
                    : DiscoveryUpdateKind.MetadataOnly;
            }

            _discovery[key] = node;
            _discoveredRules.Add(FilterRuleStateRecord.BuildKey(FilterLevel.Receiver, node.Receiver, string.Empty, string.Empty));
            if (node.Site.Length != 0)
                _discoveredRules.Add(FilterRuleStateRecord.BuildKey(FilterLevel.Site, node.Receiver, node.Site, string.Empty));
            if (node.Talkgroup.Length != 0)
                _discoveredRules.Add(FilterRuleStateRecord.BuildKey(FilterLevel.Talkgroup, node.Receiver, node.Site, node.Talkgroup));
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

        private sealed record DiscoveryNode
        {
            public string ReceiverKey { get; init; } = string.Empty;
            public string SiteKey { get; init; } = string.Empty;
            public string TalkgroupKey { get; init; } = string.Empty;
            public string Receiver { get; init; } = string.Empty;
            public string Site { get; init; } = string.Empty;
            public string Talkgroup { get; init; } = string.Empty;
            public DateTime LastSeenUtc { get; init; }
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
