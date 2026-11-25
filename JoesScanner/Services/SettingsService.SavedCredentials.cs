using JoesScanner.Helpers;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace JoesScanner.Services;

public sealed partial class SettingsService
{
    private const string CredentialInstallationKey = "credential_storage_id";
    private const string LegacyUserPrefix = "server_cred_user::";
    private const string LegacyPassPrefix = "server_cred_pass::";
    private const string What3WordsScope = "what3words";
    private const string UnscopedBasicAuthScope = "legacy-unscoped-basic-auth";
    private const string LegacyCredentialReentryKey = "legacy_basic_auth_reentry_scope";
    private const string LegacyCredentialReentryMessage =
        "The previous app did not identify which server used the saved login. Enter the username and password for this server again in Settings.";
    private readonly ProtectedCredentials _protectedCredentials;
    private readonly Dictionary<string, string?> _credentialCache = new();
    private readonly HashSet<string> _unverifiedCredentialWrites = new();
    private bool _credentialMigrationPending;
    private long _nextCredentialMigrationAttempt;
    private bool _unscopedBasicAuthPending = true;
    private long _nextUnscopedBasicAuthAttempt;
    private string? _savedCredentialWarning;

    public string? SavedCredentialWarning
    {
        get
        {
            EnsureInitialized();
            lock (_gate)
            {
                if (_savedCredentialWarning != null)
                    return _savedCredentialWarning;
                if (!_cache.TryGetValue(LegacyCredentialReentryKey, out var notice) ||
                    !string.Equals(notice, LegacyCredentialReentryScope(), StringComparison.Ordinal))
                    return null;

                return GetSelectedCredentials() == null
                    ? _savedCredentialWarning ?? LegacyCredentialReentryMessage
                    : null;
            }
        }
    }

    private string? LegacyCredentialReentryScope()
    {
        try
        {
            return CredentialScope(_cache.GetValueOrDefault(KeyServerUrl), _cache.GetValueOrDefault(KeyAuthServerBaseUrl)) is { } scope
                ? CredentialKey(scope) : null;
        }
        catch (CredentialStorageException)
        {
            return null;
        }
    }

    private static bool SafeAccountBaseChanged(string previous, string current) =>
        (previous.Length == 0 ? SavedConnectionUrl.DefaultAccountBase : previous) !=
        (current.Length == 0 ? SavedConnectionUrl.DefaultAccountBase : current);

    private void WriteConnectionSetting(string key, string value) =>
        WriteSettingRaw(key, value, DateTimeOffset.UtcNow.ToString("O"));

