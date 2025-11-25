using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace JoesScanner.ViewModels
{
    /// <summary>
    /// View model for the Settings page.
    /// Controls connection settings, call list behavior, theme,
    /// and the unified filter list that is populated from live calls.
    /// </summary>
    public class SettingsViewModel : BindableObject
    {
        private readonly ISettingsService _settings;
        private readonly MainViewModel _mainViewModel;
        private readonly HttpClient _httpClient;

        // Connection fields
        private string _serverUrl = string.Empty;
        private bool _useDefaultConnection;

        // Saved snapshot to detect connection changes
        private string _savedServerUrl = string.Empty;
        private bool _savedUseDefaultConnection;
        private bool _hasChanges;

        // Call display settings
        private int _maxCalls;
        private bool _scrollNewestAtBottom;

        // Theme as a single string: "System", "Light", "Dark"
        private string _themeMode = "System";

        // Unified filter list backing storage (shared static so MainViewModel can update it)
        private static readonly ObservableCollection<FilterLine> _filterLines = new();

        // Disabled entries persisted in settings, keyed as "receiver|site|talkgroup"
        private static readonly HashSet<string> _disabledKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track the "current" instance for static helpers
        public static SettingsViewModel? Current { get; private set; }

        // Sort order for the filter list (shared across instances)
        private static bool _sortAscending = true;

        // Commands
        public ICommand SaveCommand { get; }
        public ICommand ResetServerCommand { get; }
        public ICommand ValidateServerCommand { get; }

        // Filter commands
        public ICommand ToggleFilterLineCommand { get; }
        public ICommand ToggleReceiverFilterCommand { get; }
        public ICommand ToggleSiteFilterCommand { get; }
        public ICommand RemoveFilterLineCommand { get; }

        public const string DefaultServerUrl = "https://app.joesscanner.com";

        /// <summary>
        /// A single receiver/site/talkgroup line in the filter list.
        /// Clicking the row toggles IsEnabled.
        /// Entire line is green when enabled, red when disabled.
        /// </summary>
        public sealed class FilterLine : BindableObject
        {
            private string _receiver = string.Empty;
            private string _site = string.Empty;
            private string _talkgroup = string.Empty;
            private bool _isEnabled = true;

            public string Receiver
            {
                get => _receiver;
                set
                {
                    if (_receiver == value)
                        return;
                    _receiver = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }

            public string Site
            {
                get => _site;
                set
                {
                    if (_site == value)
                        return;
                    _site = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }

            public string Talkgroup
            {
                get => _talkgroup;
                set
                {
                    if (_talkgroup == value)
                        return;
                    _talkgroup = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }

            /// <summary>
            /// True when this line is allowed.
            /// Used by the UI to color green or red.
            /// </summary>
            public bool IsEnabled
            {
                get => _isEnabled;
                set
                {
                    if (_isEnabled == value)
                        return;
                    _isEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public SettingsViewModel(ISettingsService settingsService, MainViewModel mainViewModel)
        {
            _settings = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
            _httpClient = new HttpClient();

            Current = this;

            // Seed state from persisted settings
            InitializeFromSettings();

            // Commands
            SaveCommand = new Command(SaveSettings);
            ResetServerCommand = new Command(ResetServerUrl);
            ValidateServerCommand = new Command(async () => await ValidateServerUrlAsync());

            // Per line toggle (talkgroup level)
            ToggleFilterLineCommand = new Command(param =>
            {
                if (param is FilterLine fl)
                    ToggleFilterLine(fl);
            });

            // Receiver wide toggle
            ToggleReceiverFilterCommand = new Command(param =>
            {
                if (param is FilterLine fl)
                    ToggleReceiverFilter(fl);
            });

            // Site wide toggle for a given receiver and site
            ToggleSiteFilterCommand = new Command(param =>
            {
                if (param is FilterLine fl)
                    ToggleSiteFilter(fl);
            });

            // Remove one line from the list
            RemoveFilterLineCommand = new Command(param =>
            {
                if (param is FilterLine fl)
                    RemoveFilterLine(fl);
            });

            // Apply persisted disabled keys to any preexisting lines, then sort
            ApplyDisabledKeysToExistingLines();
            SortFilterLines();
            RaiseFilterStateChanged();

        }

        /// <summary>
        /// If true, there are unsaved connection changes.
        /// Used by SettingsPage.xaml.cs to discard or warn when backing out.
        /// </summary>
        public bool HasChanges
        {
            get => _hasChanges;
            private set
            {
                if (_hasChanges == value)
                    return;

                _hasChanges = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Current server URL in the edit box.
        /// </summary>
        public string ServerUrl
        {
            get => _serverUrl;
            set
            {
                if (_serverUrl == value)
                    return;

                _serverUrl = value ?? string.Empty;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        /// <summary>
        /// True when "Default connection" is selected.
        /// When true, the server URL will be reset to the default and saved.
        /// </summary>
        public bool UseDefaultConnection
        {
            get => _useDefaultConnection;
            set
            {
                if (_useDefaultConnection == value)
                    return;

                _useDefaultConnection = value;
                OnPropertyChanged();
                UpdateHasChanges();
            }
        }

        /// <summary>
        /// Maximum number of calls kept in the list.
        /// </summary>
        public int MaxCalls
        {
            get => _maxCalls;
            set
            {
                var clamped = value <= 0 ? 1 : value;
                if (_maxCalls == clamped)
                    return;

                _maxCalls = clamped;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// True if the value representing "Newest at bottom" is selected.
        /// </summary>
        public bool ScrollNewestAtBottom
        {
            get => _scrollNewestAtBottom;
            set
            {
                if (_scrollNewestAtBottom == value)
                    return;

                _scrollNewestAtBottom = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Theme mode string: "System", "Light", or "Dark".
        /// </summary>
        public string ThemeMode
        {
            get => _themeMode;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "System" : value;

                if (string.Equals(_themeMode, normalized, StringComparison.OrdinalIgnoreCase))
                    return;

                _themeMode = normalized;
                OnPropertyChanged();

                // Keep boolean helpers in sync
                OnPropertyChanged(nameof(IsThemeSystem));
                OnPropertyChanged(nameof(IsThemeLight));
                OnPropertyChanged(nameof(IsThemeDark));

                // Apply theme immediately and persist to the settings service
                _settings.ThemeMode = _themeMode;
                ApplyTheme(_themeMode);
            }
        }


        /// <summary>
        /// Helper booleans for the three theme radio buttons.
        /// Only the "true" assignment triggers changes.
        /// </summary>
        public bool IsThemeSystem
        {
            get => string.Equals(ThemeMode, "System", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (!value)
                    return;
                ThemeMode = "System";
            }
        }

        public bool IsThemeLight
        {
            get => string.Equals(ThemeMode, "Light", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (!value)
                    return;
                ThemeMode = "Light";
            }
        }

        public bool IsThemeDark
        {
            get => string.Equals(ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (!value)
                    return;
                ThemeMode = "Dark";
            }
        }

        /// <summary>
        /// Unified filter lines (Receiver > Site > Talkgroup).
        /// Populated dynamically from live calls.
        /// </summary>
        public ObservableCollection<FilterLine> FilterLines => _filterLines;

        /// <summary>
        /// Sort order flag. True for A to Z, false for Z to A.
        /// </summary>
        public bool SortAscending
        {
            get => _sortAscending;
            set
            {
                if (_sortAscending == value)
                    return;

                _sortAscending = value;
                SortFilterLines();
                RaiseFilterStateChanged();
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// True when at least one filter is actually restricting calls
        /// (that is, at least one line is disabled).
        /// MainViewModel can mirror this for its "Filters" header badge.
        /// </summary>
        public bool HasAnyActiveFilters =>
            FilterLines.Any(l => !l.IsEnabled);

        /// <summary>
        /// Reloads view model fields from ISettingsService.
        /// Called once from constructor.
        /// </summary>
        private void InitializeFromSettings()
        {
            // Connection seed
            _serverUrl = _settings.ServerUrl ?? string.Empty;
            _savedServerUrl = _serverUrl;

            // Default connection flag
            var defaultUrl = DefaultServerUrl;
            _useDefaultConnection = string.Equals(_serverUrl, defaultUrl, StringComparison.OrdinalIgnoreCase);
            _savedUseDefaultConnection = _useDefaultConnection;

            // Calls
            _maxCalls = _settings.MaxCalls <= 0 ? 25 : _settings.MaxCalls;

            // Scroll direction from settings service
            var scrollDirection = _settings.ScrollDirection;
            _scrollNewestAtBottom = string.Equals(scrollDirection, "Down", StringComparison.OrdinalIgnoreCase);

            // Theme – normalize to a safe value and push back into settings if needed
            var rawTheme = _settings.ThemeMode;
            if (string.IsNullOrWhiteSpace(rawTheme) ||
                (!string.Equals(rawTheme, "System", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(rawTheme, "Light", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(rawTheme, "Dark", StringComparison.OrdinalIgnoreCase)))
            {
                _themeMode = "System";
                _settings.ThemeMode = "System";
            }
            else
            {
                _themeMode = rawTheme;
            }

            // Load disabled keys from settings. We reuse ReceiverFilter
            // as "Disabled filter entries" stored as:
            //   receiver|site|talkgroup;receiver2|site2|talkgroup2
            _disabledKeys.Clear();
            var rawDisabled = _settings.ReceiverFilter ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(rawDisabled))
            {
                var entries = rawDisabled.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var entry in entries)
                {
                    var trimmed = entry.Trim();
                    if (trimmed.Length == 0)
                        continue;

                    // Only accept entries that look like our new format
                    if (!trimmed.Contains('|'))
                        continue;

                    _disabledKeys.Add(trimmed);
                }
            }

            HasChanges = false;
        }

        /// <summary>
        /// Applies the current _disabledKeys set to any already loaded filter lines.
        /// </summary>
        private void ApplyDisabledKeysToExistingLines()
        {
            foreach (var line in _filterLines)
            {
                var key = MakeKey(line.Receiver, line.Site, line.Talkgroup);
                line.IsEnabled = !_disabledKeys.Contains(key);
            }
        }

        private void ApplyTheme(string mode)
        {
            var app = Application.Current;
            if (app == null)
                return;

            AppTheme theme;

            if (string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase))
            {
                theme = AppTheme.Light;
            }
            else if (string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase))
            {
                theme = AppTheme.Dark;
            }
            else
            {
                theme = AppTheme.Unspecified; // follow system
            }

            app.UserAppTheme = theme;
        }


        /// <summary>
        /// Persists connection, call list, and theme settings,
        /// and updates main view model mirrors where needed.
        /// Also persists disabled filter keys.
        /// </summary>
        private void SaveSettings()
        {
            // Connection
            if (UseDefaultConnection)
            {
                _settings.ServerUrl = DefaultServerUrl;
                _mainViewModel.ServerUrl = _settings.ServerUrl;
            }
            else
            {
                _settings.ServerUrl = ServerUrl;
                _mainViewModel.ServerUrl = ServerUrl;
            }

            // Max calls and scroll direction
            _settings.MaxCalls = MaxCalls;
            _mainViewModel.MaxCalls = MaxCalls;

            _settings.ScrollDirection = ScrollNewestAtBottom ? "Down" : "Up";

            // Theme (apply on save)
            _settings.ThemeMode = ThemeMode;
            ApplyTheme(ThemeMode);

            // Persist disabled lines
            _settings.ReceiverFilter = string.Join(";", _disabledKeys);

            // Mirror active filter state to main view model for the "Filters" badge
            _mainViewModel.HasActiveFilters = HasAnyActiveFilters;

            // Update our saved snapshot for "HasChanges"
            _savedServerUrl = _settings.ServerUrl;
            _savedUseDefaultConnection = UseDefaultConnection;
            HasChanges = false;
        }

        /// <summary>
        /// Reset server url to the canonical default and mark as default.
        /// This only changes local fields until Save is pressed.
        /// </summary>
        private void ResetServerUrl()
        {
            ServerUrl = DefaultServerUrl;
            UseDefaultConnection = true;
        }

        /// <summary>
        /// Very lightweight server validation. You can expand this to hit
        /// your health endpoint later.
        /// </summary>
        private async Task ValidateServerUrlAsync()
        {
            var url = ServerUrl?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                await Application.Current.MainPage.DisplayAlert(
                    "Server validation",
                    "Please enter a server url first.",
                    "OK");
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    await Application.Current.MainPage.DisplayAlert(
                        "Server validation",
                        "Server appears to be reachable.",
                        "OK");
                }
                else
                {
                    await Application.Current.MainPage.DisplayAlert(
                        "Server validation",
                        $"Server responded with status {(int)response.StatusCode}.",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                await Application.Current.MainPage.DisplayAlert(
                    "Server validation",
                    $"Could not reach server: {ex.Message}",
                    "OK");
            }
        }

        /// <summary>
        /// Used by SettingsPage.xaml.cs when closing the page
        /// without saving. Resets the connection fields to match
        /// the last saved values.
        /// </summary>
        public void DiscardConnectionChanges()
        {
            ServerUrl = _savedServerUrl;
            UseDefaultConnection = _savedUseDefaultConnection;
            HasChanges = false;
        }

        private void UpdateHasChanges()
        {
            var has = !string.Equals(_serverUrl, _savedServerUrl, StringComparison.OrdinalIgnoreCase)
                      || _useDefaultConnection != _savedUseDefaultConnection;

            HasChanges = has;
        }

        private void RaiseFilterStateChanged()
        {
            OnPropertyChanged(nameof(FilterLines));
            OnPropertyChanged(nameof(HasAnyActiveFilters));
        }

        #region Filter helpers for UI

        /// <summary>
        /// Helper to set a line enabled or disabled and keep the backing
        /// disabled key set in sync.
        /// </summary>
        private void SetLineEnabled(FilterLine line, bool isEnabled)
        {
            if (line == null)
                return;

            if (line.IsEnabled == isEnabled)
                return;

            line.IsEnabled = isEnabled;

            var key = MakeKey(line.Receiver, line.Site, line.Talkgroup);
            if (!line.IsEnabled)
            {
                _disabledKeys.Add(key);
            }
            else
            {
                _disabledKeys.Remove(key);
            }
        }

        /// <summary>
        /// Toggle a single filter line on or off.
        /// This is what the talkgroup label uses.
        /// </summary>
        private void ToggleFilterLine(FilterLine line)
        {
            if (line == null)
                return;

            SetLineEnabled(line, !line.IsEnabled);

            RaiseFilterStateChanged();
            _mainViewModel.HasActiveFilters = HasAnyActiveFilters;
        }

        /// <summary>
        /// Toggle all lines that share the same Receiver as the source line.
        /// If any of them are enabled, they all become disabled.
        /// If all are disabled, they all become enabled.
        /// </summary>
        private void ToggleReceiverFilter(FilterLine source)
        {
            if (source == null)
                return;

            var receiver = source.Receiver ?? string.Empty;

            var lines = _filterLines
                .Where(l => string.Equals(l.Receiver, receiver, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (lines.Count == 0)
                return;

            var anyEnabled = lines.Any(l => l.IsEnabled);
            var newState = !anyEnabled;

            foreach (var line in lines)
            {
                SetLineEnabled(line, newState);
            }

            RaiseFilterStateChanged();
            _mainViewModel.HasActiveFilters = HasAnyActiveFilters;
        }

        /// <summary>
        /// Toggle all lines that share the same Receiver and Site as the source line.
        /// This lets the user flip an entire site worth of talkgroups at once.
        /// </summary>
        private void ToggleSiteFilter(FilterLine source)
        {
            if (source == null)
                return;

            var receiver = source.Receiver ?? string.Empty;
            var site = source.Site ?? string.Empty;

            var lines = _filterLines
                .Where(l =>
                    string.Equals(l.Receiver, receiver, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(l.Site, site, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (lines.Count == 0)
                return;

            var anyEnabled = lines.Any(l => l.IsEnabled);
            var newState = !anyEnabled;

            foreach (var line in lines)
            {
                SetLineEnabled(line, newState);
            }

            RaiseFilterStateChanged();
            _mainViewModel.HasActiveFilters = HasAnyActiveFilters;
        }

        /// <summary>
        /// Remove a single filter line from the list.
        /// Any future calls for this triple will recreate the line.
        /// </summary>
        private void RemoveFilterLine(FilterLine line)
        {
            if (line == null)
                return;

            var key = MakeKey(line.Receiver, line.Site, line.Talkgroup);

            _disabledKeys.Remove(key);
            _filterLines.Remove(line);

            RaiseFilterStateChanged();
            _mainViewModel.HasActiveFilters = HasAnyActiveFilters;
        }


        private void SortFilterLines()
        {
            if (_filterLines.Count <= 1)
                return;

            var ordered = _sortAscending
                ? _filterLines
                    .OrderBy(l => l.Receiver)
                    .ThenBy(l => l.Site)
                    .ThenBy(l => l.Talkgroup)
                    .ToList()
                : _filterLines
                    .OrderByDescending(l => l.Receiver)
                    .ThenByDescending(l => l.Site)
                    .ThenByDescending(l => l.Talkgroup)
                    .ToList();

            _filterLines.Clear();
            foreach (var fl in ordered)
                _filterLines.Add(fl);
        }

        private static string MakeKey(string receiver, string site, string talkgroup)
        {
            return $"{receiver}|{site}|{talkgroup}";
        }

        #endregion

        #region Filter helpers for MainViewModel

        /// <summary>
        /// Called from MainViewModel for every call seen.
        /// Populates the unified filter list on first sight of each triple.
        /// </summary>
        public static void OnCallSeen(CallItem call)
        {
            if (call == null)
                return;

            var receiver = call.ReceiverName ?? string.Empty;
            var site = call.SystemName ?? string.Empty;
            var talkgroup = call.Talkgroup ?? string.Empty;

            var existing = _filterLines.FirstOrDefault(l =>
                string.Equals(l.Receiver, receiver, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(l.Site, site, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(l.Talkgroup, talkgroup, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return;

            var key = MakeKey(receiver, site, talkgroup);

            var line = new FilterLine
            {
                Receiver = receiver,
                Site = site,
                Talkgroup = talkgroup,
                IsEnabled = !_disabledKeys.Contains(key)
            };

            _filterLines.Add(line);

            if (Current != null)
            {
                Current.SortFilterLines();
                Current.RaiseFilterStateChanged();
                Current._mainViewModel.HasActiveFilters = Current.HasAnyActiveFilters;
            }
        }

        /// <summary>
        /// Called from MainViewModel before adding a call to the list.
        /// Returns true if the call should be shown and played, false if filtered out.
        /// </summary>
        public static bool IsCallAllowed(CallItem call)
        {
            if (call == null)
                return true;

            var receiver = call.ReceiverName ?? string.Empty;
            var site = call.SystemName ?? string.Empty;
            var talkgroup = call.Talkgroup ?? string.Empty;

            var line = _filterLines.FirstOrDefault(l =>
                string.Equals(l.Receiver, receiver, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(l.Site, site, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(l.Talkgroup, talkgroup, StringComparison.OrdinalIgnoreCase));

            if (line != null)
                return line.IsEnabled;

            // No line yet. By default allow new calls.
            return true;
        }

        #endregion
    }
}
