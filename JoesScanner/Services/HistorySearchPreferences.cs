using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

internal sealed record SavedHistoryChoice(string Value, string Label)
{
    internal static SavedHistoryChoice? From(HistoryLookupItem? item) =>
        string.IsNullOrWhiteSpace(item?.Value) ? null : new(item.Value, item.Label);

    internal HistoryLookupItem ForRecorder(Guid recorderId) => new() { Value = Value, Label = Label, RecorderId = recorderId };
}

internal sealed record SavedHistoryFilters(HistoryDateRange Dates, SavedHistoryChoice? Receiver = null,
    SavedHistoryChoice? Site = null, SavedHistoryChoice[]? Talkgroups = null)
{
    internal HistorySearchFilters ForRecorder(Guid id) => new()
    {
        Receiver = Receiver?.ForRecorder(id),
        Site = Site?.ForRecorder(id),
        Talkgroups = Talkgroups?.Select(item => item.ForRecorder(id)).ToArray() ?? []
    };
}

public sealed class HistorySearchPreferences(IDatabasePathProvider paths)
{
    private const string RecorderKey = "history_last_recorder";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal Guid? LastRecorder
    {
        get => Guid.TryParseExact(Read(RecorderKey), "N", out var id) ? id : null;
        set => Write(RecorderKey, value?.ToString("N") ?? string.Empty);
    }

    internal SavedHistoryFilters? ReadFilters(HistoryRecorderAccess access)
    {
        var scoped = ReadFilters("history_choices_" + access.MetadataKey);
        // Older choices contained recorder-supplied names without an archive/account.
        // Calendar preferences still belong to the user's saved recorder.
        var dates = ReadFilters(FilterKey(access.Recorder.Id))?.Dates;
        return scoped == null ? (dates == null ? null : new(dates)) : scoped with { Dates = dates ?? scoped.Dates };
    }

    private SavedHistoryFilters? ReadFilters(string key)
    {
        var json = Read(key);
        try { return json == null ? null : JsonSerializer.Deserialize<SavedHistoryFilters>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    internal void SaveFilters(HistoryRecorderAccess access, SavedHistoryFilters value)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        Write(db, transaction, FilterKey(access.Recorder.Id), JsonSerializer.Serialize(new SavedHistoryFilters(value.Dates), JsonOptions));
        Write(db, transaction, "history_choices_" + access.MetadataKey, JsonSerializer.Serialize(value, JsonOptions));
        access.TryPublish(transaction.Commit);
    }

    internal static SavedScannerConnection? ChooseRecorder(IReadOnlyList<SavedScannerConnection> saved, Guid? lastHistory,
        Guid? visibleLive, string? currentEndpoint)
    {
        var endpoint = ScannerConnectionStore.Normalize(currentEndpoint);
        return saved.FirstOrDefault(item => item.Id == lastHistory) ??
            saved.FirstOrDefault(item => item.Id == visibleLive) ??
            saved.FirstOrDefault(item => item.Endpoint == endpoint) ??
            saved.FirstOrDefault(item => item.Enabled) ?? saved.FirstOrDefault();
    }

    private static string FilterKey(Guid id) => "history_filters_" + id.ToString("N");

    private string? Read(string key)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT setting_value FROM app_settings WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private void Write(string key, string value)
    {
        using var db = Open();
        Write(db, null, key, value);
    }

    private static void Write(SqliteConnection db, SqliteTransaction? transaction, string key, string value)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_settings (setting_key, setting_value, updated_utc)
            VALUES ($key, $value, $now)
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = excluded.setting_value, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DbPath }.ToString());
        try
        {
            db.Open();
            DbBootstrapper.EnsureInitialized(db);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }
}
