using Microsoft.UI.Dispatching;
using WinRT;

namespace JoesScanner.WinUI;

internal static class EntryPoint
{
    [STAThread]
    public static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            // Match the WinUI-generated entry point so awaited UI callbacks return to this queue.
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
