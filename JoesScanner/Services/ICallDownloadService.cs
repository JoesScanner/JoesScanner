using JoesScanner.Models;

namespace JoesScanner.Services
{
    public interface ICallDownloadService
    {
        Task DownloadSingleAsync(CallItem call, CancellationToken cancellationToken = default);
        Task DownloadRangeZipAsync(IReadOnlyList<CallItem> calls, CancellationToken cancellationToken = default);
    }
}
