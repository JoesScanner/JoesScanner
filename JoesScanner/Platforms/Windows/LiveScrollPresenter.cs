using JoesScanner.Views;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace JoesScanner.WinUI;

public sealed partial class LiveScrollPresenter : ScrollPresenter
{
    // Keep request identities on the actual presenter when the MAUI viewport detaches.
    // A pending user-placement callback is revoked when its visible viewport retires.
    internal LiveAppAnimations AppAnimations { get; } = new();

    public LiveScrollPresenter()
    {
        ScrollAnimationStarting += AppAnimations.Starting;
        ScrollCompleted += AppAnimations.Completed;
    }

    internal sealed class LiveAppAnimations
    {
        internal WindowsLiveAnimationQueue Queue { get; } = new();
        private (int Id, CompositionPropertySet State)? _dispatched;
        internal bool IsDispatched => _dispatched.HasValue;
        // Preserve the exact terminal of an interrupted user placement independently
        // of the replaceable viewport's general reading observer.
        private (int Id, Action<int> Callback)? _userTerminal;
        internal void AwaitUserTerminal(int id, Action<int> callback) => _userTerminal = (id, callback);
        internal void ForgetUserTerminal(int id)
        {
            if (_userTerminal?.Id == id)
                _userTerminal = null;
        }

        internal void Invalidate() => _dispatched?.State.InsertScalar("Active", 0);

        internal void Starting(ScrollPresenter sender, ScrollingScrollAnimationStartingEventArgs args)
        {
            if (!Queue.TryTake(args.CorrelationId, out var current))
                return;
            // The tracker has not yet accepted its animation. Check the compositor
            // property too, in case newer input arrives on this same dispatch turn.
            var compositor = ElementCompositionPreview.GetElementVisual(sender).Compositor;
            var animation = compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(1); // Shortest supported immediate transition.
            if (current)
            {
                Invalidate();
                var state = compositor.CreatePropertySet();
                state.InsertScalar("Active", 1);
                animation.SetReferenceParameter("owner", state);
                animation.SetVector3Parameter("destination", new System.Numerics.Vector3(args.EndPosition, 0));
                animation.InsertExpressionKeyFrame(1, "owner.Active > 0 ? destination : this.StartingValue");
                _dispatched = (args.CorrelationId, state);
            }
            else
                animation.InsertExpressionKeyFrame(1, "this.StartingValue");
            args.Animation = animation;
        }

        internal void Completed(ScrollPresenter sender, ScrollingScrollCompletedEventArgs args)
        {
            Queue.Completed(args.CorrelationId);
            if (_userTerminal is { } terminal && terminal.Id == args.CorrelationId)
            {
                _userTerminal = null;
                terminal.Callback(args.CorrelationId);
            }
            if (_dispatched?.Id == args.CorrelationId)
                _dispatched = null;
        }
    }

    internal event Action? AccessibilityStarting;
    internal event Action<int>? AccessibilitySubmitted;

    private ScrollMetrics? _publishedMetrics;