    private static bool IsLegacySecret(string key) =>
        key is KeyBasicAuthUser or KeyBasicAuthPass or KeyLastAuthUser or KeyWhat3WordsApiKey ||
        key.StartsWith(LegacyUserPrefix, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(LegacyPassPrefix, StringComparison.OrdinalIgnoreCase);

    private static string? CredentialScope(string? server, string? accountBase)
    {
        var origin = SavedConnectionUrl.Scope(server);
        if (origin == null)
            return null;
        if (!HostedServerRules.IsHostedStreamServerUrl(origin))
            return "recorder:" + origin;
        if (origin != HostedServerRules.DefaultServerUrl)
            return null;
        return "account:" + SavedConnectionUrl.AccountEndpoint(accountBase).AbsoluteUri;
    }

    private string CredentialKey(string scope) => ProtectedCredentials.Key(_cache[CredentialInstallationKey], scope);

    private string? ReadCredential(string scope)
    {
        var key = CredentialKey(scope);
        if (_unverifiedCredentialWrites.Contains(key))
            throw new CredentialStorageException();
        if (!_credentialCache.TryGetValue(key, out var value))
        {
            value = _protectedCredentials.Read(key);
            if (value == null && _credentialMigrationPending)
                throw new CredentialStorageException();
            _credentialCache[key] = value;
        }
        return value;
    }

    private void SaveCredential(string scope, string? value)
    {
        var key = CredentialKey(scope);
        _credentialCache.Remove(key);
        _unverifiedCredentialWrites.Add(key);
        if (value == null)
            _protectedCredentials.Remove(key);
        else
            _protectedCredentials.Save(key, value);
        _unverifiedCredentialWrites.Remove(key);
        _credentialCache[key] = value;
        Volatile.Write(ref _savedCredentialWarning, null);
    }

    private void ReportCredentialError()
    {
        Volatile.Write(ref _savedCredentialWarning, new CredentialStorageException().Message);
    }

    private ProtectedCredentials.Pair? GetSelectedCredentials()
    {
        try
        {
            return TryGetServerCredentials(ServerUrl, out var username, out var password) ? new(username, password) : null;
        }
        catch (CredentialStorageException) { return null; }
    }

    public string BasicAuthUsername => GetSelectedCredentials()?.Username ?? string.Empty;
    public string BasicAuthPassword => GetSelectedCredentials()?.Password ?? string.Empty;

    public bool TryGetServerCredentials(string serverUrl, out string username, out string password)
    {
        EnsureInitialized();
        username = password = string.Empty;
        lock (_gate)
        {
            try
            {
                TryMigrateLegacyCredentials();
                var scope = CredentialScope(serverUrl, AuthServerBaseUrl);
                if (scope == null || ProtectedCredentials.Decode(ReadCredential(scope)) is not { } pair)
                    return false;
                username = pair.Username;
                password = pair.Password;
                return true;
            }
            catch (CredentialStorageException)
            {
                ReportCredentialError();
                throw;
            }
        }
    }

    public void SetServerCredentials(string serverUrl, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            ClearServerCredentials(serverUrl);
            return;
        }
        PersistServerCredentials(serverUrl, new(username.Trim(), password ?? string.Empty));
    }

    public void ClearServerCredentials(string serverUrl) => PersistServerCredentials(serverUrl, null);

    private void PersistServerCredentials(string serverUrl, ProtectedCredentials.Pair? pair)
    {
        EnsureInitialized();
        lock (_gate)
        {
            try
            {
                RequireMigratedCredentials();
                var scope = CredentialScope(serverUrl, AuthServerBaseUrl) ?? throw new CredentialStorageException();
                var encoded = pair is { } credentials ? ProtectedCredentials.Encode(credentials) : null;
                var isAccount = scope.StartsWith("account:", StringComparison.Ordinal);
                if (!_unverifiedCredentialWrites.Contains(CredentialKey(scope)) && ReadCredential(scope) == encoded)
                {
                    if (pair == null && isAccount)
                    {
                        VerifiedFavoritesUserId = null;
                        RetireAccountIdentity();
                    }
                    return;
                }
                if (isAccount)
                    VerifiedFavoritesUserId = null;
                SaveCredential(scope, encoded);
                if (isAccount)
                    RetireAccountIdentity();
            }
            catch (CredentialStorageException)
            {
                ReportCredentialError();
                throw;
            }
        }
    }

    private static string RecorderCredentialScope(Guid serverId, string endpoint) =>
        "recorder-connection:" + serverId.ToString("N") + ":" +
        (SavedConnectionUrl.Scope(endpoint) ?? throw new CredentialStorageException());

    public bool TryGetRecorderCredentials(Guid serverId, string serverUrl, out string username, out string password)
    {
        EnsureInitialized();
        username = password = string.Empty;
        lock (_gate)
        {
            try
            {
                if (ProtectedCredentials.Decode(ReadCredential(RecorderCredentialScope(serverId, serverUrl))) is not { } pair)
                    return false;

                username = pair.Username;
                password = pair.Password;
                return true;
            }
            catch (CredentialStorageException)
            {
                ReportCredentialError();
                throw;
            }
        }
    }

    public void SetRecorderCredentials(Guid serverId, string serverUrl, string username, string password)
    {
        EnsureInitialized();
        lock (_gate)
        {
            try
            {
                RequireMigratedCredentials();
                var scope = RecorderCredentialScope(serverId, serverUrl);
                var encoded = string.IsNullOrWhiteSpace(username) ? null :
                    ProtectedCredentials.Encode(new(username.Trim(), password ?? string.Empty));
                if (_unverifiedCredentialWrites.Contains(CredentialKey(scope)) || ReadCredential(scope) != encoded)
                    SaveCredential(scope, encoded);
            }
            catch (CredentialStorageException)
            {
                ReportCredentialError();
                throw;
            }
        }
    }

