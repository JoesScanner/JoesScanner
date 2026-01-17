using JoesScanner.Models;
using System;
using System.Collections.Generic;

namespace JoesScanner.Services
{
    public sealed class HistoryLookupItem
    {
        public string Label { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    public sealed class HistoryLookupData
    {
        public IReadOnlyList<HistoryLookupItem> Systems { get; init; } = Array.Empty<HistoryLookupItem>();
        public IReadOnlyList<HistoryLookupItem> Receivers { get; init; } = Array.Empty<HistoryLookupItem>();
        public IReadOnlyList<HistoryLookupItem> Sites { get; init; } = Array.Empty<HistoryLookupItem>();
        public IReadOnlyList<HistoryLookupItem> Talkgroups { get; init; } = Array.Empty<HistoryLookupItem>();

        // Some Trunking Recorder endpoints (talkgroupsjson) return grouped results.
        // Key: group label (normalized). Value: child talkgroups within that group.
        // The History tab can optionally narrow Talkgroups based on the selected Site.
        public IReadOnlyDictionary<string, IReadOnlyList<HistoryLookupItem>> TalkgroupGroups { get; init; }
            = new Dictionary<string, IReadOnlyList<HistoryLookupItem>>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class HistorySearchFilters
    {
        public HistoryLookupItem? System { get; init; }
        public HistoryLookupItem? Receiver { get; init; }
        public HistoryLookupItem? Site { get; init; }
        public HistoryLookupItem? Talkgroup { get; init; }
    }

    public sealed class HistorySearchResult
    {
        public IReadOnlyList<CallItem> Calls { get; init; } = Array.Empty<CallItem>();

        // Global start index within the full filtered result set.
        // Index 0 is the newest call.
        public int StartIndex { get; init; }

        // Global index within the full filtered result set for the anchor call.
        // Index 0 is the newest call.
        public int AnchorGlobalIndex { get; init; }

        // Index into Calls that represents the anchor item (closest to the requested time).
        public int AnchorIndex { get; init; }

        public int TotalMatches { get; init; }
    }

    public sealed class HistoryCallsPage
    {
        public IReadOnlyList<CallItem> Calls { get; init; } = Array.Empty<CallItem>();
        public int TotalMatches { get; init; }
    }

    public interface ICallHistoryService
    {
        // Loads available lookup values for History filtering.
        // When system, receiver, and or site are provided, the server may return a narrowed list.
        Task<HistoryLookupData> GetLookupDataAsync(HistorySearchFilters? currentFilters, CancellationToken cancellationToken = default);

        // Finds the call closest to targetLocalTime, then returns a fixed window of calls around it.
        // Calls are returned in the same order as the main queue (newest first).
        Task<HistorySearchResult> SearchAroundAsync(
            DateTime targetLocalTime,
            HistorySearchFilters filters,
            int windowSize,
            CancellationToken cancellationToken = default);

        // Fetches a slice of calls from the full filtered result set.
        // Calls are returned newest first.
        Task<HistoryCallsPage> GetCallsPageAsync(
            int start,
            int length,
            HistorySearchFilters filters,
            CancellationToken cancellationToken = default);
    }
}
