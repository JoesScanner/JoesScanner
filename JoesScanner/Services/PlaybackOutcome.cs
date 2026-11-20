namespace JoesScanner.Services;

internal enum PlaybackOutcome
{
    Completed,
    Skipped,
    Canceled,
    Failed,
    Withheld
}

internal static class PlaybackAttempt
{
    internal static bool AdvancesQueue(this PlaybackOutcome outcome) =>
        outcome is PlaybackOutcome.Completed or PlaybackOutcome.Skipped;

    internal static async Task<PlaybackOutcome> RunAsync(Func<Task> play, CancellationToken token,
        Action<Exception> reportFailure, PlaybackCompletion? completion = null)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await play();
            if (completion?.Confirmed == true)
                return PlaybackOutcome.Completed;
            if (token.IsCancellationRequested)
                return PlaybackOutcome.Canceled;
            if (completion != null)
                throw new InvalidOperationException("The native player did not confirm the end of the recording.");
            return PlaybackOutcome.Completed;
        }
        catch (OperationCanceledException ex) when (LivePlaybackRequests.IsExpectedCancellation(ex, token))
        {
            return completion?.Confirmed == true ? PlaybackOutcome.Completed : PlaybackOutcome.Canceled;
        }
        catch (Exception ex)
        {
            reportFailure(ex);
            return PlaybackOutcome.Failed;
        }
    }
}
