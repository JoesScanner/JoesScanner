using System.Globalization;

namespace JoesScanner.Services;

internal sealed record PlaybackLagRecoveryPreference(int Minutes = 5, int CustomMinutes = 5)
{
    internal const int Immediately = 0;
    internal const int Never = -1;
    internal const int Custom = -2;
    internal const int MaximumPreset = 60;
    internal const int MaximumCustom = 1440;

    internal bool IsValid => Minutes is >= Custom and <= MaximumPreset && AllowsCustomMinutes(CustomMinutes);
    internal static bool AllowsCustomMinutes(int minutes) => minutes is >= 1 and <= MaximumCustom;

    internal bool Exceeds(TimeSpan lag) => Minutes != Never &&
        lag > TimeSpan.FromMinutes(Minutes == Custom ? CustomMinutes : Minutes);
}

// A lease belongs to the automatic audio context that existed when the outage
// was first detected. Serialize retirement with the final, synchronous commit.
internal sealed class PlaybackLagGeneration
{
    private Lease _current = new();

    internal Lease Capture() => Volatile.Read(ref _current);

    internal void Retire() => Interlocked.Exchange(ref _current, new()).Retire();

    internal sealed class Lease
    {
        private readonly CancellationTokenSource _retirement = new();
        private bool _retired;
        internal CancellationToken Token => _retirement.Token;

        internal bool TryCommit(Action commit)
        {
            lock (_retirement)
            {
                if (_retired)
                    return false;
                commit();
                return true;
            }
        }

        internal void Retire()
        {
            lock (_retirement)
                _retired = true;
            _retirement.Cancel();
        }
    }
}

internal sealed class PlaybackLagEvidence
{
    // Recorder call-start time is not ingestion time. Require near-now completed
    // calls, an exact bounded response, and two matching READY journal entries.
    private static readonly TimeSpan EdgeRecency = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumWindow = TimeSpan.FromHours(48);

    internal readonly record struct Stamp(string Id, DateTimeOffset Start, DateTimeOffset End)
    {
        internal static Stamp? Read(string? id, string? utc, double? seconds)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(utc) ||
                seconds is not (> 0 and <= 172800))
                return null;

            DateTimeOffset time;
            utc = utc.Trim();
            if (double.TryParse(utc, NumberStyles.Float, CultureInfo.InvariantCulture, out var unix) &&
                unix >= 0 && unix <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                time = DateTimeOffset.FromUnixTimeSeconds((long)unix);
            else if ((utc.EndsWith('Z') || utc.Length > 6 && (utc[^6] is '+' or '-') && utc[^3] == ':') &&
                DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var zoned))
                time = zoned.ToUniversalTime();
            else
                return null;

            var length = TimeSpan.FromSeconds(seconds.Value);
            return time <= DateTimeOffset.MaxValue - length ? new(id.Trim(), time, time + length) : null;
        }
    }

    private readonly Dictionary<string, Stamp> _rows;
    private readonly TimeProvider _clock;
    private readonly long _observed;
    private readonly DateTimeOffset _edge;

    private PlaybackLagEvidence(Dictionary<string, Stamp> rows, DateTimeOffset edge, TimeProvider clock, long observed)
    {
        _rows = rows;
        _edge = edge;
        _clock = clock;
        _observed = observed;
    }

    internal static PlaybackLagEvidence? VerifyRecorderSnapshot(IEnumerable<Stamp?> records, int? count,
        DateTimeOffset since, DateTimeOffset through, TimeProvider clock, long started)
    {
        var now = clock.GetUtcNow();
        var elapsed = clock.GetElapsedTime(started);
        if (count is not (>= 4 and <= 200) || through < since || through - since > MaximumWindow ||
            (now - through - elapsed).Duration() > ClockTolerance || elapsed > EdgeRecency)
            return null;

        var rows = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (record is not { } row || row.Start < since || row.Start > through ||
                row.End > now + ClockTolerance || !rows.TryAdd(row.Id, row))
                return null;
        }
        if (rows.Count != count)
            return null;

        // Use the older of two distinct, recent starts as a lower bound for
        // the recorder's Live edge. Compare times in the same recorder clock.
        var edge = rows.Values.Where(row => row.End >= now - EdgeRecency && row.Start >= now - EdgeRecency)
            .Select(row => row.Start).Distinct().OrderDescending().Skip(1).Take(1).ToArray();
        return edge.Length == 1 ? new(rows, edge[0], clock, started) : null;
    }

    internal TimeSpan? Measure(IEnumerable<(string Id, long? RecordedUtc)> ready)
    {
        if (_clock.GetElapsedTime(_observed) > EvidenceLifetime)
            return null;

        // The scheduler uses arrival order, so a recovery page can append
        // older recordings after recent ones. Require two distinct starts
        // whose age is corroborated, using the less delayed of the pair.
        var corroborated = ready.Select(row => _rows.TryGetValue(row.Id, out var stamp) &&
                stamp.Start.UtcTicks == row.RecordedUtc ? stamp : (Stamp?)null)
            .OfType<Stamp>()
            .Where(row => row.End < _edge)
            .GroupBy(row => row.Start)
            .Select(group => group.Max(row => row.End))
            .Order()
            .Take(2).ToArray();
        return corroborated.Length == 2 ? _edge - corroborated[1] : null;
    }
}
