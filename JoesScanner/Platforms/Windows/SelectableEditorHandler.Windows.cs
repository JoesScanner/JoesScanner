#if WINDOWS
using Microsoft.UI.Xaml.Controls;

namespace JoesScanner.Controls;

public partial class SelectableEditorHandler
{
    protected override void ConnectHandler(TextBox platformView)
    {
        base.ConnectHandler(platformView);

        try
        {
            platformView.IsReadOnly = true;
            platformView.IsTabStop = true;
            platformView.AcceptsReturn = true;
            platformView.TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap;

            // In WinUI 3, scroll bar visibility is controlled via ScrollViewer attached properties.
            platformView.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto);
            platformView.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled);
        }
        catch
        {
        }
    }
}
#endif
