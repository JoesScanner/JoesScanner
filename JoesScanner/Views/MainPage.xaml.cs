using JoesScanner.Models;
using JoesScanner.ViewModels;

namespace JoesScanner.Views
{
    public partial class MainPage : ContentPage
    {
        private readonly MainViewModel _viewModel;

        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            BindingContext = _viewModel;
        }

        private async void OnSettingsTapped(object sender, EventArgs e)
        {
            try
            {
                await Shell.Current.GoToAsync("settings");
            }
            catch
            {
                // Swallow navigation errors for now
            }
        }
        private void OnCallTapped(object sender, TappedEventArgs e)
        {
            if (sender is not TapGestureRecognizer tap)
                return;

            if (tap.BindingContext is not CallItem item)
                return;

            if (_viewModel.PlayAudioCommand?.CanExecute(item) == true)
                _viewModel.PlayAudioCommand.Execute(item);
        }

        private async void OnSiteTapped(object sender, EventArgs e)
        {
            try
            {
                await Launcher.Default.OpenAsync("https://www.joesscanner.com/");
            }
            catch
            {
                // Swallow browser launch errors for now
            }
        }
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (BindingContext is MainViewModel vm)
            {
                await vm.TryAutoReconnectAsync();
            }
        }

    }
}
