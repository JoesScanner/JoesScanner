using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Microsoft.Data.Sqlite;
using JoesScanner.Models;

namespace JoesScanner.Services
{
    // Concrete implementation of ISettingsService backed by the local SQLite database.
    // The DB is the single source of truth.
    public sealed class SettingsService : ISettingsService
    {

        // DB tables.
        private const string DbFileName = "joesscanner.db";
        private const string TableAppSettings = "app_settings";
        private const string TableServers = "servers";
        private const string TableServerRuntimeState = "server_runtime_state";
        private const string TableServerAuthState = "server_auth_state";

        // App settings keys.
        private const string KeyAuthServerBaseUrl = "auth_server_base_url";
        private const string KeyServerUrl = "server_url";
        private const string KeyBasicAuthUser = "basic_auth_user";
        private const string KeyBasicAuthPass = "basic_auth_pass";
        private const string KeyLastAuthUser = "last_auth_user";
        private const string KeyAutoPlay = "autoplay_enabled";
        private const string KeyWindowsAutoConnectOnStart = "windows_auto_connect_on_start";
        private const string KeyMobileAutoConnectOnStart = "mobile_auto_connect_on_start";
        private const string KeyWindowsStartWithWindows = "windows_start_with_windows";
        private const string KeyScrollDirection = "scroll_direction";
        private const string KeyReceiverFilter = "receiver_filter";
        private const string KeyTalkgroupFilter = "talkgroup_filter";
        private const string KeyDescriptionFilter = "description_filter";
        private const string KeyThemeMode = "theme_mode";
        private const string KeyDeviceInstallId = "device_install_id";
        private const string KeyAuthSessionToken = "auth_session_token";
        private const string KeyBluetoothArtist = "bt_label_artist";
        private const string KeyBluetoothTitle = "bt_label_title";
        private const string KeyBluetoothAlbum = "bt_label_album";
        private const string KeyBluetoothComposer = "bt_label_composer";
        private const string KeyBluetoothGenre = "bt_label_genre";

        private const string KeyWhat3WordsLinksEnabled = "what3words_links_enabled";
        private const string KeyWhat3WordsApiKey = "what3words_api_key";

        private const string KeyAddressDetectionEnabled = "address_detection_enabled";
        private const string KeyAddressDetectionOpenMapsOnTap = "address_detection_open_maps_on_tap";

        private const string KeyAddressDetectionMinConfidencePercent = "address_detection_min_confidence_percent";
        private const string KeyAddressDetectionMinAddressChars = "address_detection_min_address_chars";
        private const string KeyAddressDetectionMaxCandidatesPerCall = "address_detection_max_candidates_per_call";

        private const string KeyAudioStaticFilterEnabled = "audio_static_filter_enabled";
        private const string KeyAudioStaticAttenuatorVolume = "audio_static_attenuator_volume";
        private const string KeyAudioStaticAttenuatorVolumeLegacy = "audio_static_squelch";

        private const string KeyAudioToneFilterEnabled = "audio_tone_filter_enabled";
        private const string KeyAudioToneStrength = "audio_tone_strength";
        private const string KeyAudioToneSensitivity = "audio_tone_sensitivity";
        private const string KeyAudioToneHighlightMinutes = "audio_tone_highlight_minutes";

        private const string KeyTelemetryEnabled = "telemetry_enabled";
// Built-in server definitions for this phase.
        private const string BuiltinJoeKey = "joe";
        private const string BuiltinCustomKey = "custom";
        private const string BuiltinJoeUrl = "https://app.joesscanner.com";

        private readonly string _dbPath;
        private readonly object _gate = new();

        private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
        private bool _initialized;

        public SettingsService(IDatabasePathProvider dbPathProvider)
        {
            _dbPath = (dbPathProvider ?? throw new ArgumentNullException(nameof(dbPathProvider))).DbPath;

            _dbPath = Path.Combine(FileSystem.AppDataDirectory, DbFileName);
        }

        // Fired when any address detection setting changes, so interested view models can re-apply detection to existing calls.
        public event EventHandler? AddressDetectionSettingsChanged;

        // Fired when what3words settings change.
        public event EventHandler? What3WordsSettingsChanged;

        private void OnWhat3WordsSettingsChanged()
        {
            try
            {
                What3WordsSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
            }
        }

        private void OnAddressDetectionSettingsChanged()
        {
            try
            {
                AddressDetectionSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
            }
        }


        public string AuthServerBaseUrl
        {
            get => GetString(KeyAuthServerBaseUrl, string.Empty);
            set => SetString(KeyAuthServerBaseUrl, (value ?? string.Empty).Trim());
        }

        public string ServerUrl
        {
            // IMPORTANT:
            // Do NOT default to Joe's hosted server implicitly.
            // A blank connection card must mean "not configured" so the app cannot
            // connect or autoplay using the hosted service account.
            get => GetString(KeyServerUrl, string.Empty);
            set
            {
                var cleaned = (value ?? string.Empty).Trim();
                // Allow an empty value. This represents "no active connection".
                SetString(KeyServerUrl, cleaned);

                // Keep servers table current for Joe/custom without changing UI behavior.
                EnsureInitialized();
                EnsureBuiltinServersSeeded();
                TouchSelectedServerUrl(cleaned);
            }
        }

        public string BasicAuthUsername
        {
            get => GetString(KeyBasicAuthUser, string.Empty);
            set => SetString(KeyBasicAuthUser, (value ?? string.Empty).Trim());
        }

