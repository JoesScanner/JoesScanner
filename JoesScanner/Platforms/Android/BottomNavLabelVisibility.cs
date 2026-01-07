#if ANDROID
using Android.App;
using Google.Android.Material.BottomNavigation;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;

namespace JoesScanner.Platforms.Android;

internal static class BottomNavLabelVisibility
{
    internal static void ApplyUnlabeledMode(Activity? activity)
    {
        var decorView = activity?.Window?.DecorView;
        if (decorView is not AView root)
        {
            return;
        }

        var bottomNav = FindBottomNavigationView(root);
        if (bottomNav == null)
        {
            return;
        }

        bottomNav.LabelVisibilityMode = LabelVisibilityMode.LabelVisibilityUnlabeled;
    }

    private static BottomNavigationView? FindBottomNavigationView(AView view)
    {
        if (view is BottomNavigationView bnv)
        {
            return bnv;
        }

        if (view is AViewGroup group)
        {
            for (var i = 0; i < group.ChildCount; i++)
            {
                var child = group.GetChildAt(i);
                if (child == null)
                {
                    continue;
                }

                var found = FindBottomNavigationView(child);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }
}
#endif
