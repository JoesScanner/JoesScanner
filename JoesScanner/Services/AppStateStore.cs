using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

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

    private static SqliteConnection OpenConnection(bool durable = false)
    {
        var options = new SqliteConnectionStringBuilder { DataSource = GetDbPath() };
        if (durable)
            options.DefaultTimeout = 1;
        var connection = new SqliteConnection(options.ToString());
        try
        {
            connection.Open();
            DbBootstrapper.EnsureInitialized(connection);
            if (durable)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=250;";
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static T InTransaction<T>(Func<SqliteTransaction, T> operation, bool durable = false)
    {
        using var connection = OpenConnection(durable);
        using var transaction = connection.BeginTransaction();
        var result = operation(transaction);
        transaction.Commit();
        return result;
    }

    internal static bool TryWrite(Action<SqliteTransaction> write, Func<Action, bool> tryCommit)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        write(transaction);
        return tryCommit(transaction.Commit);
    }

    private static string? TryGetRaw(string key)
    {
        lock (Gate)
        {
            using var conn = OpenConnection();
            return ReadRaw(conn, null, key);
        }
    }

    private static void SetRaw(string key, string value)
    {
        lock (Gate)
        {
            using var conn = OpenConnection();
            WriteRaw(conn, null, key, value);
        }
    }

    internal static string? Read(SqliteTransaction transaction, string key) => ReadRaw(transaction.Connection!, transaction, key);

    private static string? ReadRaw(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT setting_value FROM {TableName} WHERE setting_key = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        var value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    internal static void Write(SqliteTransaction transaction, string key, string value) =>
        WriteRaw(transaction.Connection!, transaction, key, value);

    private static void WriteRaw(SqliteConnection connection, SqliteTransaction? transaction, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $@"
INSERT INTO {TableName} (setting_key, setting_value, updated_utc)
VALUES ($k, $v, $u)
ON CONFLICT(setting_key) DO UPDATE SET
 setting_value = excluded.setting_value,
 updated_utc = excluded.updated_utc;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
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
