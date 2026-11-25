using JoesScanner.Helpers;
using JoesScanner.Services;
using Microsoft.Maui.ApplicationModel;

namespace JoesScanner.ViewModels;

public partial class SettingsViewModel
{
    private readonly ServerDirectoryClient _serverDirectory;
    private bool _sharingScanner;
    public bool CanRefreshDirectory => _serverDirectory.CanShare && !IsDirectoryLoading;

    private void OnDirectoryPrivacyChanged()
    {
        _lastDirectoryRefreshUtc = DateTime.MinValue;
        OnPropertyChanged(nameof(CanRefreshDirectory));
        DirectoryStatusText = _serverDirectory.CanShare
            ? "Refresh to check the public directory."
            : "Directory access is paused in Local Only Mode.";
    }

    private async Task<bool> RefreshDirectoryServersAsync()
    {
        if (!_serverDirectory.CanShare || Interlocked.Exchange(ref _directoryRefreshInProgress, 1) == 1)
            return false;

        try
        {
            IsDirectoryLoading = true;
            OnPropertyChanged(nameof(CanRefreshDirectory));
            DirectoryStatusText = "Loading servers…";
            var operation = _serverDirectory.Begin();
            var snapshot = await _serverDirectory.FetchAsync(operation);
            return operation.TryApply(() =>
            {
                _mainViewModel.ApplyScannerDirectory(snapshot);
                PublishDirectoryServers(snapshot.Entries);
                _lastDirectoryRefreshUtc = DateTime.UtcNow;
            });
        }
        catch (OperationCanceledException) when (!_serverDirectory.CanShare)
        {
            return false;
        }
        catch (Exception ex)
        {
            DirectoryStatusText = "The directory could not be refreshed. Using the saved list.";
            AppLog.Add(() => $"Settings: directory refresh failed ({ex.GetType().Name}).");
            return false;
        }
        finally
        {
            IsDirectoryLoading = false;
            Interlocked.Exchange(ref _directoryRefreshInProgress, 0);
            OnPropertyChanged(nameof(CanRefreshDirectory));
        }
    }

    internal async Task SaveLocalScannerAsync()
    {
        if (!ScannerEditorOpen || !CanEditScannerAddress)
            return;
        if (!SavedConnectionUrl.TryParse(ServerUrl, out _, out var endpoint, out var embedded) ||
            HostedServerRules.IsProvidedDefaultServerUrl(endpoint))
        {
            await UiDialogs.AlertAsync("Save server", "Enter an independent recorder's HTTP or HTTPS address.", "Close");
            return;
        }
        if (_mainViewModel.ScannerConnectionEditError(_editingScannerConnection, endpoint) is { } error)
        {
            await UiDialogs.AlertAsync("Save server", error, "Close");
            return;
        }

        SavedScannerConnection? added;
        try
        {
            _connectionValidations.Begin();
            var username = embedded?.Username ?? BasicAuthUsername.Trim();
            var password = embedded?.Password ?? BasicAuthPassword;
            added = _mainViewModel.SaveScannerConnection(endpoint, ScannerName, null, _editingScannerConnection?.Id,
                (username, password));
            RecordSavedScannerSelection();
            CloseScannerEditor();
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Could not save a local recorder ({ex.GetType().Name}).");
            await UiDialogs.AlertAsync("Save server", "The local recorder could not be saved. Check the address and try again.", "Close");
            return;
        }
        if (added != null)
            await OfferDirectorySharingAsync(added, newlySaved: true);
    }

    internal async Task OfferDirectorySharingAsync(SavedScannerConnection saved, bool newlySaved)
    {
        if (_sharingScanner || saved.Managed || !_serverDirectory.CanShare || !_mainViewModel.IsSavedRecorder(saved))
            return;
        _sharingScanner = true;
        try
        {
            const string publicChoice = "Share Publicly";
            const string privateChoice = "Private / Restricted";
            const string manage = "Manage Listings";
            var choice = await UiDialogs.ActionSheetAsync(
                newlySaved ? $"Saved {saved.Name}. Share this recorder?" : $"Share or manage {saved.Name}",
                newlySaved ? "Keep Local" : "Cancel", null,
                newlySaved ? [publicChoice, privateChoice] : [publicChoice, privateChoice, manage]);
            if (choice is not (publicChoice or privateChoice or manage) || !_serverDirectory.CanShare ||
                !_mainViewModel.IsSavedRecorder(saved))
                return;

            var account = _serverDirectory.Begin();
            if (choice == manage)
            {
                await OpenMyServersAsync(account, saved, null);
                return;
            }
            if (choice == privateChoice)
            {
                var visibility = await UiDialogs.ActionSheetAsync("Choose website visibility", "Cancel", null,
                    "Private", "Restricted");
                if (visibility is "Private" or "Restricted")
                    await OpenMyServersAsync(account, saved, visibility.ToLowerInvariant());
                return;
            }
            if (!_serverDirectory.HasAccount)
            {
                if (await UiDialogs.ConfirmAsync("Verify your account", "Continue on the website to verify your email and submit the listing.",
                    "Continue on Website", "Keep Local"))
                    await OpenMyServersAsync(account, saved, "public");
                return;
            }
            await SubmitDirectoryListingAsync(account, saved);
        }
        catch (OperationCanceledException)
        {
            // An account or privacy change retires the pending sharing action.
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Recorder sharing could not complete ({ex.GetType().Name}).");
            if (_serverDirectory.CanShare)
                await UiDialogs.AlertAsync("Server directory",
                    "The online request could not be verified. Your recorder is saved on this device.", "Close");
        }
        finally { _sharingScanner = false; }
    }

