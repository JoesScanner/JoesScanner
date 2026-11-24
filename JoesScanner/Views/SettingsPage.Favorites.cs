using System.Net;
using JoesScanner.Services;
using JoesScanner.ViewModels;

namespace JoesScanner.Views;

public partial class SettingsPage
{
    private bool _favoritesSigningIn;

    private void OnPrivacyNetworkHeaderTapped(object? sender, EventArgs e) =>
        ToggleSection(PrivacyNetworkBody, PrivacyNetworkChevron);

    private void OnFavoritesHeaderTapped(object? sender, EventArgs e) =>
        ToggleSection(FavoritesBodyLayout, FavoritesChevron);

    private async void OnFavoritesSignInClicked(object? sender, EventArgs e)
    {
        if (_favoritesSigningIn || BindingContext is not SettingsViewModel vm || !vm.FavoritesCanSignIn ||
            FavoritesBodyLayout.Content?.FindByName<Entry>("FavoritesAccountName") is not { } name ||
            FavoritesBodyLayout.Content?.FindByName<Entry>("FavoritesAccountPassword") is not { } password)
            return;

        _favoritesSigningIn = true;
        try
        {
            await vm.SignInForFavoritesAsync(name.Text ?? string.Empty, password.Text ?? string.Empty);
        }
        catch (ArgumentException)
        {
            await UiDialogs.AlertAsync("Sign in", "Enter your Joe’s Scanner username or email and password.", "Close");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await UiDialogs.AlertAsync("Sign in", "The username or password was not accepted.", "Close");
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Favorites sign-in did not complete ({ex.GetType().Name}).");
            await UiDialogs.AlertAsync("Sign in", "Favorites sign-in is unavailable. Saved Favorites are still on this device.", "Close");
        }
        finally
        {
            password.Text = string.Empty;
            _favoritesSigningIn = false;
        }
    }