    internal void SetReconstructing(bool reconstructing)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(this);
        if (reconstructing && peer?.GetPattern(PatternInterface.Scroll) is IScrollProvider current)
            _publishedMetrics = new(current.HorizontallyScrollable, current.VerticallyScrollable,
                current.HorizontalScrollPercent, current.VerticalScrollPercent, current.HorizontalViewSize, current.VerticalViewSize);
        else
            _publishedMetrics = null;
        peer?.InvalidatePeer();
    }

    private sealed record ScrollMetrics(bool Horizontal, bool Vertical, double X, double Y, double Width, double Height);

    protected override AutomationPeer OnCreateAutomationPeer() => new LivePresenterPeer(this);

    private sealed partial class LivePresenterPeer : ScrollPresenterAutomationPeer
    {
        private LiveScrollProvider? _provider;
        private readonly LiveScrollPresenter _presenter;

        internal LivePresenterPeer(LiveScrollPresenter presenter) : base(presenter)
        {
            _presenter = presenter;
        }

        // A separate provider exposes the managed scroll implementation through the native UIA ABI.
        protected override object? GetPatternCore(PatternInterface pattern)
        {
            if (pattern == PatternInterface.Scroll && _provider is not null)
                return _provider;
            var native = base.GetPatternCore(pattern);
            if (pattern == PatternInterface.Scroll && native is IScrollProvider stock)
                return _provider = new LiveScrollProvider(_presenter, this, stock);
            return native;
        }

        protected override IList<AutomationPeer> GetChildrenCore() =>
            _presenter._publishedMetrics is null ? base.GetChildrenCore() : [];
    }

    private sealed partial class LiveScrollProvider(LiveScrollPresenter presenter, AutomationPeer peer, IScrollProvider native) : IScrollProvider
    {
        private const double NoScroll = -1;
        private const double LineScroll = 16;

        public bool HorizontallyScrollable => presenter._publishedMetrics?.Horizontal ?? native.HorizontallyScrollable;
        public bool VerticallyScrollable => presenter._publishedMetrics?.Vertical ?? native.VerticallyScrollable;
        public double HorizontalScrollPercent => presenter._publishedMetrics?.X ?? native.HorizontalScrollPercent;
        public double VerticalScrollPercent => presenter._publishedMetrics?.Y ?? native.VerticalScrollPercent;
        public double HorizontalViewSize => presenter._publishedMetrics?.Width ?? native.HorizontalViewSize;
        public double VerticalViewSize => presenter._publishedMetrics?.Height ?? native.VerticalViewSize;

        public void SetScrollPercent(double horizontalPercent, double verticalPercent)
        {
            CheckEnabled();
            CheckPercent(horizontalPercent);
            CheckPercent(verticalPercent);
            if (horizontalPercent == NoScroll && verticalPercent == NoScroll)
                return;
            CheckScrollable(horizontalPercent != NoScroll, verticalPercent != NoScroll);
            // Restoring a capped source during cancellation can change the extent. Resolve the
            // requested fraction against the extent after the viewport has retired the old work.
            presenter.AccessibilityStarting?.Invoke();
            var x = horizontalPercent == NoScroll ? presenter.HorizontalOffset :
                presenter.ScrollableWidth * horizontalPercent / 100;
            var y = verticalPercent == NoScroll ? presenter.VerticalOffset :
                presenter.ScrollableHeight * verticalPercent / 100;
            var id = ScrollTo(x, y);
            presenter.AccessibilitySubmitted?.Invoke(id);
        }

        public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
        {
            CheckEnabled();
            if (horizontalAmount == ScrollAmount.NoAmount && verticalAmount == ScrollAmount.NoAmount)
                return;
            CheckScrollable(horizontalAmount != ScrollAmount.NoAmount, verticalAmount != ScrollAmount.NoAmount);
            var dx = Distance(horizontalAmount, presenter.ViewportWidth);
            var dy = Distance(verticalAmount, presenter.ViewportHeight);
            presenter.AccessibilityStarting?.Invoke();
            var id = ScrollTo(presenter.HorizontalOffset + dx, presenter.VerticalOffset + dy);
            presenter.AccessibilitySubmitted?.Invoke(id);
        }

        private int ScrollTo(double x, double y) => presenter.ScrollTo(x, y,
            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));

        private void CheckEnabled()
        {
            if (!presenter.IsLoaded)
                throw new System.Runtime.InteropServices.COMException("The scroll provider is detached.", unchecked((int)0x80040201));
            if (!peer.IsEnabled())
                throw new System.Runtime.InteropServices.COMException("The scroll provider is disabled.", unchecked((int)0x80040200));
        }

        private void CheckScrollable(bool horizontal, bool vertical)
        {
            if ((horizontal && !HorizontallyScrollable) || (vertical && !VerticallyScrollable))
                throw new InvalidOperationException("The requested axis cannot scroll.");
        }

        private static void CheckPercent(double value)
        {
            if (value != NoScroll && (!double.IsFinite(value) || value < 0 || value > 100))
                throw new ArgumentOutOfRangeException(nameof(value));
        }

        private static double Distance(ScrollAmount amount, double page) => amount switch
        {
            ScrollAmount.SmallIncrement => LineScroll,
            ScrollAmount.SmallDecrement => -LineScroll,
            ScrollAmount.LargeIncrement => page,
            ScrollAmount.LargeDecrement => -page,
            ScrollAmount.NoAmount => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(amount))
        };
    }
}
