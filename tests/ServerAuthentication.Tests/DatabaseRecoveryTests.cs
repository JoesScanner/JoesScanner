using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class DatabaseRecoveryTests
{
    private const string RecorderId = "ba0f8ee2d27140b0a27803a0f360d6c7";
    private const string Endpoint = "https://recovery.test/scanner";

    internal static Task SupportedUpgrade()
    {
        using var files = new Files();
        using var writer = Open(files.DbPath);
        Execute(writer, """
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=0;
            CREATE TABLE app_settings (setting_key TEXT PRIMARY KEY, setting_value TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE servers (server_key TEXT PRIMARY KEY, base_url TEXT NOT NULL);
            CREATE TABLE scanner_connections (
                server_id TEXT PRIMARY KEY, endpoint TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL,
                directory_id INTEGER UNIQUE, enabled INTEGER NOT NULL, credentials_migrated INTEGER NOT NULL, legacy_filter_key TEXT);
            CREATE TABLE filter_profiles (
                profile_id TEXT PRIMARY KEY, name TEXT NOT NULL, created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL, json_payload TEXT NOT NULL);
            INSERT INTO app_settings VALUES ('autoplay_enabled', 'true', ''), ('telemetry_enabled', 'false', ''),
                ('theme_mode', 'Dark', ''), ('server_url', $endpoint, '');
            INSERT INTO servers VALUES ('https://recovery.test', $endpoint);
            INSERT INTO scanner_connections VALUES ($id, $endpoint, 'Synthetic Recorder', NULL, 1, 1, NULL);
            INSERT INTO filter_profiles VALUES ('legacy-profile', 'Legacy filter', '', '',
                '{"serverKey":"https://recovery.test","filters":{"sites":["county"]},"rules":[]}');
            """, ("$id", RecorderId), ("$endpoint", Endpoint));
        Check(File.Exists(files.DbPath + "-wal"), "The legacy settings must be present with WAL still open.");

        Check(!DatabaseRecovery.PrepareMain(files.DbPath), "Supported upgrades must succeed without using corruption recovery.");
        var backup = Directory.GetFiles(files.RecoveryDirectory, "*.before-v0-v5.db").Single();
        using (var old = Open(backup))
        {
            Check(Equals(Execute(old, "PRAGMA user_version;"), 0L) &&
                Equals(Execute(old, "SELECT setting_value FROM app_settings WHERE setting_key='theme_mode';"), "Dark") &&
                Equals(Execute(old, "SELECT server_id FROM scanner_connections;"), RecorderId),
                "The SQLite migration snapshot must retain the pre-upgrade version and committed WAL data.");
        }
        Check(!File.Exists(backup + "-wal"), "The snapshot must be usable by itself without original sidecars.");

        var loaded = files.Load();
        Check(loaded.AutoPlay && loaded.LocalOnlyMode && loaded.ThemeMode == "Dark" && loaded.ServerUrl == Endpoint,
            "Upgrade must preserve AutoPlay, telemetry opt-out, theme and recorder selection.");
        Check(new ScannerConnectionStore(files).Load(Endpoint).Single().Id == Guid.ParseExact(RecorderId, "N"),
            "Existing saved recorder identity must survive schema upgrade.");
        Check(Equals(Execute(writer, "SELECT filters_json FROM filter_profiles;"), """{"sites":["county"]}""") &&
            Equals(Execute(writer, "SELECT server_key FROM filter_profiles;"), "https://recovery.test"),
            "Supported legacy profile payload must be migrated into the current columns.");
        return Task.CompletedTask;
    }

    internal static Task InterruptedTransaction()
    {
        using var files = new Files();
        using var db = Open(files.DbPath);
        Execute(db, """
            CREATE TABLE schema_meta (id INTEGER PRIMARY KEY, schema_version INTEGER NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TRIGGER fail_migration BEFORE INSERT ON schema_meta BEGIN SELECT RAISE(ABORT, 'fixture interruption'); END;
            """);
        try
        {
            DbBootstrapper.EnsureInitialized(db);
            throw new InvalidOperationException("The fixture must interrupt the upgrade after queue DDL.");
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { }

        Check(Equals(Execute(db, "PRAGMA user_version;"), 0L) &&
            Equals(Execute(db, "SELECT count(*) FROM sqlite_schema WHERE name IN ('app_settings', 'live_queue_recordings');"), 0L),
            "An interrupted migration must leave the schema version and all preceding DDL unchanged.");
        var backup = Directory.GetFiles(files.RecoveryDirectory, "*.before-v0-v5.db").Single();
        using (var old = Open(backup))
            Check(Equals(Execute(old, "SELECT count(*) FROM sqlite_schema WHERE name='fail_migration';"), 1L),
                "A consistent backup must exist even if migration fails.");

        Execute(db, "DROP TRIGGER fail_migration;");
        Check(!DatabaseRecovery.PrepareMain(files.DbPath) && Equals(Execute(db, "PRAGMA user_version;"), 5L) &&
            Directory.GetFiles(files.RecoveryDirectory, "*.before-v0-v5.db").Length == 1,
            "Restart must retry safely without replacing the original migration snapshot.");
        return Task.CompletedTask;
    }

    internal static Task PartialSettingsAndFavorites()
    {
        using var files = new Files();
        Check(!DatabaseRecovery.PrepareMain(files.DbPath), "The test starts with healthy storage.");
        var settings = files.Load();
        settings.SetRecorderCredentials(Guid.ParseExact(RecorderId, "N"), Endpoint, "saved-user", "saved-vault-fixture");
        using (var db = Open(files.DbPath))
        {
            Execute(db, """
                INSERT INTO app_settings VALUES ('telemetry_enabled', 'false', ''), ('theme_mode','Dark',''),
                    ('server_cred_user::https://recovery.test', 'obsolete-user',''),
                    ('server_cred_pass::https://recovery.test', X'ABCD',''),
                    ('server_cred_user::https://unaffected.test', 'legacy-other',''),
                    ('server_cred_pass::https://unaffected.test','other-fixture','');
                INSERT INTO scanner_connections(server_id,endpoint,display_name,enabled) VALUES ($id,$url,'Recovered',1);
                INSERT INTO filter_profiles(profile_id,name,server_key,created_utc,updated_utc)
                    VALUES ('kept-profile','Kept','https://recovery.test','','');
                INSERT INTO server_runtime_state(server_key,updated_utc) VALUES ('https://recovery.test','');
                """, ("$id", RecorderId), ("$url", Endpoint));
        }
        var withUnreadable = files.Load();
        Check(withUnreadable.ThemeMode == "Dark" && withUnreadable.HadUnreadableSettings &&
            withUnreadable.TryGetServerCredentials("https://unaffected.test", out var user, out _) && user == "legacy-other" &&
            !withUnreadable.TryGetServerCredentials(Endpoint, out _, out _),
            "A malformed legacy pair must not authenticate or block a different legacy login and unrelated settings.");
        Check(DatabaseRecovery.StartupNotice(false, false, withUnreadable.HadUnreadableSettings, null)?.Contains("reenter") == true,
            "Unreadable credentials must receive a nonblocking startup notice.");

        using (var db = Open(files.DbPath))
        {
            Execute(db, """
                DROP TABLE server_auth_state;
                CREATE TABLE server_auth_state (server_key TEXT PRIMARY KEY);
                """);
        }
        var favoriteDb = new FavoritesRepository(files);
        const string owner = "synthetic-owner";
        var group = FavoriteValue.Group("First local name");
        favoriteDb.Set(owner, group);
        var sent = favoriteDb.Prepare(owner, 100).Single();
        favoriteDb.Set(owner, group with { Label = "Offline edit" });
        using (var db = Open(Path.Combine(files.Root, "favorites.db")))
        {
            Execute(db, """
                INSERT INTO favorite_records(owner,id,remote) VALUES ('synthetic-owner','malformed','malformed JSON');
                DROP TABLE favorite_server_links;
                CREATE TABLE favorite_server_links (owner TEXT NOT NULL, server_id TEXT NOT NULL, PRIMARY KEY(owner,server_id));
                """);
        }
        SqliteConnection.ClearAllPools();
        Check(DatabaseRecovery.PrepareMain(files.DbPath), "The incompatible auth table must be quarantined.");
        Check(FavoritesRepository.PrepareStorage(files.DbPath), "The incompatible Favorites link table must be quarantined.");
        var restored = files.Load();
        Check(restored.LocalOnlyMode && restored.ThemeMode == "Dark" &&
            restored.TryGetRecorderCredentials(Guid.ParseExact(RecorderId, "N"), Endpoint, out user, out var password) &&
            user == "saved-user" && password == "saved-vault-fixture" &&
            new ScannerConnectionStore(files).Load(Endpoint).Single().Id == Guid.ParseExact(RecorderId, "N"),
            "Recovery must preserve the credential namespace, recorder ID, Local Only and other settings.");
        using (var db = Open(files.DbPath))
            Check(Equals(Execute(db, "SELECT profile_id FROM filter_profiles;"), "kept-profile") &&
                Equals(Execute(db, "SELECT count(*) FROM server_runtime_state;"), 1L),
                "Healthy filters and runtime state must be salvaged independently.");
        var favorites = new FavoritesRepository(files);
        var collection = favorites.Read(owner);
        Check(collection.HasPending && collection.Values.Single().Label == "Offline edit" &&
            favorites.Prepare(owner, 100).Single() == sent && favorites.Read("different-owner").Values.Length == 0,
            "Recovery must retain an immutable pending send and newer draft under the same account.");
        Check(!DatabaseRecovery.PrepareMain(files.DbPath) && !FavoritesRepository.PrepareStorage(files.DbPath),
            "A repaired database must reopen without repeating its failed migration.");
        return Task.CompletedTask;
    }

    internal static Task UnreadableDatabase()
    {
        using var files = new Files();
        var damaged = "This synthetic file cannot be decoded as SQLite."u8.ToArray();
        File.WriteAllBytes(files.DbPath, damaged);
        var download = Path.Combine(files.Root, "manual-download.wav");
        var export = Path.Combine(files.Root, "manual-export.txt");
        File.WriteAllText(download, "fixture audio");
        File.WriteAllText(export, "fixture export");

        Check(DatabaseRecovery.PrepareMain(files.DbPath), "Unrecoverable SQLite must create a usable database automatically.");
        var archive = Directory.GetDirectories(files.RecoveryDirectory).Single();
        Check(File.ReadAllBytes(Path.Combine(archive, Path.GetFileName(files.DbPath))).SequenceEqual(damaged),
            "The exact unreadable original must be retained for later recovery.");
        var settings = files.Load();
        using var current = Open(files.DbPath);
        Check(settings.LocalOnlyMode && !settings.AutoPlay && settings.ServerUrl == "" &&
            Equals(Execute(current, "PRAGMA integrity_check;"), "ok") &&
            File.ReadAllText(download) == "fixture audio" && File.ReadAllText(export) == "fixture export" &&
            DatabaseRecovery.StartupNotice(true, false, false, null)?.Contains("backup") == true &&
            !DatabaseRecovery.PrepareMain(files.DbPath),
            "Fresh startup must be usable, respect privacy, retain external files and show a concise recovery notice.");
        return Task.CompletedTask;
    }

    internal static Task InterruptedReplacementAndUnavailableDisk()
    {
        using var files = new Files();
        var original = "Unreadable original kept for manual salvage."u8.ToArray();
        Directory.CreateDirectory(files.RecoveryDirectory);
        var id = Guid.NewGuid().ToString("N");
        var archive = Directory.CreateDirectory(Path.Combine(files.RecoveryDirectory, $"joesscanner.db-{id}")).FullName;
        var marker = Path.Combine(files.RecoveryDirectory, "joesscanner.db.recovery");
        File.WriteAllText(marker, id);
        // Simulate termination after moving the SQLite family, before promoting the replacement.
        File.WriteAllBytes(Path.Combine(archive, "joesscanner.db"), original);
        File.WriteAllText(Path.Combine(archive, "joesscanner.db-wal"), "synthetic sidecar");
        using (var replacement = Open(Path.Combine(archive, "replacement.db")))
            DbBootstrapper.EnsureInitialized(replacement);
        Check(DatabaseRecovery.PrepareMain(files.DbPath) && !File.Exists(marker) && File.Exists(files.DbPath) &&
            File.ReadAllBytes(Path.Combine(archive, "joesscanner.db")).SequenceEqual(original),
            "Restart must finish the published replacement without consuming the original family.");
        DbBootstrapper.Forget(files.DbPath);

        using (var locked = Open(files.DbPath))
        {
            Execute(locked, "PRAGMA journal_mode=DELETE; BEGIN EXCLUSIVE;");
            try
            {
                DatabaseRecovery.PrepareMain(files.DbPath);
                throw new InvalidOperationException("The competing SQLite lock must temporarily block startup.");
            }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { }
            finally { Execute(locked, "ROLLBACK;"); }
        }

        var before = File.ReadAllBytes(files.DbPath);
        foreach (var code in new[] { 8, 10, 13, 14 })
        {
            try
            {
                DatabaseRecovery.Prepare(files.DbPath, _ => throw new SqliteException("Synthetic storage error", code), []);
                throw new InvalidOperationException("Temporary storage errors must be reported to the caller.");
            }
            catch (SqliteException error) when (error.SqliteErrorCode == code) { }
        }
        Check(before.SequenceEqual(File.ReadAllBytes(files.DbPath)) && Directory.GetDirectories(files.RecoveryDirectory).Length == 1,
            "Locks, permissions, I/O failure, or disk full must never trigger replacement.");

        using var newer = Open(files.DbPath);
        Execute(newer, "PRAGMA user_version=99;");
        try
        {
            DatabaseRecovery.PrepareMain(files.DbPath);
            throw new InvalidOperationException("A future schema must be reported to the caller.");
        }
        catch (NewerDatabaseException) { }
        Check(Equals(Execute(newer, "PRAGMA user_version;"), 99L) && Directory.GetDirectories(files.RecoveryDirectory).Length == 1,
            "Data from a newer version must remain untouched by recovery.");
        return Task.CompletedTask;
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 1 }.ToString());
        db.Open();
        return db;
    }

    private static object? Execute(SqliteConnection db, string sql, params (string Name, string Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Files : IDatabasePathProvider, IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("JoesScanner.Recovery-");
        private readonly SavedCredentialTests.VaultStub _vault = new();
        internal string Root => _root.FullName;
        internal string RecoveryDirectory => Path.Combine(Root, "DatabaseRecovery");
        public string DbPath => Path.Combine(Root, "joesscanner.db");
        internal SettingsService Load() => new(this, _vault);

        public void Dispose()
        {
            DbBootstrapper.Forget(DbPath);
            SqliteConnection.ClearAllPools();
            _root.Delete(recursive: true);
        }
    }
}
