#if IOS
using Microsoft.Maui.Platform;
using UIKit;

namespace JoesScanner.Controls;

public partial class SelectableEditorHandler
{
    protected override void ConnectHandler(MauiTextView platformView)
    {
        base.ConnectHandler(platformView);

        try
        {
            // Read-only but selectable.
            platformView.Editable = false;
            platformView.Selectable = true;
            platformView.UserInteractionEnabled = true;

            // Help make long-press selection more reliable.
            platformView.DelaysContentTouches = false;
        }
        catch
        {
        }
    }
}
#endif
