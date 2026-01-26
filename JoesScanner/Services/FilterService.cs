using JoesScanner.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;

namespace JoesScanner.Services
{
    // Central filter engine used by both the main page and settings.
    // Owns the rules list, persists it, and answers filter decisions.
    internal sealed class FilterService
    {
        private const string FiltersPreferenceKey = "FilterRulesV1";

        // Windows LocalSettings has a relatively small per-value limit.
        // Store the rule payload in a file when it is large or when running on Windows.
        private const string FiltersFileName = "filter_rules_v1.json";
        private const string FiltersStoredInFileMarker = "FILE";
        private const int MaxPreferenceChars = 6000;
        public event EventHandler? RulesChanged;

        private readonly ObservableCollection<FilterRule> _rules = [];
        public ObservableCollection<FilterRule> Rules => _rules;

        public List<FilterRuleStateRecord> GetActiveStateRecords()
        {
            var list = new List<FilterRuleStateRecord>();

            lock (_rulesGate)
            {
                foreach (var r in _rules)
                {
                    if (!r.IsMuted && !r.IsDisabled)
                        continue;

                    list.Add(new FilterRuleStateRecord
                    {
                        Level = r.Level,
                        Receiver = r.Receiver,
                        Site = r.Site,
                        Talkgroup = r.Talkgroup,
                        IsMuted = r.IsMuted,
                        IsDisabled = r.IsDisabled
                    });
                }
            }

            return list;
        }

        public void ApplyStateRecords(IEnumerable<FilterRuleStateRecord> records, bool resetOthers)
        {
            var incoming = records?.ToList() ?? new List<FilterRuleStateRecord>();

            lock (_rulesGate)
            {
                if (resetOthers)
                {
                    foreach (var r in _rules)
                    {
                        r.IsMuted = false;
                        r.IsDisabled = false;
                    }
                }

                foreach (var rec in incoming)
                {
                    if (rec == null)
                        continue;

                    var receiver = Normalize(rec.Receiver);
                    var site = Normalize(rec.Site);
                    var talkgroup = Normalize(rec.Talkgroup);

                    if (!IsRuleValid(rec.Level, receiver, site, talkgroup))
                        continue;

                    var match = _rules.FirstOrDefault(r =>
                        r.Level == rec.Level &&
                        string.Equals(Normalize(r.Receiver), receiver, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Normalize(r.Site), site, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Normalize(r.Talkgroup), talkgroup, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        match = new FilterRule
                        {
                            Level = rec.Level,
                            Receiver = receiver,
                            Site = site,
                            Talkgroup = talkgroup,
                            LastSeenUtc = DateTime.UtcNow
                        };

                        match.PropertyChanged += OnRulePropertyChanged;
                        InsertRuleSorted_NoYield(match);
                    }

                    match.IsMuted = rec.IsMuted;
                    match.IsDisabled = rec.IsDisabled;
                }
            }

            SaveToPreferences();
            OnRulesChanged();
        }

        // Use System.Threading.Lock to satisfy IDE0330 and to guarantee safe snapshots.
        private readonly Lock _rulesGate = new();

        private static readonly Lazy<FilterService> _instance = new(static () => new FilterService());
        public static FilterService Instance => _instance.Value;

        private FilterService()
        {
            LoadFromPreferences();
            PruneInvalidRules(saveIfChanged: true);
        }

        public void ToggleMute(FilterRule rule)
        {
            if (rule == null)
                return;

            var newMuted = !rule.IsMuted;
            var normReceiver = Normalize(rule.Receiver);
            var normSite = Normalize(rule.Site);

            var snapshot = GetRulesSnapshot();

            if (rule.Level == FilterLevel.Receiver)
            {
                foreach (var r in snapshot)
                {
                    if (string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase))
                        r.IsMuted = newMuted;
                }
            }
            else if (rule.Level == FilterLevel.Site)
            {
                foreach (var r in snapshot)
                {
                    if (string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Normalize(r.Site), normSite, StringComparison.OrdinalIgnoreCase))
                    {
                        r.IsMuted = newMuted;
                    }
                }
            }
            else
            {
                rule.IsMuted = newMuted;
            }

            SaveToPreferences();
            OnRulesChanged();
        }

