using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity;

namespace JoesScanner;

// Main Android activity for the JoesScanner MAUI app.
[Activity(
    Label = "@string/app_name",
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
    // Handles the Android back button without using the deprecated OnBackPressed override.
    // If AudioEnabled is true, the task is moved to the background so audio can continue.
    // If AudioEnabled is false, normal back behavior runs (closes the activity and app).
    private sealed class AudioBackPressedCallback : OnBackPressedCallback
    {
        private readonly MainActivity _activity;

        public AudioBackPressedCallback(MainActivity activity)
            : base(true)
        {
            _activity = activity;
        }

        public override void HandleOnBackPressed()
        {
            var audioEnabled = Preferences.Get("AudioEnabled", true);

            if (audioEnabled)
            {
                _activity.MoveTaskToBack(true);
                return;
            }

            // Temporarily disable this callback so the dispatcher can perform the default behavior.
            Enabled = false;
            _activity.OnBackPressedDispatcher.OnBackPressed();
            Enabled = true;
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Register a back handler via AndroidX (works across Android versions and avoids deprecated APIs).
        OnBackPressedDispatcher.AddCallback(this, new AudioBackPressedCallback(this));
    }
}
