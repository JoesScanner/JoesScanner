using System.Diagnostics;
using JoesScanner.Services;
using Microsoft.Maui.Layouts;

namespace JoesScanner.Views.Controls;

public sealed class SettingsLayoutGrid : Grid
{
    private bool _traceMeasure;
    private bool _traceArrange;

    internal void BeginLayoutTrace()
    {
        _traceMeasure = true;
        _traceArrange = true;
    }

    internal void EndLayoutTrace()
    {
        _traceMeasure = false;
        _traceArrange = false;
    }

    protected override ILayoutManager CreateLayoutManager() =>
        new TimingLayoutManager(this, base.CreateLayoutManager());

    private sealed class TimingLayoutManager(SettingsLayoutGrid grid, ILayoutManager layout) : ILayoutManager
    {
        public Size Measure(double widthConstraint, double heightConstraint)
        {
            if (!grid._traceMeasure || !grid.IsVisible)
                return layout.Measure(widthConstraint, heightConstraint);

            grid._traceMeasure = false;
            var start = Stopwatch.GetTimestamp();
            var size = layout.Measure(widthConstraint, heightConstraint);
            var elapsed = Stopwatch.GetElapsedTime(start);
            AppLog.Add(() => $"Settings timing: first visible root measure={elapsed.TotalMilliseconds:F1} ms.");
            return size;
        }

        public Size ArrangeChildren(Rect bounds)
        {
            if (!grid._traceArrange || !grid.IsVisible)
                return layout.ArrangeChildren(bounds);

            grid._traceArrange = false;
            var start = Stopwatch.GetTimestamp();
            var size = layout.ArrangeChildren(bounds);
            var elapsed = Stopwatch.GetElapsedTime(start);
            AppLog.Add(() => $"Settings timing: first visible root arrange={elapsed.TotalMilliseconds:F1} ms.");
            return size;
        }
    }
}
