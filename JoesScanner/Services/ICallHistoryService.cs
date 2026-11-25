using System.Collections.Immutable;
using JoesScanner.Models;

namespace JoesScanner.Services;

public sealed record HistoryRecorder
{
    internal HistoryRecorder(Guid id, string endpoint)
    {
        Id = id;
        Endpoint = ScannerConnectionStore.Normalize(endpoint) ?? throw new ArgumentException("Invalid recorder address.", nameof(endpoint));
    }

    public Guid Id { get; }
    public string Endpoint { get; }
    internal const string LookupCachePrefix = "history-recorder:";
    internal RecorderRequestScope RequestScope => new(new Uri(Endpoint), Id);
    internal string LookupCacheKey(string? account) => LookupCachePrefix + Id.ToString("N") + "|" + Endpoint + "|" + account;
}

public sealed record HistoryLookupItem
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    internal Guid? RecorderId { get; init; }
}

public sealed class HistoryLookupData
{
    public IReadOnlyList<HistoryLookupItem> Systems { get; init; } = [];
    public IReadOnlyList<HistoryLookupItem> Receivers { get; init; } = [];
    public IReadOnlyList<HistoryLookupItem> Sites { get; init; } = [];
    public IReadOnlyList<HistoryLookupItem> Talkgroups { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<HistoryLookupItem>> TalkgroupGroups { get; init; }
        = new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);
}

public sealed class HistorySearchFilters
{
    public HistoryLookupItem? Receiver { get; init; }
    public HistoryLookupItem? Site { get; init; }
    public IReadOnlyList<HistoryLookupItem> Talkgroups { get; init; } = [];

    internal bool IsValidFor(Guid recorderId)
    {
        var selected = Talkgroups.Append(Receiver).Append(Site).Where(item => !string.IsNullOrWhiteSpace(item?.Value)).ToArray();
        return selected.Length != 0 && selected.All(item => item!.RecorderId == recorderId) &&
            Talkgroups.All(item => !string.IsNullOrWhiteSpace(item.Value) && !item.Value.Contains(','));
    }
}

public sealed record HistoryDateRange(DateOnly From, DateOnly To, TimeSpan StartTime);

internal sealed record HistoryRecorderAccess(HistoryRecorder Recorder, bool Protected, string? Account, long? AccountVersion);
internal sealed record HistorySearchSnapshot(HistoryRecorderAccess Access, HistorySearchFilters Filters,
    DateTimeOffset FromUtc, DateTimeOffset UntilUtc, int? Tier);
internal sealed record HistoryPageCursor(int Offset, string? LastId, int? TotalMatches,
    ImmutableHashSet<(Guid RecorderId, string RecordingId)> Seen)
{
    internal static HistoryPageCursor Start { get; } = new(0, null, null, []);
}

public sealed class HistorySearchResult
{
    internal HistorySearchResult(HistorySearchSnapshot snapshot, HistoryPageCursor cursor, IReadOnlyList<CallItem> calls, bool hasMore)
    {
        Snapshot = snapshot;
        Cursor = cursor;
        Calls = calls;
        HasMore = hasMore;
    }

    internal HistorySearchSnapshot Snapshot { get; }
    internal HistoryPageCursor Cursor { get; }
    public IReadOnlyList<CallItem> Calls { get; }
    public int TotalMatches => Cursor.TotalMatches ?? 0;
    public bool HasMore { get; }
    public bool LimitedToLast24Hours => Snapshot.Tier == 1;
}

internal sealed class HistoryResultsChangedException()
    : InvalidOperationException("The recorder changed these results. Search again to browse the current archive.");

public interface ICallHistoryService
{
    Task<HistoryLookupData> GetLookupDataAsync(HistorySearchFilters? currentFilters, CancellationToken cancellationToken = default);
    Task<HistoryLookupData> GetRecorderLookupsAsync(HistoryRecorder recorder, bool refresh, CancellationToken cancellationToken = default);
    Task<HistorySearchResult> SearchAsync(HistoryRecorder recorder, HistorySearchFilters filters, HistoryDateRange dates,
        CancellationToken cancellationToken = default);
    Task<HistorySearchResult> LoadMoreAsync(HistorySearchResult previous, CancellationToken cancellationToken = default);
}
