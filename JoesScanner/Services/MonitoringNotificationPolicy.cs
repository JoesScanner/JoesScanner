namespace JoesScanner.Services;

internal enum MonitoringAlertKind { Transmission, Unavailable, SignInRequired }

internal readonly record struct MonitoringAlert(string Id, MonitoringAlertKind Kind, bool Audible, int Count = 1, bool Update = false)
{
    internal string Title => Kind switch
    {
        MonitoringAlertKind.Unavailable => "Recorder unavailable",
        MonitoringAlertKind.SignInRequired => "Recorder setup required",
        _ => "Radio transmission"
    };

    internal string Body => Kind switch
    {
        MonitoringAlertKind.Unavailable => "Check Server Connections in Joe’s Scanner.",
        MonitoringAlertKind.SignInRequired => "Review the recorder’s sign-in settings in Joe’s Scanner.",
        _ => Count == 1 ? "A new transmission was received." : $"{Count} transmissions received in one talkgroup."
    };
}

internal readonly record struct NotificationRecording(Guid ServerId, TalkgroupKey? Talkgroup, bool Favorite,
    DateTimeOffset? RecordedAt, double DurationSeconds, bool FirstAdmission, bool IsCurrentTransmission);

internal sealed class MonitoringNotificationPolicy(TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private readonly Dictionary<TalkgroupKey, Group> _groups = new();
    private readonly Dictionary<Guid, Failure> _failures = new();
    private readonly string _instance = Guid.NewGuid().ToString("N")[..8];
    private int _sequence;

    private sealed record Group(string Id, long Start)
    {
        internal int Count = 1;
    }
    private sealed record Failure(long Generation, string Id);

    internal MonitoringAlert? Transmission(NotificationRecording recording, MonitoringDeviceOptions options,
        bool monitoring, bool viewingLive, bool sourceAllowed)
    {
        if (!options.TransmissionNotifications || !monitoring || viewingLive || !sourceAllowed ||
            recording.ServerId == Guid.Empty || !recording.FirstAdmission || !recording.IsCurrentTransmission ||
            !IsRecent(recording))
            return null;

        var key = recording.Talkgroup is { } identified && identified.ServerId == recording.ServerId
            ? identified : (TalkgroupKey?)null;
        var matches = options.Scope switch
        {
            TransmissionScope.AllCalls => true,
            TransmissionScope.SelectedTalkgroups => options.Talkgroups.Any(item => item.Key == key),
            TransmissionScope.Favorites => recording.Favorite,
            _ => false
        };
        if (!matches)
            return null;

        if (!options.GroupByTalkgroup || key is not { } talkgroup)
            return new(NextId(), MonitoringAlertKind.Transmission, options.Audible);

        var now = clock.GetTimestamp();
        foreach (var expired in _groups.Where(entry => clock.GetElapsedTime(entry.Value.Start, now) >= Window).ToArray())
            _groups.Remove(expired.Key);

        if (_groups.TryGetValue(talkgroup, out var group))
            return new(group.Id, MonitoringAlertKind.Transmission, options.Audible, ++group.Count, Update: true);
        if (_groups.Count >= 512)
            return null;
        group = new Group(NextId(), now);
        _groups.Add(talkgroup, group);
        return new(group.Id, MonitoringAlertKind.Transmission, options.Audible);
    }

    internal MonitoringAlert? FailureStatus(Guid recorder, long generation, ServerConnectionStatus status,
        MonitoringDeviceOptions options, out string? superseded)
    {
        superseded = null;
        if (!options.ConnectionNotifications ||
            status is not (ServerConnectionStatus.SetupRequired or ServerConnectionStatus.Unavailable))
            return null;

        if (_failures.TryGetValue(recorder, out var failure))
        {
            if (failure.Generation >= generation)
                return null;
            superseded = failure.Id;
        }

        var id = NextId();
        _failures[recorder] = new(generation, id);
        return new(id, status == ServerConnectionStatus.SetupRequired
            ? MonitoringAlertKind.SignInRequired : MonitoringAlertKind.Unavailable, options.Audible);
    }

    internal string? EndFailure(Guid recorder, long generation)
    {
        if (!_failures.TryGetValue(recorder, out var failure) || failure.Generation != generation)
            return null;
        _failures.Remove(recorder);
        return failure.Id;
    }

    internal string[] EndFailures()
    {
        var ids = _failures.Values.Select(failure => failure.Id).ToArray();
        _failures.Clear();
        return ids;
    }

    internal void ClearTransmissionGroups() => _groups.Clear();

    private bool IsRecent(NotificationRecording recording)
    {
        // Some independent sockets replay historical rows as ordinary events.
        // Use the end time so a long transmission can still notify when it finishes.
        if (recording.RecordedAt is not { } start)
            return false;
        var duration = double.IsFinite(recording.DurationSeconds) ? Math.Clamp(recording.DurationSeconds, 0, 86400) : 0;
        return (clock.GetUtcNow() - start).TotalSeconds - duration <= Window.TotalSeconds;
    }

    private string NextId() => $"{_instance}{unchecked(++_sequence):x8}";
}
