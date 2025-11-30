using JoesScanner.Models;
using Microsoft.Maui.Storage;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;

namespace JoesScanner.Services
{
    /// <summary>
    /// Central filter engine used by both the main page and settings.
    /// Owns the rules list, persists it, and answers filter decisions.
    /// </summary>
    public sealed class FilterService
    {
        private const string FiltersPreferenceKey = "FilterRulesV1";

        // Raised whenever rules are added, removed, or changed (mute/disable).
        public event EventHandler? RulesChanged;

        private readonly ObservableCollection<FilterRule> _rules = new();
        public ObservableCollection<FilterRule> Rules => _rules;

        private static readonly Lazy<FilterService> _instance =
            new Lazy<FilterService>(() => new FilterService());

        public static FilterService Instance => _instance.Value;

        private FilterService()
        {
            LoadFromPreferences();
        }

        public void ToggleMute(FilterRule rule)
        {
            if (rule == null)
                return;

            var newMuted = !rule.IsMuted;
            var normReceiver = Normalize(rule.Receiver);
            var normSite = Normalize(rule.Site);

            // Receiver level: apply to all rules for that receiver
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
            // Site level: apply to all rules with same receiver + site
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
            // Talkgroup level: only this row
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

            // Receiver level: apply to all rules for that receiver
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
            // Site level: apply to all rules with same receiver + site
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
            // Talkgroup level: only this row
            else
            {
                rule.IsDisabled = newDisabled;
            }

            SaveToPreferences();
            OnRulesChanged();
        }

        /// <summary>
        /// Ensures Receiver, Site, and Talkgroup rules exist for a call and updates LastSeen.
        /// Also keeps the Rules collection sorted by DisplayKey.
        /// </summary>
        public void EnsureRulesForCall(CallItem call)
        {
            if (call == null)
                return;

            var receiver = Normalize(call.ReceiverName);
            var site = Normalize(call.SystemName);
            var talkgroup = Normalize(call.Talkgroup);

            var nowUtc = DateTime.UtcNow;

            EnsureRule(FilterLevel.Receiver, receiver, string.Empty, string.Empty, nowUtc);
            EnsureRule(FilterLevel.Site, receiver, site, string.Empty, nowUtc);
            EnsureRule(FilterLevel.Talkgroup, receiver, site, talkgroup, nowUtc);
        }

        /// <summary>
        /// Returns true when this call should be dropped completely
        /// (not shown and not heard) due to any matching disabled rule.
        /// </summary>
        public bool ShouldHide(CallItem call)
        {
            return GetEffectiveRulesForCall(call).Any(r => r.IsDisabled);
        }

        /// <summary>
        /// Returns true when audio for this call should be muted due to filters.
        /// Disabled implies muted.
        /// </summary>
        public bool ShouldMute(CallItem call)
        {
            var rules = GetEffectiveRulesForCall(call).ToList();

            if (rules.Any(r => r.IsDisabled))
                return true;

            if (rules.Any(r => r.IsMuted))
                return true;

            return false;
        }

        /// <summary>
        /// Clears a rule from the list. New traffic will recreate it.
        /// </summary>
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

        private void OnRulesChanged()
        {
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string Normalize(string? value) =>
            (value ?? string.Empty).Trim();

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

        private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Any change to IsMuted / IsDisabled or other fields should be saved.
            if (sender is FilterRule)
            {
                SaveToPreferences();
                OnRulesChanged();
            }
        }

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
                // Ignore corrupt filter data, start fresh.
                _rules.Clear();
            }
        }

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
