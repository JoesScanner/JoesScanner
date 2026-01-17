using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
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
