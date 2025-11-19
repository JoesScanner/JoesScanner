using System;
using JoesScanner.ViewModels;
using Microsoft.Maui.Controls;

namespace JoesScanner.Views
{
    /// <summary>
    /// Settings page code behind.
    /// Responsible for wiring up navigation and discarding unsaved
    /// connection changes when the user leaves the page.
    /// </summary>
    public partial class SettingsPage : ContentPage
    {
        /// <summary>
        /// Default constructor used by XAML.
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
                // If there are unsaved connection edits (server URL / mode),
                // discard them when closing the page.
                if (vm.HasChanges)
                {
                    vm.DiscardConnectionChanges();
                }
            }

            // Prefer Shell navigation if available, otherwise use Navigation stack.
            if (Shell.Current != null)
            {
                await Shell.Current.GoToAsync("..");
            }
            else if (Navigation?.NavigationStack?.Count > 0)
            {
                await Navigation.PopAsync();
            }
        }

        /// <summary>
        /// When the page is disappearing (user navigates away),
        /// discard any unsaved connection changes so they do not leak
        /// into the next time settings is opened.
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
    }
}
