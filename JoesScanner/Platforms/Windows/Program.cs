using WinRT;

namespace JoesScanner.WinUI;

internal static class EntryPoint
{
    [MTAThread]
    public static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        Microsoft.UI.Xaml.Application.Start((Microsoft.UI.Xaml.ApplicationInitializationCallbackParams p) =>
        {
            _ = new App();
        });
    }
}
