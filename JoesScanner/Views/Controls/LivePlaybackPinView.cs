using JoesScanner.Models;

namespace JoesScanner.Views.Controls;

public sealed class LivePlaybackPinView : ContentView
{
    private readonly Grid _strip;
    private readonly Label _playing;

    public LivePlaybackPinView()
    {
        // The empty slot keeps the list's height and screen position fixed.
        HeightRequest = 26;
        _playing = Details();
        _playing.Text = "Playing";
        _playing.TextColor = Color.FromArgb("#86efac");
        _playing.FontAttributes = FontAttributes.Bold;
        _playing.MaximumWidthRequest = 125;
        var talkgroup = Details();
        talkgroup.SetBinding(Label.TextProperty, nameof(CallItem.Talkgroup));
        var time = Details();
        time.SetBinding(Label.TextProperty, nameof(CallItem.TimeOnlyDisplay));
        _strip = new Grid
        {
            AutomationId = "ActiveLiveRecording",
            IsVisible = false,
            BackgroundColor = Color.FromArgb("#14253d"),
            Padding = new Thickness(8, 0),
            ColumnSpacing = 8,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)]
        };
        _strip.Add(_playing);
        _strip.Add(talkgroup, 1);
        _strip.Add(time, 2);
        Content = _strip;
    }

    internal CallItem? Card => _strip.BindingContext as CallItem;
    internal void Present(CallItem? card, string? recorderName = null)
    {
        _playing.Text = card != null && !string.IsNullOrWhiteSpace(recorderName) ? $"Playing · {recorderName}" : "Playing";
        if (ReferenceEquals(Card, card))
            return;
        _strip.BindingContext = card;
        _strip.IsVisible = card != null;
    }

    private static Label Details() => new()
    {
        TextColor = Colors.White, FontSize = 11, LineBreakMode = LineBreakMode.TailTruncation,
        MaxLines = 1, VerticalOptions = LayoutOptions.Center
    };
}
