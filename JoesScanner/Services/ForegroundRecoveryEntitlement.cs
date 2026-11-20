namespace JoesScanner.Services;

internal static class ForegroundRecoveryEntitlement
{
    internal static async Task<RecordingPlaybackAccess.HistoryGrant?> TryVerifyAsync(ISettingsService settings,
        ISubscriptionService subscription, string server, TimeProvider clock, CancellationToken token,
        RecordingPlaybackAccess.HistoryGrant? previous = null)
    {
        // Polling runs every two seconds. Reuse a recent successful check while
        // its account, tier, and expiry remain current. Never reuse an offline result.
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
            return await RecordingPlaybackAccess.VerifyHistoryAsync(settings, subscription, server, token, clock)
                .ConfigureAwait(false);
        }
        catch (RecordingAccessException)
        {
            return null;
        }
    }
}
