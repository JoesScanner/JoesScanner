using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JoesScanner.Views.Controls
{
    public partial class LookupModalPage : ContentPage
    {
        private readonly IReadOnlyList<string> _all;
        private readonly ObservableCollection<string> _filtered;
        private readonly string _cancel;

        private readonly TaskCompletionSource<string?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> Result => _tcs.Task;

        public LookupModalPage(string title, string cancel, IReadOnlyList<string> items)
        {
            InitializeComponent();

            _all = items ?? Array.Empty<string>();
            _filtered = new ObservableCollection<string>(_all);
            _cancel = string.IsNullOrWhiteSpace(cancel) ? "Cancel" : cancel;

            TitleLabel.Text = title;
            CancelLabel.Text = _cancel;
            List.ItemsSource = _filtered;
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // If the modal is dismissed by back gesture or other navigation, complete with null.
            if (!_tcs.Task.IsCompleted)
                _tcs.TrySetResult(null);
        }

        private async void OnCancelTapped(object sender, TappedEventArgs e)
        {
            try
            {
                _tcs.TrySetResult(null);
                await Navigation.PopModalAsync(animated: true);
            }
            catch
            {
            }
        }

        private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var selected = e.CurrentSelection?.FirstOrDefault() as string;

                if (sender is CollectionView cv)
                    cv.SelectedItem = null;

                if (string.IsNullOrWhiteSpace(selected))
                    return;

                _tcs.TrySetResult(selected);
                await Navigation.PopModalAsync(animated: true);
            }
            catch
            {
            }
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var q = (e.NewTextValue ?? string.Empty).Trim();
                IEnumerable<string> next;

                if (string.IsNullOrWhiteSpace(q))
                {
                    next = _all;
                }
                else
                {
                    next = _all.Where(s => s.Contains(q, StringComparison.OrdinalIgnoreCase));
                }

                _filtered.Clear();
                foreach (var s in next)
                    _filtered.Add(s);
            }
            catch
            {
            }
        }
    }
}
