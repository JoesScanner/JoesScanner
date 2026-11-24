using System.Text.Json;
using JoesScanner.Models;

namespace JoesScanner.Services;

internal sealed record LivePlaybackPreferences(bool GroupCalls = true, bool PrioritizeFavorites = true,
    int ConsecutiveLimit = 3, int WarningThreshold = 80, int JumpThreshold = 100)
{
    internal const int MinimumConsecutive = 1;
    internal const int MaximumConsecutive = 10;
    private const string SettingsKey = "live_playback_preferences";

    internal static LivePlaybackPreferences Default { get; } = new();

    internal static LivePlaybackPreferences Read()
    {
        try { return Parse(AppStateStore.GetString(SettingsKey)); }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Live playback preferences could not be read ({ex.GetType().Name}).");
            return Default;
        }
    }

    internal static LivePlaybackPreferences Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Default;
        try
        {
            var options = JsonSerializer.Deserialize<LivePlaybackPreferences>(raw);
            return options == null ? Default : options with
            {
                ConsecutiveLimit = options.ConsecutiveLimit is >= MinimumConsecutive and <= MaximumConsecutive
                    ? options.ConsecutiveLimit : Default.ConsecutiveLimit,
                WarningThreshold = options.WarningThreshold > 0 ? options.WarningThreshold : Default.WarningThreshold,
                JumpThreshold = options.JumpThreshold > 0 ? options.JumpThreshold : Default.JumpThreshold
            };
        }
        catch (JsonException) { return Default; }
    }

    internal void Save() => AppStateStore.SetString(SettingsKey, JsonSerializer.Serialize(this));
}

internal readonly record struct TalkgroupKey(Guid ServerId, string System, string Site, string Talkgroup)
{
    internal static TalkgroupKey? From(Guid? serverId, CallCardMetadata? metadata)
    {
        var talkgroup = Identity(metadata?.TalkgroupId, metadata?.Talkgroup);
        if (serverId is not { } id || talkgroup.Length == 0)
            return null;

        var system = Identity(metadata?.SystemId, metadata?.System);
        // A verified radio-system ID scopes trunked talkgroups across that system's sites.
        // With incomplete metadata, keep sites and receivers separate to avoid false matches.
        var site = Normalize(metadata?.SystemId).Length != 0 ? string.Empty :
            Identity(metadata?.SiteId, metadata?.Site ?? metadata?.Receiver ?? metadata?.Radio);
        return new(id, system, site, talkgroup);
    }

    private static string Identity(string? id, string? label) =>
        Normalize(id) is { Length: > 0 } value ? "id:" + value :
        Normalize(label) is { Length: > 0 } name ? "label:" + name : string.Empty;

    private static string Normalize(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? string.Empty : normalized.ToUpperInvariant();
    }
}

internal interface IFavoriteTalkgroupMembership
{
    bool Contains(TalkgroupKey key);
}

// This is an effective snapshot for the current listener. A future user-scoped
// cache can supply it without changing the scheduler or transport.
internal sealed class FavoriteTalkgroupSnapshot(IEnumerable<TalkgroupKey> effective) : IFavoriteTalkgroupMembership
{
    private readonly HashSet<TalkgroupKey> _keys = effective.ToHashSet();

    public bool Contains(TalkgroupKey key) => _keys.Contains(key);

    internal static FavoriteTalkgroupSnapshot Load()
    {
#if DEBUG
        try
        {
            var raw = AppStateStore.GetString("debug_live_favorite_talkgroups");
            if (!string.IsNullOrWhiteSpace(raw))
                return new(JsonSerializer.Deserialize<TalkgroupKey[]>(raw) ?? []);
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Debug Live Favorite fixture could not be read ({ex.GetType().Name}).");
        }
#endif
        return new([]);
    }
}

internal sealed class LivePlaybackScheduler(IFavoriteTalkgroupMembership favorites)
{
    internal const int Lookahead = 20;
    internal const int FavoriteStreakLimit = 3;

    internal readonly record struct Candidate(long Arrival, TalkgroupKey? Group, bool RecoveredAudio = false, bool Resuming = false);
    internal readonly record struct Choice(Candidate Candidate, bool Favorite);

    internal sealed record Scheduled(DurableLiveQueue.Selection Row, Choice Choice);

