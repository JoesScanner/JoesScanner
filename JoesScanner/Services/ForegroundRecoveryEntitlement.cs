using JoesScanner.Helpers;

namespace JoesScanner.Services;

internal static class ForegroundRecoveryEntitlement
{
    internal static async Task<DateTimeOffset?> GetCutoffAsync(ISettingsService settings,
        ISubscriptionService subscription, string serverUrl, DateTimeOffset since, TimeProvider clock,
        CancellationToken token)
    {
        if (!HostedServerRules.RequiresApiFirewallCredentials(settings, serverUrl))
            return since;

        var result = await subscription.EnsureSubscriptionAsync(token).ConfigureAwait(false);
        if (!result.IsAllowed || result.VerifiedOnlineTier is not > 0)
            return null;

        var minimum = clock.GetUtcNow().AddHours(-24);
        return result.VerifiedOnlineTier == 1 && since < minimum ? minimum : since;
    }
}
