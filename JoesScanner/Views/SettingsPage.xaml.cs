using System;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;
using Microsoft.Maui.Controls;

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
            var text = lines.Length == 0
                ? "No log entries yet."
                : string.Join(Environment.NewLine, lines);

            await DisplayAlert("Log", text, "Close");
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
