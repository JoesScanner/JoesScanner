using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Maui.Storage;

namespace JoesScanner.Services;

// DB-backed key/value store for app state.
// No legacy migration is supported. If a user upgrades from an older build, those
// older preference values will be ignored and they may need to re-enter settings.
internal static class AppStateStore
{
    private const string DbFileName = "joesscanner.db";
    private const string TableName = "app_settings";

    private static readonly object Gate = new();

    private static string GetDbPath()
    {
        return AppPaths.GetDbPath(DbFileName);
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={GetDbPath()}");
        connection.Open();
        DbBootstrapper.EnsureInitialized(connection);
        return connection;
    }

    private static string? TryGetRaw(string key)
    {
        lock (Gate)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT setting_value FROM {TableName} WHERE setting_key = $k LIMIT 1;";
            cmd.Parameters.AddWithValue("$k", key);

            var obj = cmd.ExecuteScalar();
            return obj == null || obj == DBNull.Value ? null : Convert.ToString(obj, CultureInfo.InvariantCulture);
        }
    }

    private static void SetRaw(string key, string value)
    {
        lock (Gate)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
INSERT INTO {TableName} (setting_key, setting_value, updated_utc)
VALUES ($k, $v, $u)
ON CONFLICT(setting_key) DO UPDATE SET
 setting_value = excluded.setting_value,
 updated_utc = excluded.updated_utc;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value ?? string.Empty);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    public static string GetString(string key, string defaultValue = "")
    {
        var raw = TryGetRaw(key);
        return raw ?? defaultValue;
    }

    public static void SetString(string key, string value)
    {
        SetRaw(key, value ?? string.Empty);
    }

    public static bool GetBool(string key, bool defaultValue = false)
    {
        var raw = TryGetRaw(key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (raw == "1")
            return true;
        if (raw == "0")
            return false;

        return bool.TryParse(raw, out var b) ? b : defaultValue;
    }

    public static void SetBool(string key, bool value)
    {
        SetRaw(key, value ? "1" : "0");
    }

    public static long GetLong(string key, long defaultValue = 0)
    {
        var raw = TryGetRaw(key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
    }

    public static void SetLong(string key, long value)
    {
        SetRaw(key, value.ToString(CultureInfo.InvariantCulture));
    }

    public static double GetDouble(string key, double defaultValue = 0)
    {
        var raw = TryGetRaw(key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
    }

    public static void SetDouble(string key, double value)
    {
        SetRaw(key, value.ToString(CultureInfo.InvariantCulture));
    }
}
