using JoesScanner.ViewModels;

namespace JoesScanner.Views
{
    public partial class CommunicationsPage : ContentPage
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

            try
            {
                await _viewModel.OnPageOpenedAsync();
            }
            catch
            {
            }
        }

        protected override async void OnDisappearing()
        {
            try
            {
                await _viewModel.OnPageClosedAsync();
            }
            catch
            {
            }

            base.OnDisappearing();
        }
    }
}
