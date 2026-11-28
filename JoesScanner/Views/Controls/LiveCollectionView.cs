using System.Collections;
#if WINDOWS
using System.Collections.Specialized;
using JoesScanner.Models;
#endif

namespace JoesScanner.Views.Controls;

public sealed class LiveCollectionView : CollectionView
{
    public static readonly BindableProperty ReadingSourceProperty = BindableProperty.Create(
        nameof(ReadingSource), typeof(IEnumerable), typeof(LiveCollectionView),
        propertyChanged: (view, _, _) => ((LiveCollectionView)view).UpdateItemsSource());

    public IEnumerable? ReadingSource
    {
        get => (IEnumerable?)GetValue(ReadingSourceProperty);
        set => SetValue(ReadingSourceProperty, value);
    }

#if WINDOWS
    public LiveCollectionView()
    {
        Loaded += (_, _) => ObserveProjection();
        Unloaded += (_, _) => StopObservingProjection();
    }

    private LiveReadingSource? _readingRows;
    internal LiveReadingSource? ReadingRows
    {
        get
        {
            // Show can precede Loaded. Resolve hidden catalog changes before the
            // viewport attempts to measure the surviving card in this source.
            if (_observedProjection == null)
                _readingRows?.CatchUp();
            return _readingRows;
        }
    }
    private INotifyCollectionChanged? _observedProjection;
    private NotifyCollectionChangedEventHandler? _forwardChange;

    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        StopObservingProjection();
        base.OnHandlerChanging(args);
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        ObserveProjection();
    }

    private void ObserveProjection()
    {
        if (!IsLoaded || Handler == null || _readingRows == null || _observedProjection != null ||
            ReadingSource is not INotifyCollectionChanged projection)
            return;
        _readingRows.CatchUp();
        _observedProjection = projection;
        // A logical view can be unparented without disconnecting its MAUI handler.
        // A long-lived recorder catalog must not keep that view or its factory alive.
        var target = new WeakReference<LiveReadingSource>(_readingRows);
        void ForwardChange(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (target.TryGetTarget(out var rows))
                rows.Apply(args);
            else if (sender is INotifyCollectionChanged retired)
                retired.CollectionChanged -= ForwardChange;
        }
        _forwardChange = ForwardChange;
        projection.CollectionChanged += _forwardChange;
    }

    private void StopObservingProjection()
    {
        if (_observedProjection != null)
            _observedProjection.CollectionChanged -= _forwardChange;
        _forwardChange = null;
        _observedProjection = null;
    }

#endif

    private void UpdateItemsSource()
    {
#if WINDOWS
        StopObservingProjection();
        _readingRows = ReadingSource is IReadOnlyList<CallItem> calls ? new(calls, 0) : null;
        _readingRows?.Complete();
        ObserveProjection();
        ItemsSource = (IEnumerable?)_readingRows ?? ReadingSource;
#else
        ItemsSource = ReadingSource;
#endif
    }
}
