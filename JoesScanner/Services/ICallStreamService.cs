using JoesScanner.Models;

namespace JoesScanner.Services;

// Service that provides a continuous stream of CallItem objects from the backend.
public interface ICallStreamService
{
    // Starts streaming calls from the server until the cancellation token is triggered.
    // The caller consumes the IAsyncEnumerable to receive new CallItem instances as they arrive.
    IAsyncEnumerable<CallItem> GetCallStreamAsync(CancellationToken cancellationToken);
}