        public void ToggleDisable(FilterRule rule)
        {
            if (rule == null)
                return;

            var newDisabled = !rule.IsDisabled;
            var normReceiver = Normalize(rule.Receiver);
            var normSite = Normalize(rule.Site);

            var snapshot = GetRulesSnapshot();

            if (rule.Level == FilterLevel.Receiver)
            {
                foreach (var r in snapshot)
                {
                    if (string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase))
                        r.IsDisabled = newDisabled;
                }
            }
            else if (rule.Level == FilterLevel.Site)
            {
                foreach (var r in snapshot)
                {
                    if (string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Normalize(r.Site), normSite, StringComparison.OrdinalIgnoreCase))
                    {
                        r.IsDisabled = newDisabled;
                    }
                }
            }
            else
            {
                rule.IsDisabled = newDisabled;
            }

            SaveToPreferences();
            OnRulesChanged();
        }

        public void ClearRule(FilterRule rule)
        {
            if (rule == null)
                return;

            var removed = false;

            lock (_rulesGate)
            {
                removed = _rules.Remove(rule);
            }

            if (removed)
            {
                SaveToPreferences();
                OnRulesChanged();
            }
        }

        public void EnsureRulesForCall(CallItem call)
        {
            if (call == null)
                return;

            if (IsErrorCall(call) || IsMetaOrNonTrafficCall(call))
                return;

            var receiver = Normalize(call.ReceiverName);
            var site = Normalize(call.SystemName);
            var talkgroup = Normalize(call.Talkgroup);

            if (string.IsNullOrWhiteSpace(receiver) || string.IsNullOrWhiteSpace(site))
                return;

            var nowUtc = DateTime.UtcNow;

            EnsureRule(FilterLevel.Receiver, receiver, string.Empty, string.Empty, nowUtc);
            EnsureRule(FilterLevel.Site, receiver, site, string.Empty, nowUtc);

            if (!string.IsNullOrWhiteSpace(talkgroup) && !IsInvalidTalkgroupToken(talkgroup))
                EnsureRule(FilterLevel.Talkgroup, receiver, site, talkgroup, nowUtc);
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

            if (rule.IsDisabled)
                return true;

            return rule.IsMuted;
        }

        private void OnRulesChanged() => RulesChanged?.Invoke(this, EventArgs.Empty);

        private static string Normalize(string? value) => (value ?? string.Empty).Trim();

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

            return upper == "ERROR"
                || upper == ">> ERROR"
                || upper.Contains(" ERROR");
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

            var stripped = talkgroup.Replace(">", string.Empty).Trim();
            return stripped.Length == 0;
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

            if (upper == "ERROR" || upper == ">> ERROR" || upper.Contains(" ERROR"))
                return false;

            if (upper == "HEARTBEAT" || upper == ">> HEARTBEAT")
                return false;

            if (upper.StartsWith(">>", StringComparison.Ordinal))
                return false;

            return true;
        }

        private void EnsureRule(FilterLevel level, string receiver, string site, string talkgroup, DateTime nowUtc)
        {
            var normReceiver = Normalize(receiver);
            var normSite = Normalize(site);
            var normTalkgroup = Normalize(talkgroup);

            if (!IsRuleValid(level, normReceiver, normSite, normTalkgroup))
                return;

            var added = false;

            lock (_rulesGate)
            {
                var existing = _rules.FirstOrDefault(r =>
                    r.Level == level &&
                    string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Normalize(r.Site), normSite, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Normalize(r.Talkgroup), normTalkgroup, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.LastSeenUtc = nowUtc;
                    return;
                }

                // New rules must inherit the current receiver or site state.
                // This ensures that new talkgroups discovered after a higher level mute or disable
                // remain muted or disabled until explicitly overridden.
                var inheritedMuted = false;
                var inheritedDisabled = false;

                if (level == FilterLevel.Site)
                {
                    var receiverRule = _rules.FirstOrDefault(r =>
                        r.Level == FilterLevel.Receiver &&
                        string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase));

                    if (receiverRule != null)
                    {
                        inheritedMuted = receiverRule.IsMuted;
                        inheritedDisabled = receiverRule.IsDisabled;
                    }
                }
                else if (level == FilterLevel.Talkgroup)
                {
                    var siteRule = _rules.FirstOrDefault(r =>
                        r.Level == FilterLevel.Site &&
                        string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Normalize(r.Site), normSite, StringComparison.OrdinalIgnoreCase));

                    if (siteRule != null)
                    {
                        inheritedMuted = siteRule.IsMuted;
                        inheritedDisabled = siteRule.IsDisabled;
                    }
                    else
                    {
                        var receiverRule = _rules.FirstOrDefault(r =>
                            r.Level == FilterLevel.Receiver &&
                            string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase));

                        if (receiverRule != null)
                        {
                            inheritedMuted = receiverRule.IsMuted;
                            inheritedDisabled = receiverRule.IsDisabled;
                        }
                    }
                }

                var rule = new FilterRule
                {
                    Level = level,
                    Receiver = receiver,
                    Site = site,
                    Talkgroup = talkgroup,
                    IsMuted = inheritedMuted,
                    IsDisabled = inheritedDisabled,
                    LastSeenUtc = nowUtc
                };

                rule.PropertyChanged += OnRulePropertyChanged;

                InsertRuleSorted_NoYield(rule);
                added = true;
            }

            if (added)
            {
                SaveToPreferences();
                OnRulesChanged();
            }
        }

        // Caller must hold _rulesGate.
        private void InsertRuleSorted_NoYield(FilterRule rule)
        {
            var key = rule.DisplayKey ?? string.Empty;

            for (int i = 0; i < _rules.Count; i++)
            {
                var existingKey = _rules[i].DisplayKey ?? string.Empty;

                // Natural sort so "2" comes before "10"
                if (NaturalCompareIgnoreCase(key, existingKey) < 0)
                {
                    _rules.Insert(i, rule);
                    return;
                }
            }

            _rules.Add(rule);
        }

        private static int NaturalCompareIgnoreCase(string a, string b)
        {
            if (ReferenceEquals(a, b))
                return 0;

            a ??= string.Empty;
            b ??= string.Empty;

            int ia = 0, ib = 0;

            while (ia < a.Length && ib < b.Length)
            {
                var ca = a[ia];
                var cb = b[ib];

                var da = char.IsDigit(ca);
                var db = char.IsDigit(cb);

                if (da && db)
                {
                    // Compare numeric runs by value without allocating
                    int startA = ia;
                    int startB = ib;

                    // Skip leading zeros for value comparison
                    while (startA < a.Length && a[startA] == '0') startA++;
                    while (startB < b.Length && b[startB] == '0') startB++;

                    int endA = startA;
                    int endB = startB;

                    while (endA < a.Length && char.IsDigit(a[endA])) endA++;
                    while (endB < b.Length && char.IsDigit(b[endB])) endB++;

                    int lenA = endA - startA;
                    int lenB = endB - startB;

                    // Longer numeric value wins
                    if (lenA != lenB)
                        return lenA < lenB ? -1 : 1;

                    // Same length, compare digit by digit
                    for (int k = 0; k < lenA; k++)
                    {
                        char x = a[startA + k];
                        char y = b[startB + k];
                        if (x != y)
                            return x < y ? -1 : 1;
                    }

                    // Values equal, fewer leading zeros sorts first
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

                if (da != db)
                {
                    // Put digit chunks before letter chunks
                    return da ? -1 : 1;
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


        private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is FilterRule)
            {
                SaveToPreferences();
                OnRulesChanged();
            }
        }

        // Returns a single rule so callers can apply "most specific wins" semantics.
        // This enables explicit overrides at lower levels, for example unmuting a talkgroup
        // even while its receiver or site remains muted.
        private FilterRule? GetBestMatchRuleForCall(CallItem call)
        {
            if (call == null)
                return null;

            var receiver = Normalize(call.ReceiverName);
            if (string.IsNullOrWhiteSpace(receiver))
                return null;

            var site = Normalize(call.SystemName);
            var talkgroup = Normalize(call.Talkgroup);

            // Snapshot so nothing here can trigger CsWinRT1030.
            var snapshot = GetRulesSnapshot();
            if (snapshot.Length == 0)
                return null;

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
                    if (!string.IsNullOrWhiteSpace(site) &&
                        string.Equals(Normalize(rule.Site), site, StringComparison.OrdinalIgnoreCase))
                    {
                        siteRule = rule;
                    }

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

        private FilterRule[] GetRulesSnapshot()
        {
            lock (_rulesGate)
            {
                return _rules.ToArray();
            }
        }

        private sealed class FilterRuleDto
        {
            public FilterLevel Level { get; set; }
            public string Receiver { get; set; } = string.Empty;
            public string Site { get; set; } = string.Empty;
            public string Talkgroup { get; set; } = string.Empty;
            public bool IsMuted { get; set; }
            public bool IsDisabled { get; set; }
            public DateTime LastSeenUtc { get; set; }
        }

        private static string GetFiltersFilePath()
        {
            try
            {
                var dir = FileSystem.AppDataDirectory;
                return Path.Combine(dir, FiltersFileName);
            }
            catch
            {
                return FiltersFileName;
            }
        }

        private static string? TryReadFiltersFile()
        {
            try
            {
                var path = GetFiltersFilePath();
                if (!File.Exists(path))
                    return null;

                return File.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }

        private static void TryWriteFiltersFile(string json)
        {
            try
            {
                var path = GetFiltersFilePath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }

        private void LoadFromPreferences()
        {
            try
            {
                string json = string.Empty;

                // Prefer the file when present.
                var filePath = GetFiltersFilePath();
                if (File.Exists(filePath))
                {
                    json = File.ReadAllText(filePath);
                }
                else
                {
                    json = Preferences.Get(FiltersPreferenceKey, string.Empty);
                    if (string.Equals(json, FiltersStoredInFileMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(filePath))
                            json = File.ReadAllText(filePath);
                        else
                            json = string.Empty;
                    }
                }

                if (string.IsNullOrWhiteSpace(json))
                    return;

                var dtoList = JsonSerializer.Deserialize<List<FilterRuleDto>>(json);
                if (dtoList == null)
                    return;

                lock (_rulesGate)
                {
                    _rules.Clear();

                    foreach (var dto in dtoList)
                    {
                        var receiver = Normalize(dto.Receiver);
                        var site = Normalize(dto.Site);
                        var tg = Normalize(dto.Talkgroup);

                        if (!IsRuleValid(dto.Level, receiver, site, tg))
                            continue;

                        var rule = new FilterRule
                        {
                            Level = dto.Level,
                            Receiver = dto.Receiver,
                            Site = dto.Site,
                            Talkgroup = dto.Talkgroup,
                            IsMuted = dto.IsMuted,
                            IsDisabled = dto.IsDisabled,
                            LastSeenUtc = dto.LastSeenUtc
                        };

                        rule.PropertyChanged += OnRulePropertyChanged;
                        InsertRuleSorted_NoYield(rule);
                    }
                }
            }
            catch
            {
                lock (_rulesGate)
                {
                    _rules.Clear();
                }
            }
        }

        private void PruneInvalidRules(bool saveIfChanged)
        {
            var changed = false;

            lock (_rulesGate)
            {
                for (int i = _rules.Count - 1; i >= 0; i--)
                {
                    var r = _rules[i];

                    var receiver = Normalize(r.Receiver);
                    var site = Normalize(r.Site);
                    var tg = Normalize(r.Talkgroup);

                    if (!IsRuleValid(r.Level, receiver, site, tg))
                    {
                        _rules.RemoveAt(i);
                        changed = true;
                    }
                }
            }

            if (changed && saveIfChanged)
            {
                SaveToPreferences();
                OnRulesChanged();
            }
        }

        private void SaveToPreferences()
        {
            try
            {
                List<FilterRuleDto> dtoList;

                lock (_rulesGate)
                {
                    dtoList = _rules.Select(r => new FilterRuleDto
                    {
                        Level = r.Level,
                        Receiver = r.Receiver,
                        Site = r.Site,
                        Talkgroup = r.Talkgroup,
                        IsMuted = r.IsMuted,
                        IsDisabled = r.IsDisabled,
                        LastSeenUtc = r.LastSeenUtc
                    }).ToList();
                }

                var json = JsonSerializer.Serialize(dtoList);

                var useFile = OperatingSystem.IsWindows() || json.Length > MaxPreferenceChars;

                if (useFile)
                {
                    var filePath = GetFiltersFilePath();
                    File.WriteAllText(filePath, json);
                    Preferences.Set(FiltersPreferenceKey, FiltersStoredInFileMarker);
                    return;
                }

                Preferences.Set(FiltersPreferenceKey, json);
            }
            catch
            {
                // If we fail to persist to Preferences (for example, Windows size limits),
                // fall back to the file so the app can continue operating normally.
                try
                {
                    List<FilterRuleDto> dtoList;

                    lock (_rulesGate)
                    {
                        dtoList = _rules.Select(r => new FilterRuleDto
                        {
                            Level = r.Level,
                            Receiver = r.Receiver,
                            Site = r.Site,
                            Talkgroup = r.Talkgroup,
                            IsMuted = r.IsMuted,
                            IsDisabled = r.IsDisabled,
                            LastSeenUtc = r.LastSeenUtc
                        }).ToList();
                    }

                    var json = JsonSerializer.Serialize(dtoList);
                    var filePath = GetFiltersFilePath();
                    File.WriteAllText(filePath, json);
                    Preferences.Set(FiltersPreferenceKey, FiltersStoredInFileMarker);
                }
                catch
                {
                }
            }
        }

    }
}
