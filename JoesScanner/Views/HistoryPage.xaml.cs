using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;

namespace JoesScanner.Views
{
    public partial class HistoryPage : ContentPage
    {
        private readonly HistoryViewModel _viewModel;
        private int _ignoreNextScrollEvents;
        private bool _isDropdownOpen;
        private bool _isRenamingProfileName;

        public HistoryPage(HistoryViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            BindingContext = _viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            _viewModel.ScrollRequested -= OnScrollRequested;
            _viewModel.ScrollRequested += OnScrollRequested;

            try
            {
                await _viewModel.OnPageOpenedAsync();
            }
            catch
            {
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            _viewModel.ScrollRequested -= OnScrollRequested;

            try
            {
                _ = _viewModel.OnPageClosedAsync();
            }
            catch
            {
            }
        }

        private void OnScrollRequested(CallItem call, ScrollToPosition position)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    _ignoreNextScrollEvents = 3;
                    CallsList.ScrollTo(call, position: position, animate: false);
                }
                catch
                {
                }
            });
        }

        private void OnCallSelected(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var selected = e.CurrentSelection?.FirstOrDefault() as CallItem;

                // Clear selection immediately so the same call can be tapped again.
                if (sender is CollectionView cv)
                    cv.SelectedItem = null;

                if (selected == null)
                    return;

                if (BindingContext is HistoryViewModel vm)
                {
                    var cmd = vm.PlayFromCallCommand;
                    if (cmd != null && cmd.CanExecute(selected))
                        cmd.Execute(selected);
                }
            }
            catch
            {
            }
        }

        private void OnCallsScrolled(object sender, ItemsViewScrolledEventArgs e)
        {
            try
            {
                if (BindingContext is not HistoryViewModel vm)
                    return;

                if (_ignoreNextScrollEvents > 0)
                {
                    _ignoreNextScrollEvents--;
                    return;
                }

                // Do not block UI thread. VM throttles and serializes paging internally.
                _ = vm.OnCallsListScrolledAsync(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);
            }
            catch
            {
            }
        }

        private async void OnReceiverTapped(object sender, TappedEventArgs e)
        {
            await ShowLookupAsync(
                title: "Receiver",
                getItems: () => _viewModel.Receivers,
                getCurrent: () => _viewModel.SelectedReceiver,
                setSelected: item => _viewModel.SelectedReceiver = item);
        }

        private async void OnSiteTapped(object sender, TappedEventArgs e)
        {
            await ShowLookupAsync(
                title: "Site",
                getItems: () => _viewModel.Sites,
                getCurrent: () => _viewModel.SelectedSite,
                setSelected: item => _viewModel.SelectedSite = item);
        }

        private async void OnTalkgroupTapped(object sender, TappedEventArgs e)
        {
            await ShowLookupAsync(
                title: "Talkgroup",
                getItems: () => _viewModel.Talkgroups,
                getCurrent: () => _viewModel.SelectedTalkgroup,
                setSelected: item => _viewModel.SelectedTalkgroup = item);
        }

        private async void OnProfileTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (_isDropdownOpen)
                    return;

                _isDropdownOpen = true;

                const string cancel = "Cancel";
                const string none = "None";

                var list = _viewModel.FilterProfiles.ToList();
                var options = new List<string> { none };
                options.AddRange(list.Select(p => p.Name));

                var choice = await DisplayActionSheet("Profile", cancel, null, options.ToArray());
                if (string.IsNullOrWhiteSpace(choice) || string.Equals(choice, cancel, StringComparison.Ordinal))
                    return;

                if (string.Equals(choice, none, StringComparison.Ordinal))
                {
                    await _viewModel.SelectFilterProfileAsync(null, apply: false);
                    return;
                }

                var selected = list.FirstOrDefault(p => string.Equals(p.Name, choice, StringComparison.Ordinal));
                if (selected == null)
                    return;

                await _viewModel.SelectFilterProfileAsync(selected, apply: true);
            }
            catch
            {
            }
            finally
            {
                _isDropdownOpen = false;
            }
        }

        private async void OnSaveProfileClicked(object sender, EventArgs e)
{
    try
    {
        var option = (_viewModel.SelectedFilterProfileNameOption ?? string.Empty).Trim();
                if (_isRenamingProfileName && !string.Equals(option, "New", StringComparison.Ordinal))
                {
                    _isRenamingProfileName = false;
                }


        // Rename mode: user tapped Edit, typed a new name, then tapped Save.
        if (_isRenamingProfileName)
        {
            var renameTo = (_viewModel.FilterProfileNameDraft ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(renameTo))
            {
                await DisplayAlert("Name required", "Enter a new profile name, then tap Save.", "OK");
                return;
            }

            if (string.Equals(renameTo, "None", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(renameTo, "New", StringComparison.OrdinalIgnoreCase))
            {
                await DisplayAlert("Invalid name", "Choose a different profile name.", "OK");
                return;
            }

            await _viewModel.RenameSelectedProfileAsync(renameTo);
            _isRenamingProfileName = false;

            // Switch back to the renamed profile in the picker.
            _viewModel.SelectedFilterProfileNameOption = renameTo;
            return;
        }

        var targetName = option;

        if (string.Equals(option, "None", StringComparison.Ordinal))
        {
            await DisplayAlert("Select a profile", "Choose an existing profile to overwrite, or choose New to create a new one.", "OK");
            return;
        }

        if (string.Equals(option, "New", StringComparison.Ordinal))
        {
            targetName = (_viewModel.FilterProfileNameDraft ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(targetName))
            {
                await DisplayAlert("Name required", "Enter a new profile name, then tap Save.", "OK");
                return;
            }
        }

        if (string.Equals(targetName, "None", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetName, "New", StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlert("Invalid name", "Choose a different profile name.", "OK");
            return;
        }

        await _viewModel.SaveCurrentFiltersAsync(targetName);
    }
    catch
    {
    }
}


private async void OnEditProfileClicked(object sender, EventArgs e)
{
    try
    {
        var option = (_viewModel.SelectedFilterProfileNameOption ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(option) ||
            string.Equals(option, "None", StringComparison.Ordinal) ||
            string.Equals(option, "New", StringComparison.Ordinal))
        {
            await DisplayAlert("Select a profile", "Select an existing profile to rename.", "OK");
            return;
        }

        // Put the UI into rename mode: show the name entry and keep the current profile selected.
        _isRenamingProfileName = true;
        _viewModel.FilterProfileNameDraft = option;
        _viewModel.SelectedFilterProfileNameOption = "New";
    }
    catch
    {
    }
}
		private async void OnDeleteProfileClicked(object sender, EventArgs e)
{
    try
    {
        var option = (_viewModel.SelectedFilterProfileNameOption ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(option) ||
            string.Equals(option, "None", StringComparison.Ordinal) ||
            string.Equals(option, "New", StringComparison.Ordinal))
        {
            await DisplayAlert("Select a profile", "Select an existing profile to delete.", "OK");
            return;
        }

        var confirm = await DisplayAlert("Delete profile?", $"Delete '{option}'?", "Delete", "Cancel");
        if (!confirm)
            return;

        await _viewModel.DeleteSelectedProfileAsync();
        await _viewModel.SelectFilterProfileAsync(null, apply: false);
    }
    catch
    {
    }
}

        private async void OnManageProfileTapped(object sender, TappedEventArgs e)
        {
            try
            {
                var current = _viewModel.SelectedFilterProfile;
                if (current == null)
                {
                    await DisplayAlert("No profile selected", "Select a profile first.", "OK");
                    return;
                }

                const string cancel = "Cancel";
                const string rename = "Rename";
                const string delete = "Delete";
                const string clear = "Clear selection";

                var choice = await DisplayActionSheet("Manage profile", cancel, null, rename, delete, clear);
                if (string.IsNullOrWhiteSpace(choice) || string.Equals(choice, cancel, StringComparison.Ordinal))
                    return;

                if (string.Equals(choice, clear, StringComparison.Ordinal))
                {
                    await _viewModel.SelectFilterProfileAsync(null, apply: false);
                    return;
                }

                if (string.Equals(choice, rename, StringComparison.Ordinal))
                {
                    var newName = (_viewModel.FilterProfileNameDraft ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        await DisplayAlert("Profile name required", "Enter a profile name, then choose Rename.", "OK");
                        return;
                    }

                    if (string.Equals(newName, current.Name, StringComparison.Ordinal))
                        return;

                    var existing = _viewModel.FilterProfiles.FirstOrDefault(p => string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && !string.Equals(existing.Id, current.Id, StringComparison.Ordinal))
                    {
                        await DisplayAlert("Name already exists", "Choose a different name.", "OK");
                        return;
                    }

                    await _viewModel.RenameSelectedProfileAsync(newName);
                    return;
                }

                if (string.Equals(choice, delete, StringComparison.Ordinal))
                {
                    var confirm = await DisplayAlert("Delete profile?", $"Delete '{current.Name}'?", "Delete", "Cancel");
                    if (!confirm)
                        return;

                    await _viewModel.DeleteSelectedProfileAsync();
                    return;
                }
            }
            catch
            {
            }
        }



        private async Task ShowLookupAsync(
            string title,
            Func<System.Collections.Generic.IEnumerable<HistoryLookupItem>> getItems,
            Func<HistoryLookupItem?> getCurrent,
            Action<HistoryLookupItem?> setSelected)
        {
            try
            {
                if (_isDropdownOpen)
                    return;

                var items = getItems()?.ToList();
                if (items == null || items.Count == 0)
                    return;

                _isDropdownOpen = true;

                const string cancel = "Cancel";
                var labels = items.Select(i => i.Label).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                var choice = await DisplayActionSheet(title, cancel, null, labels);
                if (string.IsNullOrWhiteSpace(choice) || string.Equals(choice, cancel, StringComparison.Ordinal))
                    return;

                var selected = items.FirstOrDefault(i => string.Equals(i.Label, choice, StringComparison.Ordinal));
                if (selected == null)
                    return;

                var current = getCurrent();
                if (ReferenceEquals(current, selected))
                    return;

                setSelected(selected);
            }
            catch
            {
            }
            finally
            {
                _isDropdownOpen = false;
            }
        }
    }
}
