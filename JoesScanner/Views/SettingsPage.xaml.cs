using System;
using System.IO;
using System.Text;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;

namespace JoesScanner.Views
{
    /// <summary>
    /// Settings page code behind.
    /// Responsible for wiring up navigation and discarding unsaved
    /// connection changes when the user leaves the page.
    /// Also provides handlers for filter tap gestures.
    /// </summary>
    public partial class SettingsPage : ContentPage
    {
        /// <summary>
        /// Default constructor used by XAML.
        /// BindingContext is expected to be supplied by DI / Shell.
        /// </summary>
        public SettingsPage()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Optional constructor if you ever want to inject the view model.
        /// </summary>
        /// <param name="viewModel">Settings view model instance.</param>
        public SettingsPage(SettingsViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
        }

        /// <summary>
        /// Handler for the Close button in the header.
        /// Discards unsaved connection changes and then navigates back.
        /// </summary>
        private async void OnBackClicked(object sender, EventArgs e)
        {
            if (BindingContext is SettingsViewModel vm)
            {
                if (vm.HasChanges)
                {
                    vm.DiscardConnectionChanges();
                }
            }

            if (Shell.Current != null)
            {
                try
                {
                    await Shell.Current.GoToAsync("..");
                }
                catch
                {
                    // Ignore navigation failures.
                }
            }
        }
        private async void OnLogClicked(object sender, EventArgs e)
        {
            var lines = AppLog.GetSnapshot(500);
            var bodyText = lines.Length == 0
                ? "No log entries yet."
                : string.Join(Environment.NewLine, lines);

            // We want server URL and username from the settings VM for the file header
            string serverUrl = "(unknown)";
            string username = "(none)";

            if (BindingContext is SettingsViewModel vm)
            {
                serverUrl = string.IsNullOrWhiteSpace(vm.ServerUrl)
                    ? "(not set)"
                    : vm.ServerUrl;

                username = string.IsNullOrWhiteSpace(vm.BasicAuthUsername)
                    ? "(none)"
                    : vm.BasicAuthUsername;
            }

            // Show the log body in the popup and ask if user wants to download
            var download = await DisplayAlert("Log", bodyText, "Download", "Close");
            if (!download)
                return;

            try
            {
                // Build a header with useful debug info
                var snapshotTime = DateTime.Now;
                var platform = DeviceInfo.Current.Platform.ToString();
                var osVersion = DeviceInfo.Current.VersionString;

                var headerLines = new[]
                {
            $"Snapshot taken at: {snapshotTime:yyyy-MM-dd HH:mm} (local)",
            $"Server URL: {serverUrl}",
            $"Username: {username}",
            $"Platform: {platform} {osVersion}",
            $"Max log file size: 1 MB",
            string.Empty
        };

                var headerText = string.Join(Environment.NewLine, headerLines);

                // Compose full file content
                var fileContent = headerText + bodyText;

                // Enforce a max file size of 1 MB (in UTF-8 bytes)
                const int maxBytes = 1024 * 1024;
                var bytes = Encoding.UTF8.GetBytes(fileContent);

                if (bytes.Length > maxBytes)
                {
                    // If too large, keep the tail portion and mark as truncated
                    const string marker = "[Log truncated to fit 1 MB limit]" + "\r\n\r\n";

                    // Rough estimate of how many characters to keep
                    var ratio = (double)maxBytes / bytes.Length;
                    var keepChars = Math.Max(0, (int)(fileContent.Length * ratio));

                    // Keep the last 'keepChars' characters (most recent entries)
                    var trimmed = fileContent.Length > keepChars
                        ? fileContent.Substring(fileContent.Length - keepChars)
                        : fileContent;

                    fileContent = marker + trimmed;
                }

                // Build filename prefix with date and time (24-hour, no seconds)
                var stamp = snapshotTime.ToString("yyyy-MM-dd_HH-mm");
                var fileName = $"{stamp}_JoesScannerLog.txt";

                var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                // Optional: uncomment to use a subfolder
                // folder = Path.Combine(folder, "JoesScanner");
                // Directory.CreateDirectory(folder);

                var fullPath = Path.Combine(folder, fileName);

                File.WriteAllText(fullPath, fileContent, Encoding.UTF8);

                await DisplayAlert("Log saved", $"Log saved to:\n{fullPath}", "Close");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error saving log",
                    $"Could not save the log file:\n{ex.Message}",
                    "Close");
            }
        }

        /// <summary>
        /// If the user leaves the page (back, tab change, etc.)
        /// discard any unsaved connection changes to avoid
        /// half-applied states.
        /// </summary>
        protected override void OnDisappearing()
            {
                base.OnDisappearing();

                if (BindingContext is SettingsViewModel vm)
                {
                    if (vm.HasChanges)
                    {
                        vm.DiscardConnectionChanges();
                    }
                }
            }

        /// <summary>
        /// Tap handler for the Mute (M) cell in the Filters grid.
        /// Invokes SettingsViewModel.ToggleMuteFilterCommand with the FilterRule.
        /// </summary>
        private void OnMuteFilterTapped(object sender, TappedEventArgs e)
        {
            var rule = e.Parameter as FilterRule ?? (sender as BindableObject)?.BindingContext as FilterRule;
            if (rule == null)
                return;

            if (BindingContext is SettingsViewModel vm &&
                vm.ToggleMuteFilterCommand != null &&
                vm.ToggleMuteFilterCommand.CanExecute(rule))
            {
                vm.ToggleMuteFilterCommand.Execute(rule);
            }
        }

        /// <summary>
        /// Tap handler for the Disable (X) cell in the Filters grid.
        /// Invokes SettingsViewModel.ToggleDisableFilterCommand with the FilterRule.
        /// </summary>
        private void OnDisableFilterTapped(object sender, TappedEventArgs e)
        {
            var rule = e.Parameter as FilterRule ?? (sender as BindableObject)?.BindingContext as FilterRule;
            if (rule == null)
                return;

            if (BindingContext is SettingsViewModel vm &&
                vm.ToggleDisableFilterCommand != null &&
                vm.ToggleDisableFilterCommand.CanExecute(rule))
            {
                vm.ToggleDisableFilterCommand.Execute(rule);
            }
        }

        /// <summary>
        /// Tap handler for the Clear (Clr) cell in the Filters grid.
        /// Invokes SettingsViewModel.ClearFilterCommand with the FilterRule.
        /// </summary>
        private void OnClearFilterTapped(object sender, TappedEventArgs e)
        {
            var rule = e.Parameter as FilterRule ?? (sender as BindableObject)?.BindingContext as FilterRule;
            if (rule == null)
                return;

            if (BindingContext is SettingsViewModel vm &&
                vm.ClearFilterCommand != null &&
                vm.ClearFilterCommand.CanExecute(rule))
            {
                vm.ClearFilterCommand.Execute(rule);
            }
        }
    }
}