    internal async Task<Scheduled?> SelectAsync(DurableLiveQueue journal, IReadOnlyList<DurableLiveQueue.Run> scopes,
        Func<DurableLiveQueue.Run, long> recoveryFloor, LivePlaybackPreferences options, CancellationToken token,
        long? after = null, IReadOnlyList<long>? visibleReplays = null)
    {
        var window = await journal.ReadReadyWindowAsync(scopes, recoveryFloor, after.HasValue || !options.GroupCalls ? 1 : Lookahead,
            token, after ?? 0, visibleReplays);
        var choice = Choose(window.Select(row => new Candidate(row.Row.ArrivalSequence,
            TalkgroupKey.From(row.Scope.ServerId, row.Row.Snapshot?.Metadata), row.Row.AudioRecovered, row.Row.Resuming)),
            options, manualReplay: after.HasValue);
        return choice is { } selected
            ? new(window.First(row => row.Row.ArrivalSequence == selected.Candidate.Arrival), selected)
            : null;
    }

    private TalkgroupKey? _lastGroup;
    private int _consecutiveGroup;
    private int _consecutiveFavorites;
    private long _furthestCompleted;

    // The journal supplies already eligible candidates in local arrival order.
    internal Choice? Choose(IEnumerable<Candidate> candidates, LivePlaybackPreferences options, bool manualReplay = false)
    {
        var window = candidates.Take(Lookahead).ToArray();
        if (window.Length == 0)
            return null;
        if (!options.GroupCalls || manualReplay)
            return new(window[0], false);

        var waiting = window.Select(candidate => new Choice(candidate, options.PrioritizeFavorites &&
            candidate.Group is { } group && favorites.Contains(group))).ToArray();
        foreach (var choice in waiting)
            if (choice.Candidate.Resuming)
                return choice;

        if (_lastGroup.HasValue && _consecutiveGroup >= options.ConsecutiveLimit &&
            waiting.Any(choice => choice.Candidate.Group != _lastGroup))
            waiting = waiting.Where(choice => choice.Candidate.Group != _lastGroup).ToArray();

        if (options.PrioritizeFavorites && _consecutiveFavorites >= FavoriteStreakLimit)
            foreach (var choice in waiting)
                if (!choice.Favorite)
                    return choice;

        if (waiting.Any(choice => choice.Favorite))
            waiting = waiting.Where(choice => choice.Favorite).ToArray();

        foreach (var choice in waiting)
            if (choice.Candidate.RecoveredAudio && choice.Candidate.Arrival < _furthestCompleted)
                return choice;

        foreach (var choice in waiting)
            if (_lastGroup.HasValue && choice.Candidate.Group == _lastGroup)
                return choice;

        return waiting[0];
    }

    internal void Complete(Choice choice)
    {
        var group = choice.Candidate.Group;
        _consecutiveGroup = group.HasValue && group == _lastGroup ? Math.Min(LivePlaybackPreferences.MaximumConsecutive, _consecutiveGroup + 1) : 1;
        _lastGroup = group;
        _consecutiveFavorites = choice.Favorite ? Math.Min(FavoriteStreakLimit, _consecutiveFavorites + 1) : 0;
        _furthestCompleted = Math.Max(_furthestCompleted, choice.Candidate.Arrival);
    }

    internal void Reset()
    {
        _lastGroup = null;
        _consecutiveGroup = 0;
        _consecutiveFavorites = 0;
        _furthestCompleted = 0;
    }
}

internal sealed class LiveBacklogGuard
{
    internal bool WarningActive { get; private set; }
    internal bool JumpPending { get; private set; }
    private bool _jumpSuppressed;

    // Returns true only on the leading edge of each warning episode.
    internal bool Observe(int readyWaiting, LivePlaybackPreferences options, bool manualReplay)
    {
        var warn = readyWaiting >= options.WarningThreshold;
        var newWarning = warn && !WarningActive;
        WarningActive = warn;

        if (readyWaiting < options.JumpThreshold)
            _jumpSuppressed = false;
        if (manualReplay)
            CancelPendingJump();
        else if (!_jumpSuppressed && readyWaiting >= options.JumpThreshold)
            JumpPending = true;
        return newWarning;
    }

    internal void CancelPendingJump()
    {
        _jumpSuppressed |= JumpPending;
        JumpPending = false;
    }

    internal void ResetJump() => JumpPending = _jumpSuppressed = false;

    internal void Reset()
    {
        WarningActive = false;
        ResetJump();
    }
}
