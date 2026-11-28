using JoesScanner.Models;

namespace JoesScanner.Services;

// Service that provides a continuous stream of CallItem objects from the backend.
public interface ICallStreamService : IDisposable
{
    ICallStreamService ForRecorder(string endpoint, Guid? serverId = null);

    // Starts streaming calls from the server until the cancellation token is triggered.
    // The caller consumes the IAsyncEnumerable to receive new CallItem instances as they arrive.
    IAsyncEnumerable<CallItem> GetCallStreamAsync(IReadOnlyList<CallItem> existingCalls, CancellationToken cancellationToken,
        LiveStreamRecovery? recovery = null, bool includeOrderingUpdates = false);
    // Populate the shared update state for existing cards that may have missed notifications.
    Task RefreshCallMetadataAsync(IReadOnlyList<CallItem> calls, CancellationToken cancellationToken);
    Task<string?> RefreshRecordingAudioAsync(CallItem item, string server, DateTimeOffset? recordedAt, CancellationToken cancellationToken);
}
