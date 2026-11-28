using System.Xml.Linq;
using JoesScanner.Models;
using JoesScanner.Views.Controls;
using Microsoft.Maui.Controls.Xaml;

namespace JoesScanner.Views;

// Inflate the exact Live DataTemplate shipped by MainPage. Only the surrounding
// page commands are substituted so that this host can run without a recorder.
public sealed class MainPage : ContentPage
{
    private readonly Action<CallItem> _sizeChanged;
    internal HashSet<LiveTranscriptLabel> LoadedTranscripts { get; } = [];
    internal (CallItem? Card, int Count) TappedCard { get; private set; }
    internal (CallItem? Card, int Count) FavoriteClick { get; private set; }
    public sealed record CardOptions(CallCardLayout Layout);
    public CardOptions CallCardOptions { get; } = new(CallCardLayout.Default);
    public bool What3WordsLinksEnabled => true;
    internal DataTemplate LiveCardTemplate => (DataTemplate)Resources["LiveCard"];

    internal MainPage(Action<CallItem> sizeChanged)
    {
        _sizeChanged = sizeChanged;
        using var stream = GetType().Assembly.GetManifestResourceStream("LivePageMarkup")!;
        var production = XElement.Load(stream);
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var list = production.Descendants().Single(el => (string?)el.Attribute(x + "Name") == "CallsView");
        var template = new XElement(list.Descendants().Single(el => el.Name.LocalName == "DataTemplate"));
        template.SetAttributeValue(x + "Key", "LiveCard");
        var page = new XElement(production.Name, production.Attributes().Where(a => a.IsNamespaceDeclaration),
            new XAttribute(x + "Name", "Page"), new XElement(production.Name.Namespace + "ContentPage.Resources", template));
        this.LoadFromXaml(page.ToString());
        BindingContext = this;
    }

    private void OnLiveCardSizeChanged(object? sender, EventArgs e)
    {
        if (sender is Border { BindingContext: CallItem call })
            _sizeChanged(call);
    }

    private void OnLiveTranscriptLoaded(object? sender, EventArgs e)
    {
        if (sender is LiveTranscriptLabel label)
        {
            LoadedTranscripts.Add(label);
            label.ShowCorrectionFeedback(true);
        }
    }

    private void OnLiveTranscriptUnloaded(object? sender, EventArgs e)
    {
        if (sender is LiveTranscriptLabel label)
        {
            LoadedTranscripts.Remove(label);
            label.ShowCorrectionFeedback(false);
        }
    }

    private void OnCallTapped(object? sender, TappedEventArgs e) =>
        TappedCard = (e.Parameter as CallItem, TappedCard.Count + 1);
    private void OnFavoriteCallClicked(object? sender, EventArgs e) =>
        FavoriteClick = ((sender as Button)?.BindingContext as CallItem, FavoriteClick.Count + 1);
    private void OnDetectedAddressTapped(object? sender, TappedEventArgs e) { }
    private void OnWhat3WordsTapped(object? sender, TappedEventArgs e) { }
    private void OnWhat3WordsCopyTapped(object? sender, TappedEventArgs e) { }
    private void OnWhat3WordsCoordsTapped(object? sender, TappedEventArgs e) { }
    private void OnWhat3WordsCoordsCopyTapped(object? sender, TappedEventArgs e) { }
}
