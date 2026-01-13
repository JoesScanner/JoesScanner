using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using System.Linq;

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
            }
        }

        private async void OnLogTapped(object sender, EventArgs e)
        {
            try
            {
                var lines = AppLog.GetSnapshot(600);
                var text = (lines == null || lines.Length == 0)
                    ? "No log entries yet."
                    : string.Join(Environment.NewLine, lines);

                var editor = new Editor
                {
                    Text = text,
                    IsReadOnly = true,
                    AutoSize = EditorAutoSizeOption.Disabled,
                    FontSize = 12,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill
                };

                var copyButton = new Button { Text = "Copy" };
                copyButton.Clicked += async (_, __) =>
                {
                    try
                    {
                        await Clipboard.Default.SetTextAsync(text);
                    }
                    catch
                    {
                    }
                };

#if WINDOWS
                var shareButton = new Button { Text = "Close" };
#else
                var shareButton = new Button { Text = "Share" };
#endif

                var closeButton = new Button { Text = "Close" };

#if WINDOWS
                shareButton.Clicked += async (_, __) =>
                {
                    try
                    {
                        await Shell.Current.Navigation.PopModalAsync();
                    }
                    catch
                    {
                    }
                };
#else
                shareButton.Clicked += async (_, __) =>
                {
                    try
                    {
                        await Share.Default.RequestAsync(new ShareTextRequest
                        {
                            Text = text,
                            Title = "Share log"
                        });
                    }
                    catch
                    {
                    }
                };
#endif

                closeButton.Clicked += async (_, __) =>
                {
                    try
                    {
                        await Shell.Current.Navigation.PopModalAsync();
                    }
                    catch
                    {
                    }
                };

                var buttonBar = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Star },
                        new ColumnDefinition { Width = GridLength.Star },
                        new ColumnDefinition { Width = GridLength.Star }
                    },
                    ColumnSpacing = 8
                };

                buttonBar.Add(copyButton, 0, 0);
                buttonBar.Add(shareButton, 1, 0);
                buttonBar.Add(closeButton, 2, 0);

                var root = new Grid
                {
                    Padding = new Thickness(12),
                    RowDefinitions =
                    {
                        new RowDefinition { Height = GridLength.Star },
                        new RowDefinition { Height = GridLength.Auto }
                    },
                    RowSpacing = 10
                };

                var scroll = new ScrollView { Content = editor };

                root.Add(scroll);
                Grid.SetRow(scroll, 0);

                root.Add(buttonBar);
                Grid.SetRow(buttonBar, 1);

                var page = new ContentPage
                {
                    Title = "Log",
                    Content = root
                };

                await Shell.Current.Navigation.PushModalAsync(new NavigationPage(page));
            }
            catch
            {
            }
        }

        private async void OnSiteTapped(object sender, EventArgs e)
        {
            try
            {
                await Launcher.Default.OpenAsync("https://www.joesscanner.com/");
            }
            catch
            {
            }
        }

        private void OnCallSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (sender is not CollectionView cv)
                    return;

                var selected = e.CurrentSelection?.FirstOrDefault() as CallItem;

                // Always clear selection so the same item can be tapped again.
                cv.SelectedItem = null;

                if (selected == null)
                    return;

                if (BindingContext is MainViewModel vm && vm.PlayAudioCommand != null)
                {
                    if (vm.PlayAudioCommand.CanExecute(selected))
                        vm.PlayAudioCommand.Execute(selected);
                }
            }
            catch
            {
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
