using Microsoft.Maui.Controls;
using JoesScanner.Services;
using System.Diagnostics;

namespace JoesScanner.Views.Controls;

// Creates the section in its final host when the user first expands it.
public sealed class DeferredContentView : ContentView
{
    public DataTemplate? ContentTemplate { get; set; }

    internal bool TryLoadContent()
    {
        if (Content != null)
            return true;

        var started = Stopwatch.GetTimestamp();
        try
        {
            if (ContentTemplate?.CreateContent() is not View view)
                throw new InvalidOperationException("A deferred section requires a view template.");

            var created = Stopwatch.GetTimestamp();
            Content = view;
            var attached = Stopwatch.GetTimestamp();
            AppLog.Add(() => $"Settings timing: card body created={Stopwatch.GetElapsedTime(started, created).TotalMilliseconds:F1} ms, attached={Stopwatch.GetElapsedTime(created, attached).TotalMilliseconds:F1} ms.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Settings timing: deferred card creation failed ({ex.GetType().Name}).");
            return false;
        }
    }
}