        public string BasicAuthPassword
        {
            get => GetString(KeyBasicAuthPass, string.Empty);
            set => SetString(KeyBasicAuthPass, (value ?? string.Empty).Trim());
        }


        // Per-server credential cache. These are stored only after a successful Validate
        // and are keyed by the normalized server base URL.
        public bool TryGetServerCredentials(string serverUrl, out string username, out string password)
        {
            var key = NormalizeServerKey(serverUrl);
            if (string.IsNullOrWhiteSpace(key))
            {
                username = string.Empty;
                password = string.Empty;
                return false;
            }

            username = GetString(GetServerCredUserKey(key), string.Empty).Trim();
            password = GetString(GetServerCredPassKey(key), string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                return true;

            // Back-compat: try legacy keying (raw URL without path normalization) and migrate forward if found.
            var legacyKey = LegacyNormalizeServerKey(serverUrl);
            if (!string.IsNullOrWhiteSpace(legacyKey) && !string.Equals(legacyKey, key, StringComparison.Ordinal))
            {
                var legacyUser = GetString(GetServerCredUserKey(legacyKey), string.Empty).Trim();
                var legacyPass = GetString(GetServerCredPassKey(legacyKey), string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(legacyUser) && !string.IsNullOrWhiteSpace(legacyPass))
                {
                    // Migrate to normalized key.
                    SetString(GetServerCredUserKey(key), legacyUser);
                    SetString(GetServerCredPassKey(key), legacyPass);
                    Remove(GetServerCredUserKey(legacyKey));
                    Remove(GetServerCredPassKey(legacyKey));

                    username = legacyUser;
                    password = legacyPass;
                    return true;
                }
            }

            username = string.Empty;
            password = string.Empty;
            return false;
        }

        public void SetServerCredentials(string serverUrl, string username, string password)
        {
            var key = NormalizeServerKey(serverUrl);
            if (string.IsNullOrWhiteSpace(key))
                return;

            var u = (username ?? string.Empty).Trim();
            var p = (password ?? string.Empty).Trim();

            // If either is missing, treat as a clear.
            if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(p))
            {
                ClearServerCredentials(key);
                return;
            }

            SetString(GetServerCredUserKey(key), u);
            SetString(GetServerCredPassKey(key), p);

            // Also clear any legacy-stored creds for the same raw URL to avoid stale mismatches.
            try
            {
                var legacyKey = LegacyNormalizeServerKey(serverUrl);
                if (!string.IsNullOrWhiteSpace(legacyKey) && !string.Equals(legacyKey, key, StringComparison.Ordinal))
                {
                    Remove(GetServerCredUserKey(legacyKey));
                    Remove(GetServerCredPassKey(legacyKey));
                }
            }
            catch
            {
            }
        }

        public void ClearServerCredentials(string serverUrl)
        {
            var key = NormalizeServerKey(serverUrl);
            if (string.IsNullOrWhiteSpace(key))
                return;

            Remove(GetServerCredUserKey(key));
            Remove(GetServerCredPassKey(key));

            // Back-compat: also clear legacy keyed values.
            try
            {
                var legacyKey = LegacyNormalizeServerKey(serverUrl);
                if (!string.IsNullOrWhiteSpace(legacyKey) && !string.Equals(legacyKey, key, StringComparison.Ordinal))
                {
                    Remove(GetServerCredUserKey(legacyKey));
                    Remove(GetServerCredPassKey(legacyKey));
                }
            }
            catch
            {
            }
        }

        private static string GetServerCredUserKey(string normalizedServerUrl)
        {
            return $"server_cred_user::{NormalizeServerKey(normalizedServerUrl)}";
        }

        private static string GetServerCredPassKey(string normalizedServerUrl)
        {
            return $"server_cred_pass::{NormalizeServerKey(normalizedServerUrl)}";
        }


        public string LastAuthUsername
        {
            get => GetString(KeyLastAuthUser, string.Empty);
            set => SetString(KeyLastAuthUser, (value ?? string.Empty).Trim());
        }

        public bool AutoPlay
        {
            get => GetBool(KeyAutoPlay, false);
            set => SetBool(KeyAutoPlay, value);
        }

        public bool WindowsAutoConnectOnStart
        {
            get => GetBool(KeyWindowsAutoConnectOnStart, false);
            set => SetBool(KeyWindowsAutoConnectOnStart, value);
        }

        public bool MobileAutoConnectOnStart
        {
            get => GetBool(KeyMobileAutoConnectOnStart, false);
            set => SetBool(KeyMobileAutoConnectOnStart, value);
        }

        public bool WindowsStartWithWindows
        {
            get => GetBool(KeyWindowsStartWithWindows, false);
            set => SetBool(KeyWindowsStartWithWindows, value);
        }

        public string ScrollDirection
        {
            get => GetString(KeyScrollDirection, "Down");
            set => SetString(KeyScrollDirection, (value ?? "Down").Trim());
        }

        public string ReceiverFilter
        {
            get => GetString(KeyReceiverFilter, string.Empty);
            set => SetString(KeyReceiverFilter, (value ?? string.Empty).Trim());
        }

        public string TalkgroupFilter
        {
            get => GetString(KeyTalkgroupFilter, string.Empty);
            set => SetString(KeyTalkgroupFilter, (value ?? string.Empty).Trim());
        }

        public string DescriptionFilter
        {
            get => GetString(KeyDescriptionFilter, string.Empty);
            set => SetString(KeyDescriptionFilter, (value ?? string.Empty).Trim());
        }

        public string ThemeMode
        {
            get => GetString(KeyThemeMode, "System");
            set => SetString(KeyThemeMode, (value ?? "System").Trim());
        }

