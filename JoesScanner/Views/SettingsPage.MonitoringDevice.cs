using JoesScanner.ViewModels;

namespace JoesScanner.Views;

public partial class SettingsPage
{
    private async void OnNotificationsHeaderTapped(object? sender, TappedEventArgs e)
    {
        ToggleSection(NotificationsBody, NotificationsChevron);
        if (NotificationsBody.IsVisible && BindingContext is SettingsViewModel viewModel)
        {
            viewModel.RefreshNotificationTalkgroups();
            await viewModel.RefreshNotificationAccessAsync();
        }
    }

    private void OnOvernightHeaderTapped(object? sender, TappedEventArgs e) =>
        ToggleSection(OvernightBody, OvernightChevron);

    private void OnRefreshNotificationTalkgroupsClicked(object? sender, EventArgs e) =>
        (BindingContext as SettingsViewModel)?.RefreshNotificationTalkgroups();

    private void OnToggleNotificationTalkgroupClicked(object? sender, EventArgs e) =>
        (BindingContext as SettingsViewModel)?.ToggleNotificationTalkgroup();

    private async void OnAllowNotificationsClicked(object? sender, EventArgs e)
    {
        if (BindingContext is SettingsViewModel viewModel)
            await viewModel.RefreshNotificationAccessAsync(request: true);
    }
}
