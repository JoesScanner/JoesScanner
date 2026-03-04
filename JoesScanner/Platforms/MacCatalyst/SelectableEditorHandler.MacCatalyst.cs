#if MACCATALYST
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
            platformView.Editable = false;
            platformView.Selectable = true;
            platformView.UserInteractionEnabled = true;
        }
        catch
        {
        }
    }
}
#endif
