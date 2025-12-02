using JoesScanner.Models;
using Microsoft.Maui.Storage;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;

namespace JoesScanner.Services
{
    // Central filter engine used by both the main page and settings.
    // Owns the rules list, persists it, and answers filter decisions.
    public sealed class FilterService
    {
        private const string FiltersPreferenceKey = "FilterRulesV1";

        // Raised whenever rules are added, removed, or changed (mute/disable).
        public event EventHandler? RulesChanged;

        // In-memory collection of filter rules that the UI binds to.
        private readonly ObservableCollection<FilterRule> _rules = new();
        public ObservableCollection<FilterRule> Rules => _rules;

        // Lazy singleton instance so the same filter state is shared across the app.
        private static readonly Lazy<FilterService> _instance =
            new Lazy<FilterService>(() => new FilterService());

        public static FilterService Instance => _instance.Value;

        // Private constructor to enforce singleton usage and load persisted rules.
        private FilterService()
        {
            LoadFromPreferences();
        }

        // Toggles the mute state for the given rule, applying the change to
        // related rules depending on the rule level (Receiver/Site/Talkgroup).
        public void ToggleMute(FilterRule rule)
        {
            if (rule == null)
                return;

            var newMuted = !rule.IsMuted;
            var normReceiver = Normalize(rule.Receiver);
            var normSite = Normalize(rule.Site);

            // Receiver level: apply to all rules for that receiver.
            if (rule.Level == FilterLevel.Receiver)
            {
                foreach (var r in _rules)
                {
                    if (string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase))
                    {
                        r.IsMuted = newMuted;
                    }
                }
            }
            // Site level: apply to all rules with same receiver + site.
            else if (rule.Level == FilterLevel.Site)
            {
                foreach (var r in _rules)
                {
                    if (string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Normalize(r.Site), normSite, StringComparison.OrdinalIgnoreCase))
                    {
                        r.IsMuted = newMuted;
                    }
                }
            }
            // Talkgroup level: only this row.
            else
            {
                rule.IsMuted = newMuted;
            }

