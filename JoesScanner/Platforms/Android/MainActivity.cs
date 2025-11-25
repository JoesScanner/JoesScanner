using Android.App;
using Android.Content.PM;
using Android.OS;

namespace JoesScanner;

/// <summary>
/// Main Android activity for the JoesScanner MAUI app.
/// </summary>
[Activity(
    Label = "JoesScanner",
    Theme = "@style/Maui.SplashTheme",   // Splash theme is defined in Resources/values/styles.xml
    MainLauncher = true,
    ConfigurationChanges =
        ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>
    /// Standard MAUI activity initialization.
    /// Splash image is configured via the &lt;MauiSplashScreen&gt; entry in the .csproj.
    /// </summary>
    /// <param name="savedInstanceState">Saved instance state bundle.</param>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
    }

    /// <summary>
    /// Handles the Android back button.
    /// 
    /// If AudioEnabled is true:
    ///   - The activity is moved to the background
    ///   - The process continues running so audio can keep playing
    ///
    /// If AudioEnabled is false:
    ///   - Default behavior runs and the activity (and app) will close
    ///
    /// The AudioEnabled flag is controlled by the Audio On / Off button on the main page
    /// and stored in Preferences under the "AudioEnabled" key.
    /// </summary>
    public override void OnBackPressed()
    {
        // Default value is true so that new installs behave like a radio:
        // app keeps running in the background unless user explicitly turns audio off.
        var audioEnabled = Preferences.Get("AudioEnabled", true);

        if (audioEnabled)
        {
            // Keep the app process alive and send the task to the background.
            // This allows audio playback to continue while the user uses other apps.
            MoveTaskToBack(true);
        }
        else
        {
            // Normal behavior: allow the back press to close the activity and the app.
            base.OnBackPressed();
        }
    }
}
