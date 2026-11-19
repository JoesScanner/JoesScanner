namespace JoesScanner.Services;

internal enum PlaybackOutcome
{
    Completed,
    Skipped,
    Canceled,
    Failed
}

internal static class PlaybackAttempt
{
    internal static bool AdvancesQueue(this PlaybackOutcome outcome) =>
        outcome is PlaybackOutcome.Completed or PlaybackOutcome.Skipped;

    internal static async Task<PlaybackOutcome> RunAsync(Func<Task> play, CancellationToken token,
        Action<Exception> reportFailure)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await play();
            return token.IsCancellationRequested ? PlaybackOutcome.Canceled : PlaybackOutcome.Completed;
        }
        catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, token))
        {
            return PlaybackOutcome.Canceled;
        }
        catch (Exception ex)
        {
            reportFailure(ex);
            return PlaybackOutcome.Failed;
        }
    }
}
