using AView = Android.Views.View;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Activity;
using AndroidX.Core.Graphics;
using AndroidX.Core.View;
using JoesScanner.Services;

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
    // If audio is enabled, the task is moved to the background so audio can continue.
    // If audio is disabled, normal back behavior runs (closes the activity and app).
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
            var audioEnabled = AppStateStore.GetBool("audio_enabled", true);

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

        // Enable Android edge-to-edge drawing so the app behaves correctly on Android 15+
        // while still working on earlier Android versions.
        WindowCompat.SetDecorFitsSystemWindows(Window, false);
        ApplyEdgeToEdgeInsets();

        // Register a back handler via AndroidX (works across Android versions and avoids deprecated APIs).
        OnBackPressedDispatcher.AddCallback(this, new AudioBackPressedCallback(this));
    }

    private void ApplyEdgeToEdgeInsets()
    {
        var content = FindViewById<ViewGroup>(Android.Resource.Id.Content);
        var rootView = content?.GetChildAt(0);

        if (rootView is null)
        {
            return;
        }

        var initialLeft = rootView.PaddingLeft;
        var initialTop = rootView.PaddingTop;
        var initialRight = rootView.PaddingRight;
        var initialBottom = rootView.PaddingBottom;

        ViewCompat.SetOnApplyWindowInsetsListener(rootView, new EdgeToEdgeInsetsListener(
            initialLeft,
            initialTop,
            initialRight,
            initialBottom));

        ViewCompat.RequestApplyInsets(rootView);
    }

    private sealed class EdgeToEdgeInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        private readonly int _initialLeft;
        private readonly int _initialTop;
        private readonly int _initialRight;
        private readonly int _initialBottom;

        public EdgeToEdgeInsetsListener(int initialLeft, int initialTop, int initialRight, int initialBottom)
        {
            _initialLeft = initialLeft;
            _initialTop = initialTop;
            _initialRight = initialRight;
            _initialBottom = initialBottom;
        }

        public WindowInsetsCompat? OnApplyWindowInsets(AView? v, WindowInsetsCompat? insets)
        {
            if (v is null || insets is null)
            {
                return insets;
            }

            var systemBars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            var ime = insets.GetInsets(WindowInsetsCompat.Type.Ime());

            // Use the larger bottom inset so content stays above either the system navigation bar
            // or the on-screen keyboard.
            var bottomInset = Math.Max(systemBars.Bottom, ime.Bottom);

            v.SetPadding(
                _initialLeft + systemBars.Left,
                _initialTop + systemBars.Top,
                _initialRight + systemBars.Right,
                _initialBottom + bottomInset);

            return insets;
        }
    }
}
