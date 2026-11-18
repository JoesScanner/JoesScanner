using JoesScanner.Models;

namespace JoesScanner.Views.Controls;

public sealed class CallCardMetadataLabel : Label
{
    public static readonly BindableProperty MetadataProperty = BindableProperty.Create(
        nameof(Metadata), typeof(CallCardMetadata), typeof(CallCardMetadataLabel),
        propertyChanged: (view, _, _) => ((CallCardMetadataLabel)view).Render());

    public static readonly BindableProperty CardLayoutProperty = BindableProperty.Create(
        nameof(CardLayout), typeof(CallCardLayout), typeof(CallCardMetadataLabel), CallCardLayout.Default,
        propertyChanged: (view, _, _) => ((CallCardMetadataLabel)view).Render());

    public static readonly BindableProperty SectionProperty = BindableProperty.Create(
        nameof(Section), typeof(CallCardSection), typeof(CallCardMetadataLabel), CallCardSection.Header,
        propertyChanged: (view, _, _) => ((CallCardMetadataLabel)view).Render());

    public CallCardMetadataLabel() => IsVisible = false;

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

    public CallCardSection Section
    {
        get => (CallCardSection)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    private void Render()
    {
        if (Metadata is not { } metadata || CardLayout is not { } layout)
        {
            FormattedText = null;
            Text = null;
            IsVisible = false;
            return;
        }

        var fields = layout.GetDisplayFields(metadata, Section);
        if (Section == CallCardSection.Secondary)
        {
            FormattedText = null;
            Text = string.Join(" · ", fields.Select(field => field.Text));
            IsVisible = Text.Length != 0;
            return;
        }

        FormattedString? header = null;
        foreach (var (field, text) in fields)
        {
            if (header == null)
                header = new FormattedString();
            else
                header.Spans.Add(new Span { Text = " > " });

            header.Spans.Add(new Span
            {
                Text = text,
                FontAttributes = field is CallCardField.Time or CallCardField.Talkgroup ? FontAttributes.Bold : FontAttributes.None
            });
        }

        FormattedText = header;
        IsVisible = header != null;
    }
}