        // Subscription cache is stored per server base URL (ServerUrl) in server_auth_state.
        public DateTime? SubscriptionLastCheckUtc
        {
            get
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                return ParseUtc(auth.LastCheckUtc);
            }
            set
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                auth.LastCheckUtc = value.HasValue ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : null;
                WriteServerAuthState(GetAuthContextKey(), auth);
            }
        }

        public bool SubscriptionLastStatusOk
        {
            get
            {
                EnsureInitialized();
                return ReadServerAuthState(GetAuthContextKey()).IsValidated;
            }
            set
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                auth.IsValidated = value;
                if (value && string.IsNullOrWhiteSpace(auth.ValidatedUtc))
                    auth.ValidatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                WriteServerAuthState(GetAuthContextKey(), auth);
            }
        }

        public bool SubscriptionLastValidatedOnline
        {
            get
            {
                EnsureInitialized();
                return ReadServerAuthState(GetAuthContextKey()).ValidatedOnline;
            }
            set
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                auth.ValidatedOnline = value;
                WriteServerAuthState(GetAuthContextKey(), auth);
            }
        }

        public string SubscriptionLastLevel
        {
            get
            {
                EnsureInitialized();
                return (ReadServerAuthState(GetAuthContextKey()).LastLevel ?? string.Empty).Trim();
            }
            set
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                auth.LastLevel = (value ?? string.Empty).Trim();
                WriteServerAuthState(GetAuthContextKey(), auth);
            }
        }

        public string SubscriptionPriceId
        {
            get
            {
                EnsureInitialized();
                return (ReadServerAuthState(GetAuthContextKey()).PriceId ?? string.Empty).Trim();
            }
            set
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                auth.PriceId = (value ?? string.Empty).Trim();
                WriteServerAuthState(GetAuthContextKey(), auth);
            }
        }

        public int SubscriptionTierLevel
        {
            get
            {
                EnsureInitialized();
                return ReadServerAuthState(GetAuthContextKey()).TierLevel;
            }
            set
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                auth.TierLevel = value < 0 ? 0 : value;
                WriteServerAuthState(GetAuthContextKey(), auth);
            }
        }

        public DateTime? SubscriptionExpiresUtc
        {
            get
            {
                EnsureInitialized();
                return ParseUtc(ReadServerAuthState(GetAuthContextKey()).ExpiresUtc);
            }
            set
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                auth.ExpiresUtc = value.HasValue ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : null;
                WriteServerAuthState(GetAuthContextKey(), auth);
            }
        }

        public DateTime? SubscriptionRenewalUtc
        {
            get
            {
                EnsureInitialized();
                return ParseUtc(ReadServerAuthState(GetAuthContextKey()).RenewalUtc);
            }
            set
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                auth.RenewalUtc = value.HasValue ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : null;
                WriteServerAuthState(GetAuthContextKey(), auth);
            }
        }

        public string SubscriptionLastMessage
        {
            get
            {
                EnsureInitialized();
                return (ReadServerAuthState(GetAuthContextKey()).LastMessage ?? string.Empty).Trim();
            }
            set
            {
                EnsureInitialized();
                var auth = ReadServerAuthState(GetAuthContextKey());
                auth.LastMessage = (value ?? string.Empty).Trim();
                WriteServerAuthState(GetAuthContextKey(), auth);
            }
        }

        public string DeviceInstallId
        {
            get
            {
                EnsureInitialized();

                var deviceId = GetString(KeyDeviceInstallId, string.Empty);
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    deviceId = Guid.NewGuid().ToString();
                    SetString(KeyDeviceInstallId, deviceId);
                }

                return deviceId;
            }
            set
            {
                EnsureInitialized();
                var cleaned = (value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    Remove(KeyDeviceInstallId);
                    return;
                }

                SetString(KeyDeviceInstallId, cleaned);
            }
        }

        public string AuthSessionToken
        {
            get => GetString(KeyAuthSessionToken, string.Empty);
            set => SetString(KeyAuthSessionToken, (value ?? string.Empty).Trim());
        }

        public string BluetoothLabelArtist
        {
            get => GetString(KeyBluetoothArtist, "AppName");
            set => SetString(KeyBluetoothArtist, (value ?? "AppName").Trim());
        }

        public string BluetoothLabelTitle
        {
            get => GetString(KeyBluetoothTitle, "Transcription");
            set => SetString(KeyBluetoothTitle, (value ?? "Transcription").Trim());
        }

        public string BluetoothLabelAlbum
        {
            get => GetString(KeyBluetoothAlbum, "Talkgroup");
            set => SetString(KeyBluetoothAlbum, (value ?? "Talkgroup").Trim());
        }

        public string BluetoothLabelComposer
        {
            get => GetString(KeyBluetoothComposer, "Site");
            set => SetString(KeyBluetoothComposer, (value ?? "Site").Trim());
        }

        public string BluetoothLabelGenre
        {
            get => GetString(KeyBluetoothGenre, "Receiver");
            set => SetString(KeyBluetoothGenre, (value ?? "Receiver").Trim());
        }


        public bool What3WordsLinksEnabled
        {
            get => GetBool(KeyWhat3WordsLinksEnabled, true);
            set => SetBool(KeyWhat3WordsLinksEnabled, value);
        }

        public string What3WordsApiKey
        {
            get => GetString(KeyWhat3WordsApiKey, string.Empty);
            set => SetString(KeyWhat3WordsApiKey, (value ?? string.Empty).Trim());
        }

        public bool AddressDetectionEnabled
        {
            get => GetBool(KeyAddressDetectionEnabled, false);
            set => SetBool(KeyAddressDetectionEnabled, value);
        }
        public bool AddressDetectionOpenMapsOnTap
        {
            get => GetBool(KeyAddressDetectionOpenMapsOnTap, true);
            set => SetBool(KeyAddressDetectionOpenMapsOnTap, value);
        }

        public int AddressDetectionMinConfidencePercent
        {
            get => Clamp(GetInt(KeyAddressDetectionMinConfidencePercent, 70), 0, 100);
            set => SetInt(KeyAddressDetectionMinConfidencePercent, Clamp(value, 0, 100));
        }

        public int AddressDetectionMinAddressChars
        {
            get => Clamp(GetInt(KeyAddressDetectionMinAddressChars, 8), 0, 200);
            set => SetInt(KeyAddressDetectionMinAddressChars, Clamp(value, 0, 200));
        }

        public int AddressDetectionMaxCandidatesPerCall
        {
            get => Clamp(GetInt(KeyAddressDetectionMaxCandidatesPerCall, 3), 1, 10);
            set => SetInt(KeyAddressDetectionMaxCandidatesPerCall, Clamp(value, 1, 10));
        }


        public bool AudioStaticFilterEnabled
        {
            get => GetBool(KeyAudioStaticFilterEnabled, false);
            set => SetBool(KeyAudioStaticFilterEnabled, value);
        }

        public int AudioStaticAttenuatorVolume
        {
            get
            {
                var v = GetInt(KeyAudioStaticAttenuatorVolume, int.MinValue);
                if (v == int.MinValue)
                    v = GetInt(KeyAudioStaticAttenuatorVolumeLegacy, 50);
                return Clamp(v, 0, 100);
            }
            set => SetInt(KeyAudioStaticAttenuatorVolume, Clamp(value, 0, 100));
        }

        public bool AudioToneFilterEnabled
        {
            get => GetBool(KeyAudioToneFilterEnabled, false);
            set => SetBool(KeyAudioToneFilterEnabled, value);
        }

        public int AudioToneStrength
        {
            get => Clamp(GetInt(KeyAudioToneStrength, 50), 0, 100);
            set => SetInt(KeyAudioToneStrength, Clamp(value, 0, 100));
        }

        public int AudioToneSensitivity
        {
            // Higher = detect tones more easily.
            get => Clamp(GetInt(KeyAudioToneSensitivity, 50), 0, 100);
            set => SetInt(KeyAudioToneSensitivity, Clamp(value, 0, 100));
        }


        public int AudioToneHighlightMinutes
        {
            get => Clamp(GetInt(KeyAudioToneHighlightMinutes, 5), 1, 99);
            set => SetInt(KeyAudioToneHighlightMinutes, Clamp(value, 1, 99));
        }

        public bool TelemetryEnabled
        {
            // Default ON to preserve existing behavior.
            get => GetBool(KeyTelemetryEnabled, true);
            set => SetBool(KeyTelemetryEnabled, value);
        }

        private void EnsureInitialized()
        {
            lock (_gate)
            {
                if (_initialized)
                    return;

                EnsureSchema();
                EnsureBuiltinServersSeeded();
                LoadCache();
                EnsureDefaults();

                _initialized = true;
            }
        }

        private void EnsureSchema()
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
PRAGMA foreign_keys=ON;
PRAGMA busy_timeout=5000;