    private async void OnFavoritesSignOutClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not SettingsViewModel vm ||
            !await UiDialogs.ConfirmAsync("Sign out?", "This also signs you out of Joe’s Scanner account services. Unsynced Favorites stay with this account on this device.",
                "Sign out", "Cancel"))
            return;
        await RunFavoritesActionAsync(vm.SignOutOfFavoritesAsync);
    }

    private async void OnImportDeviceFavoritesClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not SettingsViewModel vm)
            return;
        var favorites = vm.Favorites;
        var owner = favorites.OwnerKey;
        if (favorites.IsSignedIn && await UiDialogs.ConfirmAsync("Import device Favorites?",
            "Copy signed-out Favorites and groups into this account. Existing Favorites and deletions are preserved.",
            "Import", "Cancel"))
            await RunFavoritesActionAsync(() =>
            {
                favorites.ImportDevice(owner);
                return Task.CompletedTask;
            });
    }

    private async void OnManageFavoriteGroupsClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not SettingsViewModel vm)
            return;
        await RunFavoritesActionAsync(async () =>
        {
            var favorites = vm.Favorites;
            var owner = favorites.OwnerKey;
            var groups = favorites.Collection.Values.Where(value => value.IsGroup)
                .OrderBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var options = FavoriteOptions(groups, group => group.Label!, "New group…");
            var pick = await UiDialogs.ActionSheetAsync("Favorite groups", "Cancel", null, ["New group…", .. options]);
            if (owner != favorites.OwnerKey)
                return;
            if (pick == "New group…")
            {
                await SaveFavoriteGroupAsync(favorites, owner);
                return;
            }
            var index = Array.IndexOf(options, pick);
            if (index < 0)
                return;
            var group = groups[index];
            var action = await UiDialogs.ActionSheetAsync(group.Label!, "Cancel", "Delete group", "Rename group");
            if (action == "Rename group")
                await SaveFavoriteGroupAsync(favorites, owner, group);
            else if (action == "Delete group" && await UiDialogs.ConfirmAsync("Delete group?",
                $"Remove {group.Label}? Its talkgroups will remain in Favorites.", "Delete group", "Cancel"))
                favorites.SetValue(owner, group, deleted: true);
        });
    }

    private static async Task SaveFavoriteGroupAsync(FavoritesService favorites, string owner, FavoriteValue? group = null)
    {
        var name = await UiDialogs.PromptAsync(group == null ? "New Favorite group" : "Rename Favorite group",
            "Group name", "Save", "Cancel", group?.Label, maxLength: 128);
        if (name == null)
            return;
        var label = FavoriteValue.CleanLabel(name);
        if (label.Length == 0)
            return;
        if (favorites.Collection.Values.Any(value => value.IsGroup && value.Id != group?.Id &&
            string.Equals(value.Label, label, StringComparison.OrdinalIgnoreCase)))
        {
            await UiDialogs.AlertAsync("Favorite groups", "A group with that name already exists.", "Close");
            return;
        }
        favorites.SetValue(owner, group == null ? FavoriteValue.Group(label) : group with { Label = label });
    }

    private async void OnManageFavoritesClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not SettingsViewModel vm)
            return;
        await RunFavoritesActionAsync(async () =>
        {
            var favorites = vm.Favorites;
            var owner = favorites.OwnerKey;
            var values = favorites.Collection.Values.Where(value => !value.IsGroup)
                .OrderBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (values.Length == 0)
            {
                await UiDialogs.AlertAsync("Favorites", "Star a talkgroup on a Live call card to save it.", "Close");
                return;
            }
            var options = FavoriteOptions(values, value => $"{value.Label} · {favorites.ServerName(value)}");
            var pick = await UiDialogs.ActionSheetAsync("Saved talkgroups", "Cancel", null, options);
            var index = Array.IndexOf(options, pick);
            if (index < 0 || owner != favorites.OwnerKey)
                return;
            var favorite = values[index];
            var existing = favorites.Collection.Values.FirstOrDefault(value => value.Id == favorite.GroupId && value.IsGroup);
            var description = favorite.Label + " · " + (existing?.Label ?? "Ungrouped");
            var action = await UiDialogs.ActionSheetAsync(description, "Cancel", "Remove Favorite", "Change group");
            if (action == "Remove Favorite")
                favorites.SetValue(owner, favorite, deleted: true);
            else if (action == "Change group" && owner == favorites.OwnerKey)
            {
                var groups = favorites.Collection.Values.Where(value => value.IsGroup)
                    .OrderBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
                var names = FavoriteOptions(groups, value => value.Label!, "No group");
                var choice = await UiDialogs.ActionSheetAsync("Choose a Favorite group", "Cancel", null, ["No group", .. names]);
                var group = Array.IndexOf(names, choice);
                if (group >= 0 || choice == "No group")
                    favorites.SetValue(owner, favorite with { GroupId = group < 0 ? null : groups[group].Id });
            }
        });
    }

    private async void OnLinkFavoriteRecorderClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not SettingsViewModel vm)
            return;
        await RunFavoritesActionAsync(async () =>
        {
            var favorites = vm.Favorites;
            var owner = favorites.OwnerKey;
            var custom = vm.ScannerConnections.Where(row => !row.Managed).ToArray();
            if (custom.Length == 0)
            {
                await UiDialogs.AlertAsync("Link custom recorders", "Add a custom recorder in Server Connections first.", "Close");
                return;
            }
            var rows = custom.Select(row => row.Name + " · " + row.Endpoint).ToArray();
            var selected = Array.IndexOf(rows, await UiDialogs.ActionSheetAsync("Select a custom recorder", "Cancel", null, rows));
            if (selected < 0 || owner != favorites.OwnerKey)
                return;
            var row = custom[selected];
            var action = await UiDialogs.ActionSheetAsync(row.Name, "Cancel", null, "Copy link code", "Enter link code");
            if (action == "Copy link code" && favorites.GetLinkCode(owner, row.Id) is { } code)
            {
                await Clipboard.Default.SetTextAsync(code);
                await UiDialogs.AlertAsync("Link code copied", "Enter this code for the same custom recorder on your other device.", "Close");
            }
            else if (action == "Enter link code")
            {
                var entered = await UiDialogs.PromptAsync("Link this recorder",
                    "Enter the code copied from the same recorder on another device. Existing Favorites will be preserved.",
                    "Link", "Cancel", maxLength: 34);
                if (entered != null && !favorites.Link(owner, row.Id, entered))
                    await UiDialogs.AlertAsync("Recorder not linked", "The code is invalid, assigned to another recorder, or the account changed.", "Close");
            }
        });
    }

    private async void OnResolveFavoriteConflictClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not SettingsViewModel vm)
            return;
        await RunFavoritesActionAsync(async () =>
        {
            var favorites = vm.Favorites;
            var owner = favorites.OwnerKey;
            var conflicts = favorites.Collection.Conflicts;
            var options = FavoriteOptions(conflicts.Select(edit => edit.Value).ToArray(), value => value.Label ?? value.Id);
            var selected = Array.IndexOf(options, await UiDialogs.ActionSheetAsync("Favorites sync conflicts", "Cancel", null, options));
            if (selected < 0 || owner != favorites.OwnerKey)
                return;
            var edit = conflicts[selected];
            var decision = await UiDialogs.ActionSheetAsync(
                edit.Deleted ? "Another device changed this Favorite before it could be removed."
                    : "Another device changed or removed this Favorite.", "Cancel", null, "Keep cloud version", "Apply my change");
            if (decision is "Keep cloud version" or "Apply my change")
                favorites.Resolve(owner, edit.Value.Id, decision == "Apply my change");
        });
    }

    private static string[] FavoriteOptions(FavoriteValue[] values, Func<FavoriteValue, string> text, string? reserved = null)
    {
        var labels = values.Select(text).ToArray();
        var counts = labels.ToLookup(label => label, StringComparer.CurrentCultureIgnoreCase);
        return labels.Select((label, index) => counts[label].Count() == 1 &&
            !string.Equals(label, "Cancel", StringComparison.CurrentCultureIgnoreCase) &&
            !string.Equals(label, reserved, StringComparison.CurrentCultureIgnoreCase)
            ? label : $"{label} ({values[index].Id[^6..]})").ToArray();
    }

    private static async Task RunFavoritesActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            AppLog.Add(() => $"A Favorites action could not be completed ({ex.GetType().Name}).");
            await UiDialogs.AlertAsync("Favorites", "The change could not be saved. Try again.", "Close");
        }
    }
}
