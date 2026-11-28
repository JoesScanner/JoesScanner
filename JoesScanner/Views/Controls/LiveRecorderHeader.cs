namespace JoesScanner.Views.Controls;

internal sealed class LiveRecorderHeader : Grid
{
    internal Button NewCalls { get; }

    internal LiveRecorderHeader(Guid id, Label name, View status)
    {
        ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Auto)];
        ColumnSpacing = 5;
        MinimumHeightRequest = 26;
        Padding = new Thickness(4, 2);

        NewCalls = new Button { FontSize = 12, IsVisible = false, AutomationId = $"LiveNewCalls-{id:N}" };
        NewCalls.SetDynamicResource(StyleProperty, "PillButtonStyle");
        SemanticProperties.SetHint(NewCalls, "Return to the newest recordings from this server");
        this.Add(name);
        this.Add(status, 1, 0);
        this.Add(NewCalls, 2, 0);
    }
}
