using Android.App;
using Android.Runtime;

namespace JoesScanner
{
    // Android application entry point that bootstraps the MAUI app.
    [Application]
    public class MainApplication : MauiApplication
    {
        // Standard Android application constructor used by the runtime.
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        // Creates and returns the MAUI app instance.
        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
