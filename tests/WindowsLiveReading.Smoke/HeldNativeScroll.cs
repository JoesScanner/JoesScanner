using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace JoesScanner.Views;

internal sealed partial class LiveReadingViewport
{
    internal int SmokeAppRequest(double offset) => ScrollWindowsTo(offset, _windowsIntent!.Generation);
    internal bool SmokeHasEvictedTailWindow => _windowsRestore is { TailWindow: > 0 } && _windowsSource?.IsComplete == false;
    internal bool SmokeOwnsViewer(Microsoft.UI.Xaml.Controls.ScrollView view) =>
        _windowsIntent != null && ReferenceEquals(view, _windowsView) && ReferenceEquals(view.ScrollPresenter, _windowsPresenter);
    internal Action<int> CaptureSmokeTerminalReceiver()
    {
        var presenter = _windowsPresenter!;
        return id => CompleteWindowsScroll(presenter, id);
    }

    internal int SmokePending => _windowsIntent?.PendingOperations ?? 0;
    internal int SmokeAppSubmissions { get; private set; }
    internal int SmokeCenteredPublications { get; private set; }
    internal double SmokeMaximumCenterError { get; private set; }
    partial void WindowsPublishingCenteredCard(double top, double height, double viewportHeight)
    {
        SmokeCenteredPublications++;
        SmokeMaximumCenterError = Math.Max(SmokeMaximumCenterError, Math.Abs(top - (viewportHeight - height) / 2));
    }
    internal event Action<int>? SmokeApplicationSubmitted;
    partial void WindowsAppScrollSubmitted(int id)
    {
        SmokeAppSubmissions++;
        SmokeApplicationSubmitted?.Invoke(id);
    }
    internal bool SmokeAcceptsMoreAnimations => _windowsPresenter!.AppAnimations.Queue.HasRoom;
    internal bool SmokeHasWheelAnchor => _wheelAnchor != null;
    internal void SmokeWheel(int delta) => MoveWindowsWheel(_windowsView!, _windowsIntent!,
        WindowsLiveScrollIntent.WheelDistance(delta, 3, _windowsView!.ViewportHeight), delta % 120 == 0);
    internal void SmokeComplete(int id) => CompleteWindowsScroll(_windowsPresenter!, id);
    internal int SmokePreviewKeys { get; private set; }
    partial void WindowsPreviewKeyReceived() => SmokePreviewKeys++;
    internal bool SmokeReaderReady => _windowsRestore == null && _windowsRepeater?.Opacity > 0 &&
        _windowsView?.ScrollPresenter.IsLoaded == true;
    internal string SmokeStatus => $"offset={_windowsView?.VerticalOffset:F2}, extent={_windowsView?.ScrollableHeight:F2}, " +
        $"scope={_windowsIntent?.Generation}, pending={_windowsIntent?.PendingOperations}, " +
        $"restore={_windowsRestore != null}, stage={_windowsSource?.Count}/{_windowsSource?.IsComplete}, " +
        $"visible={_reader.ResolveAnchor()?.Index}/{_reader.ResolveAnchor()?.Top}, unread={_reader.UnseenCount}, " +
        $"bar={_windowsBar?.GetHashCode()}/{_windowsPresenter?.VerticalScrollController?.GetHashCode()}, physical={_windowsIntent?.IsPhysical}";

    internal Action SuppressSmokeTerminals(Action<int> record)
    {
        var presenter = _windowsPresenter!;
        presenter.ScrollCompleted -= OnWindowsPresenterScrollCompleted;
        presenter.ScrollCompleted += Suppress;
        return () =>
        {
            presenter.ScrollCompleted -= Suppress;
            if (ReferenceEquals(presenter, _windowsPresenter))
                presenter.ScrollCompleted += OnWindowsPresenterScrollCompleted;
        };
        void Suppress(ScrollPresenter sender, ScrollingScrollCompletedEventArgs args) => record(args.CorrelationId);
    }
    // The native operation still executes. Intercept only its terminal at the
    // controller boundary, then pass it back through the production classifier.
    internal sealed class HeldNativeScroll : IDisposable
    {
        private readonly LiveReadingViewport _owner;
        private readonly ScrollPresenter _sender;
        private readonly long _generation;
        internal int Id { get; }
        internal bool Arrived { get; private set; }

        internal HeldNativeScroll(LiveReadingViewport owner, double destination)
        {
            _owner = owner;
            _sender = owner._windowsPresenter!;
            // A reveal or Jump starts a new application scope before submitting its native request.
            owner.CancelPendingScroll();
            _generation = owner._windowsIntent!.Generation;
            _sender.ScrollCompleted -= owner.OnWindowsPresenterScrollCompleted;
            _sender.ScrollCompleted += Intercept;
            Id = owner.ScrollWindowsTo(destination, _generation);
            if (Id < 0)
            {
                Dispose();
                throw new InvalidOperationException("WinUI rejected the application scroll used for the collision test.");
            }
        }

        private void Intercept(ScrollPresenter sender, ScrollingScrollCompletedEventArgs args)
        {
            if (args.CorrelationId == Id)
                Arrived = true;
            else
                _owner.OnWindowsPresenterScrollCompleted(sender, args);
        }

        internal void Deliver() => _owner.CompleteWindowsScroll(_sender, Id);
        internal int SubmitObsolete(double destination) => _owner.ScrollWindowsTo(destination, _generation);
        internal int Retained => _owner._windowsIntent?.PendingOperations ?? 0;

        public void Dispose()
        {
            _sender.ScrollCompleted -= Intercept;
            if (ReferenceEquals(_sender, _owner._windowsPresenter))
                _sender.ScrollCompleted += _owner.OnWindowsPresenterScrollCompleted;
        }
    }
}