    public string What3WordsApiKey
    {
        get
        {
            EnsureInitialized();
            lock (_gate)
            {
                try
                {
                    TryMigrateLegacyCredentials();
                    return ReadCredential(What3WordsScope) ?? string.Empty;
                }
                catch (CredentialStorageException)
                {
                    ReportCredentialError();
                    return string.Empty;
                }
            }
        }
        set
        {
            EnsureInitialized();
            var cleaned = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            lock (_gate)
            {
                try
                {
                    RequireMigratedCredentials();
                    if (_unverifiedCredentialWrites.Contains(CredentialKey(What3WordsScope)) || ReadCredential(What3WordsScope) != cleaned)
                        SaveCredential(What3WordsScope, cleaned);
                }
                catch (CredentialStorageException)
                {
                    ReportCredentialError();
                    throw;
                }
            }
        }
    }

    private void RequireMigratedCredentials()
    {
        TryMigrateLegacyCredentials(force: true);
        if (_credentialMigrationPending)
            throw new CredentialStorageException();
    }

    private void TryMigrateLegacyCredentials(bool force = false)
    {
        TryPreserveUnscopedBasicAuth();
        if (!_credentialMigrationPending || (!force && Environment.TickCount64 < _nextCredentialMigrationAttempt))
            return;
        try
        {
            MigrateLegacyCredentials();
            _credentialMigrationPending = false;
            _credentialCache.Clear();
            Volatile.Write(ref _savedCredentialWarning, null);
        }
        catch (Exception ex) when (ex is CredentialStorageException or SqliteException)
        {
            _nextCredentialMigrationAttempt = Environment.TickCount64 + 10_000;
            ReportCredentialError();
        }
    }