            SaveToPreferences();
            OnRulesChanged();
        }

        // Toggles the disabled state for the given rule, applying the change to
        // related rules depending on the rule level (Receiver/Site/Talkgroup).
        public void ToggleDisable(FilterRule rule)
        {
            if (rule == null)
                return;

            var newDisabled = !rule.IsDisabled;
            var normReceiver = Normalize(rule.Receiver);
            var normSite = Normalize(rule.Site);

            // Receiver level: apply to all rules for that receiver.
            if (rule.Level == FilterLevel.Receiver)
            {
                foreach (var r in _rules)
                {
                    if (string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase))
                    {
                        r.IsDisabled = newDisabled;
                    }
                }
            }
            // Site level: apply to all rules with same receiver + site.
            else if (rule.Level == FilterLevel.Site)
            {
                foreach (var r in _rules)
                {
                    if (string.Equals(Normalize(r.Receiver), normReceiver, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Normalize(r.Site), normSite, StringComparison.OrdinalIgnoreCase))
                    {
                        r.IsDisabled = newDisabled;
                    }
                }
            }
            // Talkgroup level: only this row.
            else
            {
                rule.IsDisabled = newDisabled;
            }

            SaveToPreferences();
            OnRulesChanged();
        }

        // Ensures Receiver, Site, and Talkgroup rules exist for a call and updates LastSeen.
        // Also keeps the Rules collection sorted by DisplayKey.
        public void EnsureRulesForCall(CallItem call)
        {
            if (call == null)
                return;

            // Do not create filter rules for error entries
            if (IsErrorCall(call))
                return;

            var receiver = Normalize(call.ReceiverName);
            var site = Normalize(call.SystemName);
            var talkgroup = Normalize(call.Talkgroup);

            var nowUtc = DateTime.UtcNow;

            EnsureRule(FilterLevel.Receiver, receiver, string.Empty, string.Empty, nowUtc);
            EnsureRule(FilterLevel.Site, receiver, site, string.Empty, nowUtc);
            EnsureRule(FilterLevel.Talkgroup, receiver, site, talkgroup, nowUtc);
        }

        // Returns true when this call should be dropped completely
        // (not shown and not heard) due to any matching disabled rule.
        public bool ShouldHide(CallItem call)
        {
            return GetEffectiveRulesForCall(call).Any(r => r.IsDisabled);
        }

        // Returns true when audio for this call should be muted due to filters.
        // Disabled implies muted.
        public bool ShouldMute(CallItem call)
        {
            var rules = GetEffectiveRulesForCall(call).ToList();

            if (rules.Any(r => r.IsDisabled))
                return true;

            if (rules.Any(r => r.IsMuted))
                return true;

            return false;
        }

        // Clears a rule from the list. New traffic will recreate it as needed.
        public void ClearRule(FilterRule rule)
        {
            if (rule == null)
                return;

            if (_rules.Remove(rule))
            {
                SaveToPreferences();
                OnRulesChanged();
            }
        }

        // Raises the RulesChanged event to notify listeners of rule updates.
        private void OnRulesChanged()
        {
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }

        // Normalizes string values used as keys for matching rules.
        private static string Normalize(string? value) =>
            (value ?? string.Empty).Trim();

        // Returns true if this call looks like an error line rather than real traffic.
        // We key off the fact that Receiver and Site are empty and the talkgroup text
        // contains ERROR (for example ">> ERROR").
        private static bool IsErrorCall(CallItem call)
        {
            if (call == null)
                return false;

            var receiverEmpty = string.IsNullOrWhiteSpace(call.ReceiverName);
            var siteEmpty = string.IsNullOrWhiteSpace(call.SystemName);
            var tgRaw = call.Talkgroup ?? string.Empty;
            var talkgroup = tgRaw.Trim();

            if (!receiverEmpty || !siteEmpty)
                return false;

            if (talkgroup.Length == 0)
                return false;

            var upper = talkgroup.ToUpperInvariant();

            // Handles "ERROR", ">> ERROR", and similar variants
            if (upper == "ERROR")
                return true;

            if (upper == ">> ERROR")
                return true;

            if (upper.Contains(" ERROR"))
                return true;

            return false;
        }

        // Ensures a specific rule exists for the given receiver/site/talkgroup and level.
        // Updates LastSeenUtc for existing rules, inserts new ones in sorted order.
        private void EnsureRule(
            FilterLevel level,
            string receiver,
            string site,
            string talkgroup,
            DateTime nowUtc)
        {
            var normReceiver = Normalize(receiver);
            var normSite = Normalize(site);
            var normTalkgroup = Normalize(talkgroup);

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

            var rule = new FilterRule
            {
                Level = level,
                Receiver = receiver,
                Site = site,
                Talkgroup = talkgroup,
                LastSeenUtc = nowUtc
            };

            rule.PropertyChanged += OnRulePropertyChanged;

            InsertRuleSorted(rule);

            SaveToPreferences();
            OnRulesChanged();
        }

        // Inserts a rule into the collection keeping rules sorted by DisplayKey.
        private void InsertRuleSorted(FilterRule rule)
        {
            var key = rule.DisplayKey ?? string.Empty;

            if (_rules.Count == 0)
            {
                _rules.Add(rule);
                return;
            }

            for (int i = 0; i < _rules.Count; i++)
            {
                var existingKey = _rules[i].DisplayKey ?? string.Empty;
                if (string.Compare(key, existingKey, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _rules.Insert(i, rule);
                    return;
                }
            }

            _rules.Add(rule);
        }

        // Persists changes for rules that raise PropertyChanged (mute/disable, etc.).
        private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Any change to IsMuted / IsDisabled or other fields should be saved.
            if (sender is FilterRule)
            {
                SaveToPreferences();
                OnRulesChanged();
            }
        }

        // Returns all rules that apply to the given call, across receiver/site/talkgroup levels.
        private System.Collections.Generic.IEnumerable<FilterRule> GetEffectiveRulesForCall(CallItem call)
        {
            if (call == null)
                yield break;

            var receiver = Normalize(call.ReceiverName);
            var site = Normalize(call.SystemName);
            var talkgroup = Normalize(call.Talkgroup);

            foreach (var rule in _rules)
            {
                if (!string.Equals(Normalize(rule.Receiver), receiver, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (rule.Level == FilterLevel.Receiver)
                {
                    yield return rule;
                    continue;
                }

                if (rule.Level == FilterLevel.Site)
                {
                    if (string.Equals(Normalize(rule.Site), site, StringComparison.OrdinalIgnoreCase))
                        yield return rule;

                    continue;
                }

                if (rule.Level == FilterLevel.Talkgroup)
                {
                    if (string.Equals(Normalize(rule.Site), site, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Normalize(rule.Talkgroup), talkgroup, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return rule;
                    }
                }
            }
        }

        // DTO used to serialize and deserialize filter rules to preferences.
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

        // Loads persisted rules from preferences and rebuilds the in-memory collection.
        private void LoadFromPreferences()
        {
            try
            {
                var json = Preferences.Get(FiltersPreferenceKey, string.Empty);
                if (string.IsNullOrWhiteSpace(json))
                    return;

                var dtoList = JsonSerializer.Deserialize<System.Collections.Generic.List<FilterRuleDto>>(json);
                if (dtoList == null)
                    return;

                _rules.Clear();

                foreach (var dto in dtoList)
                {
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
                    InsertRuleSorted(rule);
                }
            }
            catch
            {
                // Ignore corrupt filter data and start fresh.
                _rules.Clear();
            }
        }

        // Saves the current rules collection to preferences as JSON.
        private void SaveToPreferences()
        {
            try
            {
                var dtoList = _rules.Select(r => new FilterRuleDto
                {
                    Level = r.Level,
                    Receiver = r.Receiver,
                    Site = r.Site,
                    Talkgroup = r.Talkgroup,
                    IsMuted = r.IsMuted,
                    IsDisabled = r.IsDisabled,
                    LastSeenUtc = r.LastSeenUtc
                }).ToList();

                var json = JsonSerializer.Serialize(dtoList);
                Preferences.Set(FiltersPreferenceKey, json);
            }
            catch
            {
                // Do not crash if preferences cannot be saved.
            }
        }
    }
}
