using System.Globalization;
using JoesScanner.Data;
using JoesScanner.Helpers;
using JoesScanner.Models;

namespace JoesScanner.Services;

public sealed partial class CallHistoryService
{
    internal const int InitialResultCount = 250;
    internal const int AdditionalResultCount = 100;
    private const int MaxRequestCount = 200;

    public async Task<HistorySearchResult> SearchAsync(HistoryRecorder recorder, HistorySearchFilters filters, HistoryDateRange dates,
        CancellationToken cancellationToken = default)
    {
        if (!filters.IsValidFor(recorder.Id))
            throw new ArgumentException("Select a site, receiver, or talkgroup from this recorder before searching.", nameof(filters));

        // Copy the selection before awaiting network I/O. Paging uses the same
        // filter values and exclusive time boundary as the initial request.
        filters = new HistorySearchFilters
        {
            Receiver = filters.Receiver,
            Site = filters.Site,
            Talkgroups = filters.Talkgroups.DistinctBy(item => item.Value, StringComparer.Ordinal).ToArray()
        };
        var started = _clock.GetUtcNow();
        var access = ReadRecorderAccess(recorder);
        var grant = await VerifyRecorderHistoryAsync(access, cancellationToken).ConfigureAwait(false);
        var (from, until) = GetSearchBounds(started, dates, grant.Cutoff);
        var snapshot = new HistorySearchSnapshot(access, filters, from, until, grant.Tier);
        return await ReadResultsAsync(snapshot, HistoryPageCursor.Start, grant, InitialResultCount, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HistorySearchResult> LoadMoreAsync(HistorySearchResult previous, CancellationToken cancellationToken = default)
    {
        var snapshot = previous.Snapshot;
        if (!previous.HasMore)
            return new(snapshot, previous.Cursor, [], false);

        var grant = await VerifyRecorderHistoryAsync(snapshot.Access, cancellationToken).ConfigureAwait(false);
        if (grant.Tier != snapshot.Tier)
            throw new HistoryResultsChangedException();

        return await ReadResultsAsync(snapshot, previous.Cursor, grant, AdditionalResultCount, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HistorySearchResult> ReadResultsAsync(HistorySearchSnapshot snapshot, HistoryPageCursor cursor,
        RecordingPlaybackAccess.HistoryGrant grant, int limit, CancellationToken token)
    {
        if (snapshot.FromUtc >= snapshot.UntilUtc || grant.Cutoff >= snapshot.UntilUtc)
            return new(snapshot, cursor, [], false);

        var calls = new List<CallItem>(limit);
        var records = new List<DbCallRecord>();
        var seen = cursor.Seen.ToBuilder();
        var offset = cursor.Offset;
        var total = cursor.TotalMatches;
        var lastId = cursor.LastId;

        while (calls.Count < limit && (total == null || offset < total))
        {
            // Re-read just the boundary row to detect shifts in the recorder's
            // offset-based archive. The marker never enters the result page.
            var marker = offset == 0 ? 0 : 1;
            var length = Math.Min(MaxRequestCount, limit - calls.Count + marker);
            var page = await FetchCallsPageAsync(snapshot, grant, offset - marker, length, token).ConfigureAwait(false);
            if (total != null && total != page.TotalMatches || page.Rows.Count > length ||
                marker != 0 && (page.Rows.Count == 0 || page.Rows[0].DT_RowId?.Trim() != lastId))
                throw new HistoryResultsChangedException();
            total = page.TotalMatches;
            var consumed = page.Rows.Count - marker;
            if (consumed <= 0)
            {
                if (offset < total)
                    throw new HistoryResultsChangedException();
                break;
            }
            if (offset > total - consumed)
                throw new HistoryResultsChangedException();

            for (var index = marker; index < page.Rows.Count; index++)
            {
                var row = page.Rows[index];
                var id = row.DT_RowId?.Trim();
                if (string.IsNullOrEmpty(id))
                    throw new InvalidDataException("The recorder returned a History row without a recording ID.");

                var recordedAt = CallStreamService.TryParseTimestampUtc(row.StartTime, row.StartTimeUTC);
                if (recordedAt is not { } time || time < snapshot.FromUtc || time >= snapshot.UntilUtc || !grant.Allows(time))
                {
                    // Ignore out-of-window rows even if an intermediary or
                    // older recorder did not apply the requested date range.
                    continue;
                }
                if (!seen.Add((snapshot.Access.Recorder.Id, id)))
                    continue;

                if (ConvertRowToCallItem(row, snapshot.Access, time, offset + (index - marker)) is { } call)
                {
                    calls.Add(call);
                    if (MapToDbRecord(row, snapshot.Access.Recorder.Endpoint) is { } record)
                        records.Add(record);
                }
            }

            offset += consumed;
            lastId = page.Rows[^1].DT_RowId!.Trim();
        }

        ValidateSearch(snapshot.Access, grant, token);
        QueueHistoryPersistence(snapshot.Access.Recorder.Endpoint, records);
        calls.Sort(HistoryResultOrder.Compare);
        return new(snapshot, new(offset, lastId, total, seen.ToImmutable()), calls, offset < total);
    }

    private async Task<RecordingPlaybackAccess.HistoryGrant> VerifyRecorderHistoryAsync(HistoryRecorderAccess access, CancellationToken token)
    {
        ValidateRecorderAccess(access, token);
        var grant = await RecordingPlaybackAccess.VerifyHistoryAsync(_settingsService, _subscriptionService,
            access.Recorder.Endpoint, token, _clock, independent: true).ConfigureAwait(false);
        ValidateSearch(access, grant, token);
        return grant;
    }

    private HistoryRecorderAccess ReadRecorderAccess(HistoryRecorder recorder)
    {
        var hosted = HostedServerRules.RequiresApiFirewallCredentials(_settingsService, recorder.Endpoint);
        return hosted
            ? new(recorder, true, RecordingPlaybackAccess.AccountKey(_settingsService, recorder.Endpoint, independent: true),
                _settingsService.ReadAccountSession().AccountVersion)
            : new(recorder, false, RecordingPlaybackAccess.RecorderAccountKey(_settingsService, recorder.Id, recorder.Endpoint), null);
    }

    private void ValidateRecorderAccess(HistoryRecorderAccess access, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ReadRecorderAccess(access.Recorder) != access)
            throw new RecordingAccessException("The recorder or its sign-in changed. Search again.");
    }

    private void ValidateSearch(HistoryRecorderAccess access, RecordingPlaybackAccess.HistoryGrant grant, CancellationToken token)
    {
        ValidateRecorderAccess(access, token);
        grant.Validate(_settingsService);
        if (access.Protected && access.Account != grant.Account)
            throw RecordingPlaybackAccess.VerificationRequired();
    }

    internal static (DateTimeOffset From, DateTimeOffset Until) GetSearchBounds(DateTimeOffset started, HistoryDateRange dates,
        DateTimeOffset? standardCutoff)
    {
        if (dates.StartTime < TimeSpan.Zero || dates.StartTime >= TimeSpan.FromDays(1) || dates.From > dates.To)
            throw new ArgumentException("Choose a valid date range and start time.");

        var until = DateTimeOffset.FromUnixTimeSeconds(started.ToUnixTimeSeconds());
        var today = DateOnly.FromDateTime(started.LocalDateTime);
        var fromLocal = (standardCutoff.HasValue ? today : dates.From).ToDateTime(TimeOnly.FromTimeSpan(dates.StartTime));
        if (standardCutoff.HasValue && fromLocal > started.LocalDateTime)
            fromLocal = fromLocal.AddDays(-1);
        if (TimeZoneInfo.Local.IsInvalidTime(fromLocal))
            throw new ArgumentException("The selected start time does not exist on that date.");

        var from = new DateTimeOffset(fromLocal).ToUniversalTime();
        if (standardCutoff is { } cutoff && from < cutoff)
            from = cutoff;

        if (!standardCutoff.HasValue && dates.To < today)
        {
            var end = new DateTimeOffset(dates.To.AddDays(1).ToDateTime(TimeOnly.MinValue)).ToUniversalTime();
            if (end < until)
                until = end;
        }

        return (RecordingPlaybackAccess.RoundStartToSecond(from), until);
    }

    private static string FormatSearchDate(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
