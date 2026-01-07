using JoesScanner.Services;

namespace JoesScanner.Views
{
    public partial class LogPage : ContentPage
    {
        private const int MaxLines = 500;

        public LogPage()
        {
            InitializeComponent();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            RefreshLog();
        }

        private void RefreshLog()
        {
            try
            {
                var lines = AppLog.GetSnapshot(MaxLines);
                LogEditor.Text = string.Join(Environment.NewLine, lines);
            }
            catch
            {
                LogEditor.Text = "Unable to load log.";
            }
        }

        private void OnRefreshClicked(object sender, EventArgs e)
        {
            RefreshLog();
        }

        private async void OnCopyClicked(object sender, EventArgs e)
        {
            try
            {
                var text = LogEditor.Text ?? string.Empty;
                await Clipboard.Default.SetTextAsync(text);
                await DisplayAlertAsync("Copied", "Log copied to clipboard.", "OK");
            }
            catch
            {
                await DisplayAlertAsync("Error", "Unable to copy log.", "OK");
            }
        }
    }
}
