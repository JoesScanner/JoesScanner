using JoesScanner.Models;

namespace JoesScanner.Services;

// Service that provides a continuous stream of CallItem objects from the backend.
public interface ICallStreamService
{
    // Starts streaming calls from the server until the cancellation token is triggered.
    // The caller consumes the IAsyncEnumerable to receive new CallItem instances as they arrive.
    IAsyncEnumerable<CallItem> GetCallStreamAsync(CancellationToken cancellationToken);

    // Best-effort helper used to catch up on transcription updates that may have been missed
    // while the app was backgrounded or the main UI was not visible.
    // Returns null when the call cannot be found or the server has no transcription yet.
    Task<string?> TryFetchTranscriptionByIdAsync(string backendId, CancellationToken cancellationToken);
}
