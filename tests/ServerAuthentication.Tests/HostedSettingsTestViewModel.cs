using JoesScanner.Helpers;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

// The production hosted Validate handler is linked into this partial. The test
// supplies the page's input snapshot, account transition, and firewall probe.
public partial class SettingsViewModel
{
    private const string DefaultServerUrl = HostedServerRules.DefaultServerUrl;
    private readonly ISettingsService _settings;
    private readonly SubscriptionService _subscriptionService;
    private readonly MainViewModel _mainViewModel = new();
    private readonly ConnectionValidationRequests _connectionValidations = new();
    private int _suppressDirectorySelectionSync;

    internal SettingsViewModel(ISettingsService settings, SubscriptionService subscriptionService)
    {
        _settings = settings;
        _subscriptionService = subscriptionService;
        AuthServerBaseUrl = settings.AuthServerBaseUrl;
        ServerUrl = settings.ServerUrl;
        BasicAuthUsername = settings.BasicAuthUsername;
        BasicAuthPassword = settings.BasicAuthPassword;
    }

    internal string AuthServerBaseUrl { get; set; }
    internal string ServerUrl { get; set; }
    internal string BasicAuthUsername { get; set; }
    internal string BasicAuthPassword { get; set; }
    internal string ServerValidationMessage { get; private set; } = "";
    internal bool ServerValidationIsError { get; private set; }
    internal bool UseDefaultConnection { get; private set; }
    internal bool ShowValidationPrefix { get; private set; }
    internal int FirewallProbes { get; private set; }
    internal int SavedSelections { get; private set; }
    internal Func<Task>? BeforeAccountTransition { set => _mainViewModel.BeforeAccountTransition = value; }

    internal Task<bool> PressValidateAsync()
    {
        var revision = _connectionValidations.Begin();
        var inputs = (ServerUrl, AuthServerBaseUrl, BasicAuthUsername, BasicAuthPassword);
        bool OwnsInputs() => _connectionValidations.IsCurrent(revision) &&
            inputs == (ServerUrl, AuthServerBaseUrl, BasicAuthUsername, BasicAuthPassword);

        return ValidateHostedAccountAsync(OwnsInputs,
            (session, apply) => _connectionValidations.TryApply(revision, session, OwnsInputs, apply), () =>
            {
                FirewallProbes++;
                return Task.CompletedTask;
            });
    }

    private void RecordSavedScannerSelection() => SavedSelections++;
    private void SetShowValidationPrefix(bool value) => ShowValidationPrefix = value;
    private void UpdateSubscriptionSummaryFromSettings() { }

    private sealed class MainViewModel
    {
        internal string ServerUrl { get; set; } = "";
        internal Func<Task>? BeforeAccountTransition { get; set; }

        internal async Task ApplyHostedAccountAsync(string username, bool authorityChanged, Func<bool> canApply, Action save)
        {
            if (BeforeAccountTransition is { } wait)
                await wait();
            if (canApply())
                save();
        }
    }
}
