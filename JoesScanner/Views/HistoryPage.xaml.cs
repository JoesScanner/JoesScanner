using Microsoft.Maui.ApplicationModel;
using JoesScanner.Models;
using JoesScanner.ViewModels;

namespace JoesScanner.Views
{
    public partial class HistoryPage : ContentPage
    {
        private readonly HistoryViewModel _viewModel;

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
        }

        void OnScrollRequested(CallItem call, ScrollToPosition position)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    CallsList.ScrollTo(call, position: position, animate: false);
                }
                catch
                {
                }
            });
        }
    }
}
