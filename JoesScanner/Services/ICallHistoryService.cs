using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
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
    internal const string LookupCachePrefix = "history-recorder:v2:";
    internal static bool IsLookupCacheKey(string? key) => key != null && key.StartsWith(LookupCachePrefix, StringComparison.Ordinal) &&
        key.Length == LookupCachePrefix.Length + 64 && key.AsSpan(LookupCachePrefix.Length).IndexOfAnyExcept("0123456789ABCDEF") < 0;
    internal RecorderRequestScope RequestScope => new(new Uri(Endpoint), Id);
    internal string LookupCacheKey(string? account, bool hosted) =>
        LookupCachePrefix + Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { Id.ToString("N"), Endpoint, hosted ? "hosted" : "custom", account })));
}

public sealed record HistoryLookupItem
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    internal Guid? RecorderId { get; init; }
}

public sealed class HistoryLookupData
{
    internal HistoryRecorderAccess? Access { get; init; }
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

public sealed record HistoryRecorderAccess
{
    internal HistoryRecorderAccess(HistoryRecorder recorder, bool isProtected, string? account, long? version,
        RecorderOperation? operation = null, RecorderOperation? hostedIdentity = null)
    {
        (Recorder, Protected, Account, AccountVersion, Operation, HostedIdentity) =
            (recorder, isProtected, account, version, operation, hostedIdentity);
    }

    internal HistoryRecorder Recorder { get; }
    internal bool Protected { get; }
    internal string? Account { get; }
    internal long? AccountVersion { get; }
    internal RecorderOperation? Operation { get; }
    internal RecorderOperation? HostedIdentity { get; }
    internal bool AllowsSharedCatalog => Protected || Account == null;
    internal string MetadataKey => new RecordingMetadataScope(Recorder.Id, Recorder.Endpoint, Protected, Account).StorageKey;
    internal bool Retired => Operation?.Token.IsCancellationRequested == true || HostedIdentity?.Token.IsCancellationRequested == true;
    internal CancellationTokenSource Link(CancellationToken token) =>
        CancellationTokenSource.CreateLinkedTokenSource(token, Operation?.Token ?? default, HostedIdentity?.Token ?? default);

    internal bool TryPublish(Action commit)
    {
        bool applied = false;
        void ApplyRecorder() => applied = Operation!.TryApply(commit);
        if (HostedIdentity == null)
            ApplyRecorder();
        else
            HostedIdentity.TryApply(ApplyRecorder);
        return applied;
    }

    internal bool TryPublish(RecorderOperation cloudAccount, Action commit)
    {
        var published = false;
        cloudAccount.TryApply(() => published = TryPublish(commit));
        return published;
    }

    internal RecorderRequestScope RequestScope => Recorder.RequestScope with { Operation = Operation };
    internal string LookupCacheKey => Recorder.LookupCacheKey(Account, Protected);
}
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
