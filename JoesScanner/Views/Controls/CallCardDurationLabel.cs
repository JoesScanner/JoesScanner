using JoesScanner.Models;

namespace JoesScanner.Views.Controls;

public sealed class CallCardDurationLabel : Label
{
    public static readonly BindableProperty MetadataProperty = BindableProperty.Create(
        nameof(Metadata), typeof(CallCardMetadata), typeof(CallCardDurationLabel),
        propertyChanged: (view, _, _) => ((CallCardDurationLabel)view).Render());

    public static readonly BindableProperty CardLayoutProperty = BindableProperty.Create(
        nameof(CardLayout), typeof(CallCardLayout), typeof(CallCardDurationLabel), CallCardLayout.Default,
        propertyChanged: (view, _, _) => ((CallCardDurationLabel)view).Render());

    public static readonly BindableProperty CompactFormatProperty = BindableProperty.Create(
        nameof(CompactFormat), typeof(bool), typeof(CallCardDurationLabel), false,
        propertyChanged: (view, _, _) => ((CallCardDurationLabel)view).Render());

    public CallCardDurationLabel()
    {
        IsVisible = false;
        LineBreakMode = LineBreakMode.NoWrap;
        HorizontalTextAlignment = TextAlignment.End;
    }

    public CallCardMetadata? Metadata
    {
        get => (CallCardMetadata?)GetValue(MetadataProperty);
        set => SetValue(MetadataProperty, value);
    }

    public CallCardLayout? CardLayout
    {
        get => (CallCardLayout?)GetValue(CardLayoutProperty);
        set => SetValue(CardLayoutProperty, value);
    }

    public bool CompactFormat
    {
        get => (bool)GetValue(CompactFormatProperty);
        set => SetValue(CompactFormatProperty, value);
    }

    private void Render()
    {
        var text = CardLayout?.GetCornerDuration(Metadata, CompactFormat);
        Text = text;
        IsVisible = text != null;
    }
}
