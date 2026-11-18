using System.Diagnostics;
using JoesScanner.Services;

namespace JoesScanner.Views;

// Scoped to one synchronous host add. Only control types and positions enter the log.
internal sealed class SettingsHandlerTrace : IDisposable
{
    private readonly Dictionary<View, Sample> _samples = new();
    private readonly Stack<Sample> _initializing = new();
    private readonly Sample _root;
    private readonly string _operation;

    public SettingsHandlerTrace(View view, string operation)
    {
        _operation = operation;
        _root = Watch(view, view.GetType().Name);
    }

    public long AttachStarted { private get; set; }

    private Sample Watch(View view, string path)
    {
        var sample = new Sample(path);
        _samples.Add(view, sample);
        view.HandlerChanging += OnHandlerChanging;
        view.HandlerChanged += OnHandlerChanged;
        if (view is Microsoft.Maui.IVisualTreeElement tree)
        {
            var children = tree.GetVisualChildren();
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is View child && !_samples.ContainsKey(child))
                    Watch(child, $"{path}/{i}:{child.GetType().Name}");
            }
        }
        return sample;
    }

    private void OnHandlerChanging(object? sender, HandlerChangingEventArgs e)
    {
        if (e.NewHandler == null || sender is not View view)
            return;

        var sample = _samples[view];
        sample.Start = Stopwatch.GetTimestamp();
        _initializing.Push(sample);
    }

    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is not View view || !_initializing.TryPeek(out var sample) || sample != _samples[view])
            return;

        sample.End = Stopwatch.GetTimestamp();
        _initializing.Pop();
        if (_initializing.TryPeek(out var parent))
            parent.ChildTicks += sample.End - sample.Start;
    }

    public void Dispose()
    {
        foreach (var view in _samples.Keys)
        {
            view.HandlerChanging -= OnHandlerChanging;
            view.HandlerChanged -= OnHandlerChanged;
        }

        var completed = _samples.Values.Where(sample => sample.End > 0).ToArray();
        AppLog.Add(() => $"Settings timing: {_operation} handler trace observed {completed.Length} assignments across {_samples.Count} controls.");
        if (_root.End > 0)
        {
            AppLog.Add(() => $"Settings timing: {_operation} root handler started={Stopwatch.GetElapsedTime(AttachStarted, _root.Start).TotalMilliseconds:F1} ms from host add, created={Stopwatch.GetElapsedTime(_root.Start, _root.End).TotalMilliseconds:F1} ms.");
        }

        foreach (var sample in completed.OrderByDescending(sample => sample.OwnTicks).Take(5))
        {
            AppLog.Add(() => $"Settings timing: {_operation} handler {sample.Path}, own={TimeSpan.FromSeconds((double)sample.OwnTicks / Stopwatch.Frequency).TotalMilliseconds:F1} ms, inclusive={Stopwatch.GetElapsedTime(sample.Start, sample.End).TotalMilliseconds:F1} ms.");
        }
    }

    private sealed class Sample(string path)
    {
        public string Path { get; } = path;
        public long Start { get; set; }
        public long End { get; set; }
        public long ChildTicks { get; set; }
        public long OwnTicks => End - Start - ChildTicks;
    }
}