CREATE TABLE IF NOT EXISTS app_settings (
  setting_key TEXT PRIMARY KEY,
  setting_value TEXT NOT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS servers (
  server_key TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  base_url TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  sort_order INTEGER NOT NULL DEFAULT 0,
  is_builtin INTEGER NOT NULL DEFAULT 0,
  created_utc TEXT NOT NULL,
  updated_utc TEXT NOT NULL,
  last_used_utc TEXT NULL
);

CREATE TABLE IF NOT EXISTS server_runtime_state (
  server_key TEXT PRIMARY KEY,
  last_history_loaded_utc TEXT NULL,
  last_live_seen_utc TEXT NULL,
  last_error_utc TEXT NULL,
  last_error TEXT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS server_auth_state (
  server_key TEXT PRIMARY KEY,
  is_validated INTEGER NOT NULL DEFAULT 0,
  validated_online INTEGER NOT NULL DEFAULT 0,
  validated_utc TEXT NULL,
  expires_utc TEXT NULL,
  last_attempt_utc TEXT NULL,
  last_status_code INTEGER NULL,
  last_error TEXT NULL,
  last_check_utc TEXT NULL,
  last_level TEXT NULL,
  last_message TEXT NULL,
  price_id TEXT NULL,
  tier_level INTEGER NOT NULL DEFAULT 0,
  renewal_utc TEXT NULL,
  updated_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_servers_enabled_sort ON servers(enabled, sort_order);
";
            cmd.ExecuteNonQuery();

            // Upgrade older installs that had a minimal servers table.
            EnsureColumnExists(conn, TableServers, "display_name", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, TableServers, "enabled", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumnExists(conn, TableServers, "sort_order", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumnExists(conn, TableServers, "is_builtin", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumnExists(conn, TableServers, "created_utc", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, TableServers, "updated_utc", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, TableServers, "last_used_utc", "TEXT NULL");

            EnsureColumnExists(conn, TableServers, "info_url", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, TableServers, "area_label", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, TableServers, "map_anchor", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, TableServers, "is_official", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumnExists(conn, TableServers, "directory_id", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumnExists(conn, TableServers, "badge", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, TableServers, "badge_label", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, TableServers, "source", "TEXT NOT NULL DEFAULT ''");

            // Upgrade older installs that had a minimal auth state table.
            EnsureColumnExists(conn, TableServerAuthState, "validated_online", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumnExists(conn, TableServerAuthState, "tier_level", "INTEGER NOT NULL DEFAULT 0");
        }

        private static void EnsureColumnExists(SqliteConnection conn, string tableName, string columnName, string columnDefinition)
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
            alter.ExecuteNonQuery();
        }

        private SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            conn.Open();
            return conn;
        }

        private void LoadCache()
        {
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT setting_key, setting_value FROM {TableAppSettings};";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                _cache[reader.GetString(0)] = reader.GetString(1);
            }
        }

        private void EnsureDefaults()
        {
            // IMPORTANT: EnsureDefaults is invoked from EnsureInitialized() while _initialized is still false.
            // Do NOT call GetString/SetString here, because those call EnsureInitialized() and would recurse.
            // Read from the cache loaded by LoadCache() and write via a raw DB upsert if needed.

            var currentServer = string.Empty;
            lock (_gate)
            {
                if (_cache.TryGetValue(KeyServerUrl, out var v))
                    currentServer = v;
            }

            if (string.IsNullOrWhiteSpace(currentServer))
            {
                // Leave server_url empty by default.
                // The user must explicitly configure/validate a connection before the app
                // will connect to any server (including the hosted Joe's server).
                var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                WriteSettingRaw(KeyServerUrl, string.Empty, now);
            }
        }

        private void WriteSettingRaw(string key, string value, string nowUtc)
        {
            lock (_gate)
            {
                _cache[key] = value ?? string.Empty;
            }

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO app_settings (setting_key, setting_value, updated_utc)
VALUES ($k, $v, $u)
ON CONFLICT(setting_key) DO UPDATE SET
  setting_value = excluded.setting_value,
  updated_utc = excluded.updated_utc;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value ?? string.Empty);
            cmd.Parameters.AddWithValue("$u", nowUtc);
            cmd.ExecuteNonQuery();
        }

        private void EnsureBuiltinServersSeeded()
        {
            using var conn = OpenConnection();
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            UpsertServer(conn, BuiltinJoeKey, "Joe's server", BuiltinJoeUrl, enabled: 1, sortOrder: 0, isBuiltin: 1, nowUtc: now);
            UpsertServer(conn, BuiltinCustomKey, "Custom", "", enabled: 1, sortOrder: 1, isBuiltin: 1, nowUtc: now);
        }

        private static void UpsertServer(SqliteConnection conn, string serverKey, string displayName, string baseUrl, int enabled, int sortOrder, int isBuiltin, string nowUtc)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO servers (
	  server_key, display_name, base_url, enabled, sort_order, is_builtin, created_utc, updated_utc, last_used_utc
)
VALUES (
	  $k, $n, $u, $e, $s, $b, $c, $m, $lu
)
ON CONFLICT(server_key) DO UPDATE SET
  display_name = CASE WHEN length(trim(servers.display_name)) = 0 THEN excluded.display_name ELSE servers.display_name END,
  base_url = CASE WHEN length(trim(excluded.base_url)) > 0 THEN excluded.base_url ELSE servers.base_url END,
  enabled = excluded.enabled,
  sort_order = excluded.sort_order,
  is_builtin = excluded.is_builtin,
	  last_used_utc = COALESCE(servers.last_used_utc, excluded.last_used_utc),
	  updated_utc = excluded.updated_utc;";
            cmd.Parameters.AddWithValue("$k", serverKey);
            cmd.Parameters.AddWithValue("$n", displayName);
            cmd.Parameters.AddWithValue("$u", baseUrl ?? string.Empty);
            cmd.Parameters.AddWithValue("$e", enabled);
            cmd.Parameters.AddWithValue("$s", sortOrder);
            cmd.Parameters.AddWithValue("$b", isBuiltin);
            cmd.Parameters.AddWithValue("$c", nowUtc);
            cmd.Parameters.AddWithValue("$m", nowUtc);
	            cmd.Parameters.AddWithValue("$lu", nowUtc);
            cmd.ExecuteNonQuery();
        }

        private void TouchSelectedServerUrl(string serverUrl)
        {
            var cleaned = (serverUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
                return;

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE servers
SET base_url = CASE
    WHEN server_key = $joe THEN $joeUrl
    WHEN server_key = $custom THEN $customUrl
    ELSE base_url
  END,
  last_used_utc = $now,
  updated_utc = $now
WHERE server_key IN ($joe, $custom);";
            cmd.Parameters.AddWithValue("$joe", BuiltinJoeKey);
            cmd.Parameters.AddWithValue("$custom", BuiltinCustomKey);
            cmd.Parameters.AddWithValue("$joeUrl", BuiltinJoeUrl);
            cmd.Parameters.AddWithValue("$customUrl", cleaned == BuiltinJoeUrl ? string.Empty : cleaned);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }



        public IReadOnlyList<ServerDirectoryEntry> GetCachedDirectoryServers()
        {
            EnsureInitialized();

            var list = new List<ServerDirectoryEntry>();

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT directory_id, display_name, base_url, info_url, area_label, map_anchor, is_official, badge, badge_label
FROM servers
WHERE source = 'directory'
ORDER BY is_official DESC, sort_order ASC, display_name ASC;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var entry = new ServerDirectoryEntry
                {
                    DirectoryId = SafeGetInt(reader, 0),
                    Name = SafeGetString(reader, 1),
                    Url = SafeGetString(reader, 2),
                    InfoUrl = SafeGetString(reader, 3),
                    AreaLabel = SafeGetString(reader, 4),
                    MapAnchor = SafeGetString(reader, 5),
                    IsOfficial = SafeGetInt(reader, 6) != 0,
                    Badge = SafeGetString(reader, 7),
                    BadgeLabel = SafeGetString(reader, 8)
                };

                // Skip entries that do not have a usable URL.
                if (string.IsNullOrWhiteSpace(entry.Url))
                    continue;

                list.Add(entry);
            }

            return list;
        }

        public void UpsertCachedDirectoryServers(IEnumerable<ServerDirectoryEntry> servers)
        {
            EnsureInitialized();

            var incoming = (servers ?? Array.Empty<ServerDirectoryEntry>()).ToList();

            // Normalize and filter up front.
            var normalized = new List<(string ServerKey, ServerDirectoryEntry Entry)>();

            foreach (var s in incoming)
            {
                if (s == null)
                    continue;

                var url = CanonicalizeBaseUrl(s.Url);
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                s.Url = url;

                // Directory entries use a stable key based on the URL.
                var key = "dir::" + NormalizeServerKey(url);
                if (string.IsNullOrWhiteSpace(key) || key == "dir::")
                    continue;

                normalized.Add((key, s));
            }

            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            // Upsert all incoming.
            for (var i = 0; i < normalized.Count; i++)
            {
                var (serverKey, entry) = normalized[i];
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO servers (
  server_key, display_name, base_url, enabled, sort_order, is_builtin, created_utc, updated_utc, last_used_utc,
  info_url, area_label, map_anchor, is_official, directory_id, badge, badge_label, source
)
VALUES (
  $k, $n, $u, 1, $s, 0, $c, $m, NULL,
  $info, $area, $anchor, $official, $dirId, $badge, $badgeLabel, 'directory'
)
ON CONFLICT(server_key) DO UPDATE SET
  display_name = excluded.display_name,
  base_url = excluded.base_url,
  enabled = excluded.enabled,
  sort_order = excluded.sort_order,
  updated_utc = excluded.updated_utc,
  info_url = excluded.info_url,
  area_label = excluded.area_label,
  map_anchor = excluded.map_anchor,
  is_official = excluded.is_official,
  directory_id = excluded.directory_id,
  badge = excluded.badge,
  badge_label = excluded.badge_label,
  source = excluded.source;";

                cmd.Parameters.AddWithValue("$k", serverKey);
                cmd.Parameters.AddWithValue("$n", (entry.Name ?? string.Empty).Trim());
                cmd.Parameters.AddWithValue("$u", entry.Url);
                cmd.Parameters.AddWithValue("$s", 100 + i);
                cmd.Parameters.AddWithValue("$c", now);
                cmd.Parameters.AddWithValue("$m", now);
                cmd.Parameters.AddWithValue("$info", (entry.InfoUrl ?? string.Empty).Trim());
                cmd.Parameters.AddWithValue("$area", (entry.AreaLabel ?? string.Empty).Trim());
                cmd.Parameters.AddWithValue("$anchor", (entry.MapAnchor ?? string.Empty).Trim());
                cmd.Parameters.AddWithValue("$official", entry.IsOfficial ? 1 : 0);
                cmd.Parameters.AddWithValue("$dirId", entry.DirectoryId);
                cmd.Parameters.AddWithValue("$badge", (entry.Badge ?? string.Empty).Trim());
                cmd.Parameters.AddWithValue("$badgeLabel", (entry.BadgeLabel ?? string.Empty).Trim());

                cmd.ExecuteNonQuery();
            }

            // Remove directory entries that no longer exist.
            // Keep built-ins and any non-directory rows.
            if (normalized.Count == 0)
            {
                using var delAll = conn.CreateCommand();
                delAll.Transaction = tx;
                delAll.CommandText = "DELETE FROM servers WHERE source = 'directory';";
                delAll.ExecuteNonQuery();
            }
            else
            {
                var keys = normalized.Select(x => x.ServerKey).Distinct(StringComparer.Ordinal).ToList();

                // Build a safe IN clause with parameters.
                var placeholders = string.Join(",", keys.Select((_, idx) => "$k" + idx));
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM servers WHERE source = 'directory' AND server_key NOT IN ({placeholders});";
                for (var i = 0; i < keys.Count; i++)
                {
                    del.Parameters.AddWithValue("$k" + i, keys[i]);
                }
                del.ExecuteNonQuery();
            }

            tx.Commit();
        }

        public bool TryGetMapAnchorForServerUrl(string serverUrl, out string mapAnchor)
        {
            mapAnchor = string.Empty;
            EnsureInitialized();

            var url = CanonicalizeBaseUrl(serverUrl);
            if (string.IsNullOrWhiteSpace(url))
                return false;

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT map_anchor
FROM servers
WHERE lower(trim(base_url)) = lower(trim($u))
LIMIT 1;";
            cmd.Parameters.AddWithValue("$u", url);

            var result = cmd.ExecuteScalar();
            if (result == null)
                return false;

            var s = (result?.ToString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(s))
                return false;

            mapAnchor = s;
            return true;
        }

        private static string CanonicalizeBaseUrl(string? url)
        {
            var s = (url ?? string.Empty).Trim();
            if (s.Length == 0)
                return string.Empty;

            // Keep scheme and host exactly as provided, but remove trailing slashes.
            return s.TrimEnd('/');
        }

        private static string SafeGetString(SqliteDataReader reader, int ordinal)
        {
            try
            {
                return reader.IsDBNull(ordinal) ? string.Empty : (reader.GetString(ordinal) ?? string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int SafeGetInt(SqliteDataReader reader, int ordinal)
        {
            try
            {
                return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
            }
            catch
            {
                try
                {
                    if (reader.IsDBNull(ordinal))
                        return 0;

                    var raw = reader.GetValue(ordinal)?.ToString() ?? "0";
                    return int.TryParse(raw, out var v) ? v : 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        private string GetString(string key, string defaultValue)
        {
            EnsureInitialized();
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var v))
                    return v;
            }
            return defaultValue;
        }

        private bool GetBool(string key, bool defaultValue)
        {
            var raw = GetString(key, defaultValue ? "true" : "false");
            return bool.TryParse(raw, out var b) ? b : defaultValue;
        }

        private int GetInt(string key, int defaultValue)
        {
            var raw = GetString(key, defaultValue.ToString(CultureInfo.InvariantCulture));
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
        }

        private void SetInt(string key, int value)
        {
            SetString(key, value.ToString(CultureInfo.InvariantCulture));

            if (key.StartsWith("address_detection_", StringComparison.OrdinalIgnoreCase))
                OnAddressDetectionSettingsChanged();

        }

        private void SetString(string key, string value)
        {
            EnsureInitialized();
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            lock (_gate)
            {
                _cache[key] = value ?? string.Empty;
            }

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO app_settings (setting_key, setting_value, updated_utc)
VALUES ($k, $v, $u)
ON CONFLICT(setting_key) DO UPDATE SET
  setting_value = excluded.setting_value,
  updated_utc = excluded.updated_utc;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value ?? string.Empty);
            cmd.Parameters.AddWithValue("$u", now);
            cmd.ExecuteNonQuery();
        }

        private void SetBool(string key, bool value)
        {
            SetString(key, value ? "true" : "false");

            if (key.StartsWith("address_detection_", StringComparison.OrdinalIgnoreCase))
                OnAddressDetectionSettingsChanged();

        
            if (string.Equals(key, KeyWhat3WordsLinksEnabled, StringComparison.OrdinalIgnoreCase))
                OnWhat3WordsSettingsChanged();

            if (string.Equals(key, KeyWhat3WordsApiKey, StringComparison.OrdinalIgnoreCase))
                OnWhat3WordsSettingsChanged();

        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private void Remove(string key)
        {
            EnsureInitialized();
            lock (_gate)
            {
                _cache.Remove(key);
            }

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {TableAppSettings} WHERE setting_key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }

        private string GetAuthContextKey()
        {
            // Subscription/auth state is scoped to the configured server URL.
            // IMPORTANT: Normalize to scheme + host (+ port) only so:
            //   - credentials saved for https://host/path still match https://host
            //   - auth state isn't accidentally split by path/query variations
            // If there is no configured server URL, treat auth context as empty.
            var url = GetString(KeyServerUrl, string.Empty);
            return NormalizeServerKey(url);
        }

        private static string NormalizeServerKey(string serverKey)
        {
            var raw = (serverKey ?? string.Empty).Trim();
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

                // UriBuilder normalizes the host casing; always trim trailing slash.
                return builder.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return raw;
            }
        }

        // Legacy behavior (pre-2026-02-22): key was the raw URL with only whitespace and trailing slash removed.
        private static string LegacyNormalizeServerKey(string serverKey)
        {
            return (serverKey ?? string.Empty).Trim().TrimEnd('/');
        }

        private void DeleteServerAuthStateRow(string normalizedServerKey)
        {
            if (string.IsNullOrWhiteSpace(normalizedServerKey))
                return;

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM server_auth_state WHERE server_key = $k;";
            cmd.Parameters.AddWithValue("$k", normalizedServerKey);
            cmd.ExecuteNonQuery();
        }

        private static DateTime? ParseUtc(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var dt)
                ? dt
                : null;
        }

        private sealed class ServerAuthStateRow
        {
            public bool IsValidated { get; set; }
            public bool ValidatedOnline { get; set; }
            public string? ValidatedUtc { get; set; }
            public string? ExpiresUtc { get; set; }
            public string? LastAttemptUtc { get; set; }
            public int? LastStatusCode { get; set; }
            public string? LastError { get; set; }
            public string? LastCheckUtc { get; set; }
            public string? LastLevel { get; set; }
            public string? LastMessage { get; set; }
            public string? PriceId { get; set; }
            public int TierLevel { get; set; }
            public string? RenewalUtc { get; set; }

            public bool HasAnyValue
            {
                get
                {
                    return IsValidated
                        || ValidatedOnline
                        || !string.IsNullOrWhiteSpace(ValidatedUtc)
                        || !string.IsNullOrWhiteSpace(ExpiresUtc)
                        || !string.IsNullOrWhiteSpace(LastAttemptUtc)
                        || LastStatusCode.HasValue
                        || !string.IsNullOrWhiteSpace(LastError)
                        || !string.IsNullOrWhiteSpace(LastCheckUtc)
                        || !string.IsNullOrWhiteSpace(LastLevel)
                        || !string.IsNullOrWhiteSpace(LastMessage)
                        || !string.IsNullOrWhiteSpace(PriceId)
                        || TierLevel != 0
                        || !string.IsNullOrWhiteSpace(RenewalUtc);
                }
            }
        }

        private ServerAuthStateRow ReadServerAuthState(string serverKey)
        {
            EnsureInitialized();
            var key = NormalizeServerKey(serverKey);
            if (string.IsNullOrWhiteSpace(key))
                return new ServerAuthStateRow();

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT is_validated, validated_online, validated_utc, expires_utc, last_attempt_utc, last_status_code, last_error,
       last_check_utc, last_level, last_message, price_id, tier_level, renewal_utc
FROM server_auth_state
WHERE server_key = $k
LIMIT 1;";
            cmd.Parameters.AddWithValue("$k", key);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new ServerAuthStateRow
                {
                    IsValidated = reader.GetInt32(0) == 1,
                    ValidatedOnline = reader.GetInt32(1) == 1,
                    ValidatedUtc = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ExpiresUtc = reader.IsDBNull(3) ? null : reader.GetString(3),
                    LastAttemptUtc = reader.IsDBNull(4) ? null : reader.GetString(4),
                    LastStatusCode = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    LastError = reader.IsDBNull(6) ? null : reader.GetString(6),
                    LastCheckUtc = reader.IsDBNull(7) ? null : reader.GetString(7),
                    LastLevel = reader.IsDBNull(8) ? null : reader.GetString(8),
                    LastMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
                    PriceId = reader.IsDBNull(10) ? null : reader.GetString(10),
                    TierLevel = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                    RenewalUtc = reader.IsDBNull(12) ? null : reader.GetString(12)
                };
            }

            // Back-compat: attempt legacy key and migrate forward if found.
            try
            {
                var legacyKey = LegacyNormalizeServerKey(serverKey);
                if (!string.IsNullOrWhiteSpace(legacyKey) && !string.Equals(legacyKey, key, StringComparison.Ordinal))
                {
                    using var conn2 = OpenConnection();
                    using var cmd2 = conn2.CreateCommand();
                    cmd2.CommandText = cmd.CommandText;
                    cmd2.Parameters.AddWithValue("$k", legacyKey);

                    using var reader2 = cmd2.ExecuteReader();
                    if (reader2.Read())
                    {
                        var row = new ServerAuthStateRow
                        {
                            IsValidated = reader2.GetInt32(0) == 1,
                            ValidatedOnline = reader2.GetInt32(1) == 1,
                            ValidatedUtc = reader2.IsDBNull(2) ? null : reader2.GetString(2),
                            ExpiresUtc = reader2.IsDBNull(3) ? null : reader2.GetString(3),
                            LastAttemptUtc = reader2.IsDBNull(4) ? null : reader2.GetString(4),
                            LastStatusCode = reader2.IsDBNull(5) ? null : reader2.GetInt32(5),
                            LastError = reader2.IsDBNull(6) ? null : reader2.GetString(6),
                            LastCheckUtc = reader2.IsDBNull(7) ? null : reader2.GetString(7),
                            LastLevel = reader2.IsDBNull(8) ? null : reader2.GetString(8),
                            LastMessage = reader2.IsDBNull(9) ? null : reader2.GetString(9),
                            PriceId = reader2.IsDBNull(10) ? null : reader2.GetString(10),
                            TierLevel = reader2.IsDBNull(11) ? 0 : reader2.GetInt32(11),
                            RenewalUtc = reader2.IsDBNull(12) ? null : reader2.GetString(12)
                        };

                        if (row.HasAnyValue)
                        {
                            WriteServerAuthState(key, row);
                            DeleteServerAuthStateRow(legacyKey);
                        }

                        return row;
                    }
                }
            }
            catch
            {
            }

            return new ServerAuthStateRow();
        }

        private void WriteServerAuthState(string serverKey, ServerAuthStateRow row)
        {
            EnsureInitialized();
            var key = NormalizeServerKey(serverKey);
            if (string.IsNullOrWhiteSpace(key))
                return;

            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO server_auth_state (
  server_key,
  is_validated,
  validated_online,
  validated_utc,
  expires_utc,
  last_attempt_utc,
  last_status_code,
  last_error,
  last_check_utc,
  last_level,
  last_message,
  price_id,
  tier_level,
  renewal_utc,
  updated_utc
)
VALUES (
  $k,
  $ok,
  $on,
  $v,
  $x,
  $a,
  $sc,
  $e,
  $lc,
  $ll,
  $lm,
  $p,
  $t,
  $r,
  $u
)
ON CONFLICT(server_key) DO UPDATE SET
  is_validated = excluded.is_validated,
  validated_online = excluded.validated_online,
  validated_utc = excluded.validated_utc,
  expires_utc = excluded.expires_utc,
  last_attempt_utc = excluded.last_attempt_utc,
  last_status_code = excluded.last_status_code,
  last_error = excluded.last_error,
  last_check_utc = excluded.last_check_utc,
  last_level = excluded.last_level,
  last_message = excluded.last_message,
  price_id = excluded.price_id,
  tier_level = excluded.tier_level,
  renewal_utc = excluded.renewal_utc,
  updated_utc = excluded.updated_utc;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$ok", row.IsValidated ? 1 : 0);
            cmd.Parameters.AddWithValue("$on", row.ValidatedOnline ? 1 : 0);
            cmd.Parameters.AddWithValue("$v", (object?)row.ValidatedUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$x", (object?)row.ExpiresUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$a", (object?)row.LastAttemptUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sc", row.LastStatusCode.HasValue ? row.LastStatusCode.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)row.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lc", (object?)row.LastCheckUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ll", (object?)row.LastLevel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lm", (object?)row.LastMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$p", (object?)row.PriceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", row.TierLevel);
            cmd.Parameters.AddWithValue("$r", (object?)row.RenewalUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", now);
            cmd.ExecuteNonQuery();
        }
    }
}
