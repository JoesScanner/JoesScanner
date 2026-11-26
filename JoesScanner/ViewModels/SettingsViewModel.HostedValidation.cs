using JoesScanner.Helpers;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class SettingsViewModel
{
    internal async Task<bool> ValidateHostedAccountAsync(Func<bool> ownsInputs,
        Func<WordPressSession, Action, bool> publish, Func<Task> verifyFirewall)
    {
        var username = BasicAuthUsername.Trim();
        var password = TextNormalizationHelper.NormalizeSmartQuotes(BasicAuthPassword);
        var accountBase = AuthServerBaseUrl;
        var account = WordPressSession.Capture(_settings);

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            var matchesEndpoint = account.Endpoint("auth") == SavedConnectionUrl.AccountEndpoint(accountBase);
            var hasSavedAccount = matchesEndpoint && _settings.TryGetServerCredentials(DefaultServerUrl, out _, out _);
            if (username.Length == 0 && password.Length == 0 && hasSavedAccount &&
                HostedServerRules.IsHostedStreamServerUrl(_settings.ServerUrl))
            {
                await _mainViewModel.ApplyHostedAccountAsync(string.Empty, false,
                    () => ownsInputs() && account.IsCurrentAccount,
                    () => publish(account, () => _settings.ClearServerCredentials(DefaultServerUrl)));
                if (!ownsInputs())
                    return false;
            }
            var notice = !hasSavedAccount && matchesEndpoint ? _settings.SavedCredentialWarning : null;
            SetShowValidationPrefix(false);
            return FailHostedValidation(notice ?? "Enter your Joe's Scanner username and password, then tap Validate.");
        }

        var (result, verifiedAccount) = await _subscriptionService.ValidateFromSettingsAsync(
            account, accountBase, username, password, ownsInputs, _mainViewModel.ApplyHostedAccountAsync);
        if (!ownsInputs() || result.ErrorCode == "account_changed")
            return false;
        if (result.VerifiedOnlineTier == null)
        {
            var message = result.ErrorCode switch
            {
                { } http when http.StartsWith("http_", StringComparison.Ordinal) =>
                    $"Auth server responded with HTTP {http[5..]}.",
                "timeout" => "Server did not respond in time (validation timed out).",
                _ => result.Message ?? "Account validation failed."
            };
            return FailHostedValidation(message);
        }
        if (verifiedAccount?.IsCurrentAccount != true)
            return false;

        _mainViewModel.ServerUrl = DefaultServerUrl;
        ServerValidationMessage = "Joe's Scanner account validated.";
        ServerValidationIsError = false;

        // Refresh the plan and renewal display with the saved account.
        Interlocked.Increment(ref _suppressDirectorySelectionSync);
        try
        {
            UseDefaultConnection = true;
            ServerUrl = DefaultServerUrl;
            BasicAuthUsername = username;
            BasicAuthPassword = password;
        }
        finally
        {
            Interlocked.Decrement(ref _suppressDirectorySelectionSync);
        }

        RecordSavedScannerSelection();
        SetShowValidationPrefix(true);
        UpdateSubscriptionSummaryFromSettings();
        await verifyFirewall();
        return verifiedAccount.IsCurrentAccount;
    }

    private bool FailHostedValidation(string message)
    {
        ServerValidationMessage = message;
        ServerValidationIsError = true;
        UpdateSubscriptionSummaryFromSettings();
        return false;
    }
}
