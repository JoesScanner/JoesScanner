namespace JoesScanner.Services;

internal static class ForegroundRecoveryEntitlement
{
    internal static async Task<RecordingPlaybackAccess.HistoryGrant?> TryVerifyAsync(ISettingsService settings,
        ISubscriptionService subscription, string server, TimeProvider clock, CancellationToken token,
        RecordingPlaybackAccess.HistoryGrant? previous = null, bool independent = false)
    {
        // Polling runs every two seconds. Reuse a recent grant while the
        // shared policy still authorizes its account, tier and deadline.
        if (previous?.Server == server && clock.GetUtcNow() - previous.VerifiedAt < TimeSpan.FromSeconds(15))
        {
            try
            {
                previous.Validate(settings);
                return previous;
            }
            catch (RecordingAccessException)
            {
                // The account changed, or a newer check revoked this grant.
            }
        }

        try
        {
            return await RecordingPlaybackAccess.VerifyHistoryAsync(settings, subscription, server, token, clock, independent: independent)
                .ConfigureAwait(false);
        }
        catch (RecordingAccessException)
        {
            return null;
        }
    }
}
