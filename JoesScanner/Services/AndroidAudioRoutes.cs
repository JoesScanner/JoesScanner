namespace JoesScanner.Services;

// Null means the platform hasn't identified the media route. An empty snapshot
// means it has identified a route with no private outputs.
internal sealed class AndroidAudioRoutes
{
    private int[]? _privateRoute;
    private int[] _idleInventory = [];
    private readonly HashSet<int> _reportedLosses = [];
    private bool _unknownLossReported;

    public bool ObserveRoute(int[] privateRoute, IReadOnlyCollection<int> connected)
    {
        var previous = _privateRoute;
        _privateRoute = privateRoute;
        if (privateRoute.Length > 0)
            _unknownLossReported = false;
        return previous?.Length > 0 &&
            Report(previous.Where(id => !connected.Contains(id)));
    }

    // The previous player route stops being evidence as soon as the clip is
    // silent. A last-moment disconnect is still attributable to that clip.
    public bool EnterIdle(int[]? expectedRoute, IReadOnlyCollection<int> connected)
    {
        var lost = _privateRoute != null && Report(_privateRoute.Where(id => !connected.Contains(id)));
        _privateRoute = expectedRoute;
        _idleInventory = connected.ToArray();
        if (expectedRoute?.Length > 0)
            _unknownLossReported = false;
        return lost;
    }

    public bool IdleDevicesRemoved(int[] removed, IReadOnlyCollection<int> connected, int[]? expectedRoute)
    {
        _idleInventory = connected.ToArray();
        // A freshly confirmed private destination is safe between recordings.
        // A speaker report after removal could be Android's fallback, so use
        // the preceding idle observation (or legacy evidence) in that case.
        if (expectedRoute is { Length: > 0 } &&
            expectedRoute.All(id => !removed.Contains(id) && connected.Contains(id)))
        {
            _privateRoute = expectedRoute;
            _unknownLossReported = false;
            return false;
        }
        var lost = DevicesRemoved(removed, connected);
        _privateRoute = expectedRoute;
        return lost;
    }

    public bool CheckIdleRoute(int[]? expectedRoute, IReadOnlyCollection<int> connected) =>
        IdleDevicesRemoved(_idleInventory.Where(id => !connected.Contains(id)).ToArray(), connected, expectedRoute);

    public bool DevicesRemoved(int[] removed, IReadOnlyCollection<int> connected)
    {
        if (_privateRoute != null)
            return Report(removed.Where(id => _privateRoute.Contains(id)));
        if (removed.Length == 0 || connected.Count > 0)
            return false;
        var firstSignal = !_unknownLossReported;
        _unknownLossReported = true;
        return Report(removed) && firstSignal;
    }

    public void DevicesAdded(IEnumerable<int> added)
    {
        foreach (var id in added)
        {
            _reportedLosses.Remove(id);
            if (_privateRoute == null)
                _unknownLossReported = false;
        }
    }

    public bool BecomingNoisy()
    {
        if (_privateRoute != null)
            return Report(_privateRoute);
        var firstSignal = !_unknownLossReported;
        _unknownLossReported = true;
        return firstSignal;
    }

    public void Reset()
    {
        _privateRoute = null;
        _idleInventory = [];
        _reportedLosses.Clear();
        _unknownLossReported = false;
    }

    private bool Report(IEnumerable<int> ids)
    {
        var fresh = false;
        foreach (var id in ids)
            fresh |= _reportedLosses.Add(id);
        return fresh;
    }
}
