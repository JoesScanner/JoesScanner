#if ANDROID
using Android.Text;
using Android.Views;
using Microsoft.Maui.Platform;

namespace JoesScanner.Controls;

public partial class SelectableEditorHandler
{
    protected override void ConnectHandler(MauiAppCompatEditText platformView)
    {
        base.ConnectHandler(platformView);

        try
        {
            // Allow selection and copy even when effectively read-only.
            platformView.SetTextIsSelectable(true);
            platformView.LongClickable = true;
            platformView.Clickable = true;
            platformView.Focusable = true;
            platformView.FocusableInTouchMode = true;

            // Avoid popping the keyboard when tapping to select.
            platformView.ShowSoftInputOnFocus = false;
            platformView.InputType = InputTypes.Null;

            // Keep scroll behavior usable for long logs.
            platformView.SetHorizontallyScrolling(false);
            platformView.VerticalScrollBarEnabled = true;

            // Prevent edits while still allowing selection.
            platformView.KeyListener = null;
        }
        catch
        {
        }
    }
}
#endif
