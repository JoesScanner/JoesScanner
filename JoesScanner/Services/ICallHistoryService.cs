using JoesScanner.Models;

namespace JoesScanner.Services
{
    public interface ICallHistoryService
    {
        Task<IReadOnlyList<CallItem>> GetLatestCallsAsync(int count, CancellationToken cancellationToken = default);
    }
}
