using Microsoft.Maui.Controls;

namespace JoesScanner.Views.Controls;

// Hosts a DataTemplate and delays creating the actual visual tree until IsLoaded becomes true.
// This is used to keep large pages responsive by allowing the first render to complete before
// constructing expensive sections.
public sealed class DeferredContentView : ContentView
{
    public static readonly BindableProperty ContentTemplateProperty = BindableProperty.Create(
        nameof(ContentTemplate),
        typeof(DataTemplate),
        typeof(DeferredContentView),
        default(DataTemplate),
        propertyChanged: (b, o, n) => ((DeferredContentView)b).TryInflate());

    public static readonly BindableProperty IsLoadedProperty = BindableProperty.Create(
        nameof(IsLoaded),
        typeof(bool),
        typeof(DeferredContentView),
        false,
        propertyChanged: (b, o, n) => ((DeferredContentView)b).TryInflate());

    private bool _inflated;

    public DataTemplate? ContentTemplate
    {
        get => (DataTemplate?)GetValue(ContentTemplateProperty);
        set => SetValue(ContentTemplateProperty, value);
    }

    public bool IsLoaded
    {
        get => (bool)GetValue(IsLoadedProperty);
        set => SetValue(IsLoadedProperty, value);
    }

    private void TryInflate()
    {
        if (_inflated)
            return;

        if (!IsLoaded)
            return;

        var template = ContentTemplate;
        if (template is null)
            return;

        try
        {
            var content = template.CreateContent();
            if (content is View v)
            {
                Content = v;
                _inflated = true;
            }
        }
        catch
        {
            // Best effort. If a template fails to inflate, do not crash the app.
        }
    }
}
