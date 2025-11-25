using System;
using JoesScanner.ViewModels;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

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

        private async void OnSiteTapped(object sender, EventArgs e)
        {
            try
            {
                await Launcher.Default.OpenAsync("https://www.joesscanner.com");
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
