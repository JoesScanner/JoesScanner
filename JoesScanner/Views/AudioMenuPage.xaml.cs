using System;
using System.Threading.Tasks;

namespace JoesScanner.Views
{
    public partial class AudioMenuPage : ContentPage
    {
        private readonly Func<double?, Task> _onSelected;

        public AudioMenuPage(Func<double?, Task> onSelected)
        {
            InitializeComponent();
            _onSelected = onSelected;
        }

        private async Task SelectAsync(double? speedStep)
        {
            try
            {
                await _onSelected(speedStep);
            }
            catch
            {
            }

            try
            {
                await Navigation.PopModalAsync();
            }
            catch
            {
            }
        }

        private Task CancelAsync()
        {
            try
            {
                return Navigation.PopModalAsync();
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        private async void OnOffClicked(object? sender, EventArgs e) => await SelectAsync(null);
        private async void On1xClicked(object? sender, EventArgs e) => await SelectAsync(0);
        private async void On125xClicked(object? sender, EventArgs e) => await SelectAsync(1);
        private async void On15xClicked(object? sender, EventArgs e) => await SelectAsync(2);
        private async void On175xClicked(object? sender, EventArgs e) => await SelectAsync(3);
        private async void On2xClicked(object? sender, EventArgs e) => await SelectAsync(4);
        private async void OnCancelClicked(object? sender, EventArgs e) => await CancelAsync();
    }
}
