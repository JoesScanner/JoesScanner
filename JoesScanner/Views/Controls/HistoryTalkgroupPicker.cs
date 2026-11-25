using JoesScanner.Services;

namespace JoesScanner.Views.Controls;

internal sealed class HistoryTalkgroupPicker : ContentPage
{
    private readonly TaskCompletionSource<IReadOnlyList<HistoryLookupItem>?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Choice[] _choices;

    internal HistoryTalkgroupPicker(IReadOnlyList<HistoryLookupItem> items, IReadOnlyList<HistoryLookupItem> selected)
    {
        AutomationId = "HistoryTalkgroupPicker";
        var selectedValues = selected.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        var duplicateLabels = items.GroupBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _choices = items.Select(item => new Choice(item,
            duplicateLabels.Contains(item.Label) ? $"{item.Label} [{item.Value}]" : item.Label,
            selectedValues.Contains(item.Value))).ToArray();

        var cancel = new Button { Text = "Cancel" };
        var apply = new Button { Text = "Apply", AutomationId = "ApplyHistoryTalkgroups" };
        cancel.Clicked += async (_, _) => await CloseAsync(null);
        apply.Clicked += async (_, _) => await CloseAsync(_choices.Where(choice => choice.Selected).Select(choice => choice.Item).ToArray());

        var heading = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 8
        };
        heading.Add(new Label { Text = "Talkgroups", FontSize = 18, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center });
        heading.Add(cancel, 1, 0);
        heading.Add(apply, 2, 0);

        var search = new SearchBar { Placeholder = "Find talkgroups" };
        var list = new CollectionView
        {
            ItemsSource = _choices,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(CreateRow)
        };
        search.TextChanged += (_, e) =>
        {
            var text = e.NewTextValue?.Trim() ?? "";
            list.ItemsSource = text.Length == 0 ? _choices :
                _choices.Where(choice => choice.Display.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    choice.Item.Value.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        };

        var layout = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
            RowSpacing = 8,
            Padding = 12
        };
        layout.Add(heading);
        layout.Add(search, 0, 1);
        layout.Add(list, 0, 2);
        Content = layout;
    }

    internal Task<IReadOnlyList<HistoryLookupItem>?> Result => _result.Task;

    private static Grid CreateRow()
    {
        var check = new CheckBox { AutomationId = "HistoryTalkgroupCheckBox" };
        check.SetBinding(CheckBox.IsCheckedProperty, nameof(Choice.Selected), BindingMode.TwoWay);
        check.SetBinding(SemanticProperties.DescriptionProperty, nameof(Choice.Display));
        var label = new Label { VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.WordWrap };
        label.SetBinding(Label.TextProperty, nameof(Choice.Display));
        var row = new Grid
        {
            Padding = new Thickness(0, 2),
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)]
        };
        row.Add(check);
        row.Add(label, 1, 0);
        return row;
    }

    private async Task CloseAsync(IReadOnlyList<HistoryLookupItem>? selected)
    {
        if (!_result.TrySetResult(selected))
            return;
        await Navigation.PopModalAsync(animated: true);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _result.TrySetResult(null);
    }

    private sealed class Choice(HistoryLookupItem item, string display, bool selected)
    {
        internal HistoryLookupItem Item { get; } = item;
        public string Display { get; } = display;
        public bool Selected { get; set; } = selected;
    }
}
