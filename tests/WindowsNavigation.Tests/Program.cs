using System.Collections.Concurrent;
using JoesScanner.WinUI;
using Microsoft.UI.Dispatching;

internal static class WindowsNavigationTests
{
    public static void Main()
    {
        EntryPoint.Main([]);
        if (!Attribute.IsDefined(typeof(EntryPoint).GetMethod(nameof(EntryPoint.Main))!, typeof(STAThreadAttribute)))
            throw new InvalidOperationException("The WinUI entry point must run in the STA apartment.");

        Console.WriteLine("PASS Windows startup installs the owning dispatcher context before an async UI callback");
    }
}

// Run the actual Windows entry point with a managed dispatcher in place of the native WinUI host.
namespace JoesScanner.WinUI
{
    internal sealed class App
    {
        internal static Task? Navigation { get; private set; }

        public App()
        {
            var ownerThread = Environment.CurrentManagedThreadId;
            var animation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Navigation = ContinueAfterAnimationAsync(animation.Task, ownerThread);
            animation.SetResult();
        }

        private static async Task ContinueAfterAnimationAsync(Task animation, int ownerThread)
        {
            await animation;
            if (Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException("The navigation continuation left the UI thread.");
        }
    }
}

namespace Microsoft.UI.Xaml
{
    internal sealed class ApplicationInitializationCallbackParams;

    internal static class Application
    {
        public static void Start(Action<ApplicationInitializationCallbackParams> initialize)
        {
            var callerContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                initialize(new());
                DispatcherQueue.GetForCurrentThread().DrainUntil(App.Navigation!);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(callerContext);
            }
        }
    }
}

namespace Microsoft.UI.Dispatching
{
    internal sealed class DispatcherQueue
    {
        private static readonly DispatcherQueue Queue = new();
        private readonly BlockingCollection<Action> _posted = new();

        public static DispatcherQueue GetForCurrentThread() => Queue;
        public void Post(Action continuation) => _posted.Add(continuation);

        public void DrainUntil(Task work)
        {
            while (!work.IsCompleted)
            {
                if (_posted.TryTake(out var continuation, TimeSpan.FromMilliseconds(100)))
                    continuation();
                else
                    break;
            }
            work.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    internal sealed class DispatcherQueueSynchronizationContext(DispatcherQueue dispatcher) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            dispatcher.Post(() => callback(state));
    }
}

namespace WinRT
{
    internal static class ComWrappersSupport
    {
        public static void InitializeComWrappers() { }
    }
}
