using JoesScanner.ViewModels;

namespace JoesScanner.Views
{
    public partial class CommunicationsPage : ContentPage, ITabShowingAware, ITabHidingAware
    {
        private readonly CommunicationsViewModel _viewModel;

        public CommunicationsPage(CommunicationsViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            BindingContext = _viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await ShowPageAsync();
        }

        protected override async void OnDisappearing()
        {
            await HidePageAsync();
            base.OnDisappearing();
        }

        public void OnTabShowing()
        {
            _ = ShowPageAsync();
        }

        public void OnTabHiding()
        {
            _ = HidePageAsync();
        }

        private async Task ShowPageAsync()
        {
            try
            {
                // Clear the badge immediately when navigating to the page.
                _viewModel.MarkAllKnownAsSeenOnNavigate();
                await _viewModel.OnPageOpenedAsync();
            }
            catch
            {
            }
        }

        private async Task HidePageAsync()
        {
            try
            {
                await _viewModel.OnPageClosedAsync();
            }
            catch
            {
            }
        }
    }
}
