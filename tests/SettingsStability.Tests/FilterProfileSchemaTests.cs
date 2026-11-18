using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class FilterProfileSchemaTests
{
    private const string Origin = "https://profiles.test";

    public static async Task RunAsync()
    {
        await VerifySchemaAsync("fresh database");
        await VerifySchemaAsync("split JSON before server scoping", splitJson: true);
        await VerifySchemaAsync("legacy bootstrap payload", legacyPayload: true);
    }

    private static async Task VerifySchemaAsync(string scenario, bool splitJson = false, bool legacyPayload = false)
    {
        var temp = Directory.CreateTempSubdirectory("JoesScanner.SettingsTests-");
        AppPaths.DatabaseDirectory = temp.FullName;
        var path = AppPaths.GetDbPath("joesscanner.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            if (splitJson || legacyPayload)
            {
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    PRAGMA user_version=1;
                    CREATE TABLE app_settings (setting_key TEXT PRIMARY KEY, setting_value TEXT NOT NULL, updated_utc TEXT NOT NULL);
                    INSERT INTO app_settings VALUES ('example_setting', 'preserved', '');
                    """;
                seed.ExecuteNonQuery();
                seed.CommandText = splitJson
                    ? """
                      CREATE TABLE filter_profiles (profile_id TEXT PRIMARY KEY, name TEXT NOT NULL, created_utc TEXT NOT NULL,
                          updated_utc TEXT NOT NULL, filters_json TEXT NOT NULL, rules_json TEXT NOT NULL);
                      INSERT INTO filter_profiles VALUES ('existing', 'Existing profile', '', '',
                          '{"receiverLabel":"Existing receiver"}', '[{"receiver":"Existing receiver","isMuted":true}]');
                      """
                    : """
                      CREATE TABLE filter_profiles (profile_id TEXT PRIMARY KEY, name TEXT NOT NULL, created_utc TEXT NOT NULL,
                          updated_utc TEXT NOT NULL, json_payload TEXT NOT NULL);
                      INSERT INTO filter_profiles VALUES ('existing', 'Existing profile', '', '',
                          '{"Filters":{"receiverLabel":"Existing receiver"},"Rules":[{"receiver":"Existing receiver","isMuted":true}]}');
                      INSERT INTO filter_profiles VALUES ('malformed', 'Malformed payload', '', '', 'preserve this legacy content');
                      """;
                seed.ExecuteNonQuery();

                seed.CommandText = "CREATE INDEX IF NOT EXISTS idx_filter_profiles_server_key ON filter_profiles(server_key);";
                try
                {
                    seed.ExecuteNonQuery();
                    throw new InvalidOperationException("The original index statement should fail before the schema repair.");
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.SqliteExtendedErrorCode == 1 &&
                    ex.Message.Contains("no such column: server_key", StringComparison.Ordinal))
                {
                    Console.WriteLine($"REPRO {scenario} rejected the original index statement (SQLite 1/1, missing server_key)");
                }
            }

            var store = new LocalFilterProfileStore();
            var profiles = await store.GetProfilesForServerAsync(Origin);
            if (splitJson || legacyPayload)
            {
                var profile = profiles.Single(p => p.Id == "existing");
                Check(profile.ServerKey == Origin && profile.Name == "Existing profile" &&
                      profile.Filters.ReceiverLabel == "Existing receiver" && profile.Rules.Single().IsMuted,
                    "A legacy profile lost its scoping, name, filters, or rules.");
                Check(ReadScalar(connection, "SELECT setting_value FROM app_settings WHERE setting_key='example_setting'") == "preserved",
                    "The schema repair modified an unrelated setting.");
            }
            else
                Check(profiles.Count == 0, "A fresh database should have no profiles.");

            if (legacyPayload)
                Check(ReadScalar(connection, "SELECT json_payload FROM filter_profiles WHERE profile_id='malformed'") == "preserve this legacy content",
                    "An unrecognized legacy payload should remain available.");

            await store.SaveOrUpdateAsync(new FilterProfile { Id = "new", Name = "New profile", ServerKey = Origin });
            await store.ImportAsync(new FilterProfileStoreEnvelope
            {
                Profiles = [new FilterProfile { Id = "imported", Name = "Imported profile", ServerKey = Origin }]
            }, merge: true);
            var reloaded = await new LocalFilterProfileStore().GetProfilesForServerAsync(Origin);
            Check(reloaded.Any(p => p.Id == "new") && reloaded.Any(p => p.Id == "imported"),
                "Saved and imported profiles must survive reopening the store.");
            Check((await new LocalFilterProfileStore().GetProfilesForServerAsync("https://other.test")).Count == 0,
                "Profiles must remain isolated by server.");
            Check(ReadScalar(connection, "SELECT name FROM pragma_index_info('idx_filter_profiles_server_key')") == "server_key",
                "The server index was not created after the column became available.");
            Console.WriteLine($"PASS Filter profiles support reads and writes with {scenario}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var databasePath = Path.GetFullPath(path);
            if (Path.GetDirectoryName(databasePath) == temp.FullName)
            {
                File.Delete(databasePath);
                File.Delete(databasePath + "-wal");
                File.Delete(databasePath + "-shm");
                temp.Delete();
            }
        }
    }

    private static string? ReadScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
