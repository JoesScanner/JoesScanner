#if WINDOWS || WINDOWS_LIVE_READING_TESTS
namespace JoesScanner.Views;

// One instance belongs to one native ScrollView. WinUI may reuse its IDs on a
// replacement view and can deliver a superseded request's terminal much later.
internal sealed class WindowsLiveScrollIntent
{
    internal bool OwnsApp(int id) => !_retired && !_accessibility && !IsPhysical && _request == id;

    internal static double WheelDistance(int wheelDelta, uint lines, double viewportHeight) =>
        -(double)wheelDelta / 120 * (lines == uint.MaxValue ? viewportHeight : 16d * lines);

    private int? _request;
    private bool _accessibility;
    private bool _retired;

    internal long Generation { get; private set; }
    internal bool IsPhysical { get; private set; }
    internal int PendingOperations => _request.HasValue ? 1 : 0;

    internal bool IsCurrent(long generation) => !_retired && generation == Generation;

    internal long Cancel()
    {
        IsPhysical = false;
        _request = null;
        _accessibility = false;
        return ++Generation;
    }

    internal long BeginPhysical()
    {
        Cancel();
        IsPhysical = !_retired;
        return Generation;
    }

    internal void EndPhysical() => IsPhysical = false;

    internal long BeginAccessibility()
    {
        Cancel();
        _accessibility = true;
        return Generation;
    }

    internal void SubmittedAccessibility(long generation, int id)
    {
        if (IsCurrent(generation) && _accessibility && id >= 0)
            _request = id;
    }

    internal void SubmittedApplication(long generation, int id)
    {
        if (IsCurrent(generation) && !_accessibility && !IsPhysical && id >= 0)
            _request = id;
    }

    internal void RetireApplication(long generation, int id)
    {
        if (IsCurrent(generation) && !_accessibility && _request == id)
            _request = null;
    }

    internal bool AcceptAccessibilityCompletion(int id)
    {
        if (_retired || _request != id)
            return false;

        _request = null;
        return _accessibility;
    }

    internal void Retire()
    {
        Cancel();
        _retired = true;
    }
}

// An accepted ID can outlive a detached viewport on the same presenter. Record its
// lightweight intent owner until WinUI dispatches it, even if the reader is retired.
internal sealed class WindowsLiveAnimationQueue
{
    internal const int Capacity = 128;
    private readonly Dictionary<int, WindowsLiveScrollIntent> _submitted = [];
    internal bool HasPending => _submitted.Count != 0;
    internal bool HasRoom => _submitted.Count < Capacity;

    internal void Add(int id, WindowsLiveScrollIntent owner)
    {
        if (id >= 0 && HasRoom)
            _submitted[id] = owner;
    }

    internal bool TryTake(int id, out bool current)
    {
        current = false;
        if (!_submitted.Remove(id, out var owner))
            return false;
        current = owner.OwnsApp(id);
        return true;
    }

    internal void Completed(int id) => _submitted.Remove(id);
}
#endif