    private void TryPreserveUnscopedBasicAuth()
    {
        if (!_unscopedBasicAuthPending || Environment.TickCount64 < _nextUnscopedBasicAuthAttempt)
            return;

        try
        {
            using var db = OpenConnection();
            using var command = db.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = "PRAGMA secure_delete=ON;";
            command.ExecuteNonQuery();
            using var transaction = db.BeginTransaction(deferred: true);
            command.Transaction = transaction;
            command.CommandText = "SELECT setting_key, setting_value FROM app_settings WHERE setting_key IN ($user, $pass);";
            command.Parameters.AddWithValue("$user", KeyBasicAuthUser);
            command.Parameters.AddWithValue("$pass", KeyBasicAuthPass);
            string? user = null;
            string? password = null;
            var unreadable = false;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetValue(1) is not string value)
                    {
                        unreadable = true;
                        continue;
                    }
                    if (reader.GetString(0) == KeyBasicAuthUser)
                        user = value;
                    else
                        password = value;
                }
            }
            if (!unreadable && user == null && password == null)
            {
                _unscopedBasicAuthPending = false;
                return;
            }

            var preserved = true;
            if (unreadable)
            {
                HadUnreadableSettings = true;
            }
            else if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(password))
            {
                // The selection is only a hint for the re-entry notice. Only
                // per-server rows and URL user info can identify a recorder.
                if (!_cache.ContainsKey(LegacyCredentialReentryKey) && LegacyCredentialReentryScope() is { } scope)
                    _cache[LegacyCredentialReentryKey] = scope;
                try
                {
                    _protectedCredentials.Save(CredentialKey(UnscopedBasicAuthScope),
                        ProtectedCredentials.Encode(new(user ?? "", password ?? "")));
                }
                catch (CredentialStorageException)
                {
                    preserved = false;
                }
            }

            if (preserved)
            {
                command.CommandText = "DELETE FROM app_settings WHERE setting_key IN ($user, $pass);";
                command.ExecuteNonQuery();
            }
            if (_cache.TryGetValue(LegacyCredentialReentryKey, out var notice))
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO app_settings(setting_key, setting_value, updated_utc)
                    VALUES ($key, $notice, $now) ON CONFLICT(setting_key) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$key", LegacyCredentialReentryKey);
                command.Parameters.AddWithValue("$notice", notice);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            _unscopedBasicAuthPending = !preserved;
        }
        catch (SqliteException)
        {
            // Leave these rows available for retry. This transaction is
            // independent of credentials with a confirmed server scope.
        }
        _nextUnscopedBasicAuthAttempt = Environment.TickCount64 + 10_000;
    }

    private sealed record UrlRow(string Table, string Key, string Url, string InfoUrl);

    private void MigrateLegacyCredentials()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction(deferred: true);
        command.Transaction = transaction;
        command.CommandText = "SELECT setting_key, setting_value FROM app_settings;";
        var legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetValue(0) is not string key || key is KeyBasicAuthUser or KeyBasicAuthPass)
                    continue;
                if (IsLegacySecret(key) || key is KeyServerUrl or KeyAuthServerBaseUrl)
                {
                    if (reader.GetValue(1) is string value)
                        legacy[key] = value;
                    else
                        unreadable.Add(key);
                }
            }
        }

        var urls = new List<UrlRow>();
        foreach (var table in new[] { TableServers, TableServerRuntimeState, TableServerAuthState })
        {
            command.CommandText = table == TableServers
                ? "SELECT server_key, base_url, info_url FROM servers;"
                : $"SELECT server_key, '', '' FROM {table};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetValue(0) is not string key)
                {
                    HadUnreadableSettings = true;
                    continue;
                }
                var url = reader.GetValue(1) as string;
                var info = reader.GetValue(2) as string;
                HadUnreadableSettings |= url == null || info == null;
                urls.Add(new(table, key, url ?? "", info ?? ""));
            }
        }

        var values = new Dictionary<string, string>();
        var authBase = SavedConnectionUrl.SafeDisplay(legacy.GetValueOrDefault(KeyAuthServerBaseUrl));
        var selected = legacy.GetValueOrDefault(KeyServerUrl, "");
        var unassigned = new Dictionary<string, string>();

        string? LegacyScope(string server)
        {
            try { return CredentialScope(server, authBase); }
            catch (CredentialStorageException) { return null; }
        }

        void KeepPair(string? scope, string source, ProtectedCredentials.Pair pair)
        {
            if (scope != null && !string.IsNullOrWhiteSpace(pair.Username))
            {
                values.TryAdd(scope, ProtectedCredentials.Encode(pair));
                return;
            }
            unassigned[source] = ProtectedCredentials.Encode(pair);
        }

        foreach (var suffix in legacy.Keys.Where(key => key.StartsWith(LegacyUserPrefix, StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith(LegacyPassPrefix, StringComparison.OrdinalIgnoreCase)).Select(key => key[(key.IndexOf("::", StringComparison.Ordinal) + 2)..])
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(key => key != SavedConnectionUrl.Scope(key)))
        {
            if (unreadable.Contains(LegacyUserPrefix + suffix) || unreadable.Contains(LegacyPassPrefix + suffix))
                continue;
            var pair = new ProtectedCredentials.Pair(legacy.GetValueOrDefault(LegacyUserPrefix + suffix, ""),
                legacy.GetValueOrDefault(LegacyPassPrefix + suffix, ""));
            if (pair.Username.Length != 0 || pair.Password.Length != 0)
                KeepPair(LegacyScope(suffix), "server:" + suffix, pair);
        }

        foreach (var raw in urls.Where(row => row.Table == TableServers).Select(row => row.Url).Prepend(selected))
        {
            if (SavedConnectionUrl.TryParse(raw, out var uri, out _, out var embedded) && embedded is { } pair)
                KeepPair(HostedServerRules.IsHostedServerUri(uri) ? null : LegacyScope(raw), "embedded:" + raw, pair);
        }

        if (legacy.TryGetValue(KeyWhat3WordsApiKey, out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
            values.Add(What3WordsScope, apiKey);

        var cleanUrls = new List<(UrlRow Old, UrlRow New)>();
        foreach (var row in urls)
        {
            var prefix = row.Key.StartsWith("dir::", StringComparison.Ordinal) ? "dir::" : "";
            var rawKey = row.Key[prefix.Length..];
            var safeKey = SavedConnectionUrl.Scope(rawKey) is { } origin
                ? prefix + (prefix.Length == 0 ? SavedConnectionUrl.SafeDisplay(rawKey) : origin)
                : row.Key;
            var safeUrl = SavedConnectionUrl.SafeDisplay(row.Url);
            var safeInfoUrl = SavedConnectionUrl.SafeInfoUrl(row.InfoUrl);
            var cleaned = new UrlRow(row.Table, safeKey, safeUrl, safeInfoUrl);
            if (cleaned == row)
                continue;
            cleanUrls.Add((row, cleaned));
            PreserveUnsupportedUrl("key", rawKey, safeKey.Length == 0 ? rawKey : safeKey[prefix.Length..]);
            PreserveUnsupportedUrl("info", row.InfoUrl, safeInfoUrl);
            PreserveUnsupportedUrl("base", row.Url, safeUrl);
        }
        foreach (var setting in new[] { KeyServerUrl, KeyAuthServerBaseUrl })
        {
            if (legacy.TryGetValue(setting, out var raw))
                PreserveUnsupportedUrl(setting, raw, SavedConnectionUrl.SafeDisplay(raw));
        }

        void PreserveUnsupportedUrl(string label, string raw, string safe)
        {
            if (raw.Length == 0 || raw.TrimEnd('/').Equals(safe, StringComparison.Ordinal))
                return;
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var withoutUserInfo = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
                if (!Uri.TryCreate(safe, UriKind.Absolute, out var clean) ||
                    withoutUserInfo.Query != clean.Query || withoutUserInfo.Fragment.Length != 0)
                    unassigned[label + ":" + raw] = withoutUserInfo.AbsoluteUri;
            }
            else if (safe.Length == 0)
                unassigned[label + ":" + raw] = raw;
        }

        // Preserve unscoped or superseded legacy data in protected storage without
        // allowing it to authenticate to any recorder or WordPress endpoint.
        foreach (var (source, value) in unassigned)
            values["legacy:" + ProtectedCredentials.Key("", source)] = JsonSerializer.Serialize(value);

        foreach (var (scope, value) in values)
        {
            var key = CredentialKey(scope);
            // A failed preference commit can still change the process's in-memory
            // view of the store. Confirm a durable write again before SQLite cleanup.
            var persisted = _protectedCredentials.Read(key);
            _unverifiedCredentialWrites.Add(key);
            _protectedCredentials.Save(key, persisted ?? value);
            _unverifiedCredentialWrites.Remove(key);
        }

        void Execute(string sql, params (string Key, string Value)[] parameters)
        {
            command.Parameters.Clear();
            command.CommandText = sql;
            foreach (var (key, value) in parameters)
                command.Parameters.AddWithValue(key, value);
            command.ExecuteNonQuery();
        }

        foreach (var (key, value) in legacy)
        {
            if (IsLegacySecret(key))
                Execute("DELETE FROM app_settings WHERE setting_key = $key;", ("$key", key));
            else if (SavedConnectionUrl.SafeDisplay(value) is var safe && safe != value)
                Execute("UPDATE app_settings SET setting_value = $value WHERE setting_key = $key;",
                    ("$key", key), ("$value", safe));
        }
        foreach (var key in unreadable)
            Execute("DELETE FROM app_settings WHERE setting_key = $key;", ("$key", key));
        foreach (var (row, clean) in cleanUrls)
        {
            if (row.Table == TableServers)
                Execute("UPDATE OR IGNORE servers SET server_key=$new, base_url=$url, info_url=$info WHERE server_key=$old;",
                    ("$old", row.Key), ("$new", clean.Key), ("$url", clean.Url), ("$info", clean.InfoUrl));
            else
                Execute($"UPDATE OR IGNORE {row.Table} SET server_key=$new WHERE server_key=$old;", ("$old", row.Key), ("$new", clean.Key));
            if (clean.Key != row.Key)
                Execute($"DELETE FROM {row.Table} WHERE server_key=$old;", ("$old", row.Key));
        }
        transaction.Commit();
        HadUnreadableSettings |= unreadable.Count > 0;
    }
}