    private async Task OpenMyServersAsync(DirectoryRequest account, SavedScannerConnection saved, string? visibility)
    {
        if (!_mainViewModel.IsSavedRecorder(saved) || !_serverDirectory.CanShare)
            return;
        var site = await _serverDirectory.GetSiteAsync(account);
        if (site.MyServers is not { } page)
        {
            await UiDialogs.AlertAsync("My Servers", "The account website has not configured its My Servers page.", "Close");
            return;
        }
        var destination = visibility == null ? page :
            ServerDirectoryClient.BuildPrefill(account.Session.Endpoint("servers"), page, saved.Endpoint, visibility);
        Task<bool>? launch = null;
        if (_mainViewModel.IsSavedRecorder(saved))
            account.TryApply(() => launch = Launcher.Default.OpenAsync(destination));
        if (launch != null && !await launch)
            throw new InvalidOperationException("The website could not be opened.");
    }

    private async Task SubmitDirectoryListingAsync(DirectoryRequest account, SavedScannerConnection saved)
    {
        var address = ServerDirectoryClient.ShareableAddress(saved.Endpoint);
        var anchor = await UiDialogs.PromptAsync("Map anchor", "Enter the city and region that this recorder covers.",
            "Continue", "Cancel", maxLength: 120);
        if (string.IsNullOrWhiteSpace(anchor) || !_serverDirectory.CanShare)
            return;
        const string login = "Scanner login";
        const string firewall = "Firewall authentication";
        const string both = "Both";
        var auth = await UiDialogs.ActionSheetAsync("What does this recorder require?", "Cancel", null,
            "Neither", login, firewall, both);
        if (auth is not ("Neither" or login or firewall or both) || !_serverDirectory.CanShare)
            return;

        var site = await _serverDirectory.GetSiteAsync(account);
        var confirmation = $"{saved.Name}\n{address}\n{anchor.Trim()}\n\n{site.Terms}";
        if (!await UiDialogs.ConfirmAsync("Public listing terms", confirmation, "Agree and Submit", "Cancel") ||
            !_mainViewModel.IsSavedRecorder(saved) || !_serverDirectory.CanShare)
            return;

        var response = await _serverDirectory.SubmitAsync(account, site,
            new(saved.Name, address, anchor, auth is login or both, auth is firewall or both));
        if (!account.Session.IsCurrentAccount || account.PrivacyGeneration.IsCancellationRequested || account.AccountGeneration.IsCancellationRequested)
            return;
        var message = response switch
        {
            DirectorySubmissionStatus.Pending => "Pending Approval. An administrator will review your listing.",
            DirectorySubmissionStatus.SignInRequired => "Your account could not be verified. Sign in again or use My Servers on the website.",
            DirectorySubmissionStatus.Conflict => "That address already has a listing or is unavailable for submission. Review it in My Servers.",
            DirectorySubmissionStatus.RateLimited => "Too many attempts. Try again later.",
            _ => "The directory did not accept the listing. Check the address and listing details."
        };
        await UiDialogs.AlertAsync("Server directory", message, "Close");
    }

    internal async Task ReviewDirectoryUpdateAsync(ScannerConnectionRow row)
    {
        if (!row.HasDirectoryUpdate || !_serverDirectory.CanShare)
            return;
        try
        {
            var account = _serverDirectory.Begin();
            var source = ServerDirectoryClient.Source(account.Session);
            if (!await RefreshDirectoryServersAsync() || row.Profile.DirectorySource != source ||
                row.Profile.DirectoryCandidate is not { } endpoint)
            {
                await UiDialogs.AlertAsync("Directory update", "A current, approved listing could not be verified.", "Close");
                return;
            }
            var changing = endpoint != row.Endpoint;
            var title = changing ? "Confirm directory address" : "Restore directory connection";
            var details = changing
                ? $"The directory approved {endpoint}. Enter a recorder login for the new origin if required."
                : $"The directory published {row.Name} again. Restore this recorder's monitoring eligibility?";
            if (!await UiDialogs.ConfirmAsync(title, details, "Confirm", "Cancel"))
                return;
            var applied = false;
            account.TryApply(() => applied = _mainViewModel.ConfirmScannerDirectory(row.Id, endpoint, source));
            if (!applied)
                await UiDialogs.AlertAsync("Directory update", "The listing changed. Refresh and review it again.", "Close");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Recorder directory update could not be applied ({ex.GetType().Name}).");
            if (_serverDirectory.CanShare)
                await UiDialogs.AlertAsync("Directory update", "The new address could not be saved. Check for another recorder at that address.", "Close");
        }
    }
}
