using JoesScanner.Models;

namespace JoesScanner.Services
{
    public interface ICommunicationsSyncCoordinator
    {
        IReadOnlyList<CommsMessage> Messages { get; }

        string StatusText { get; }

        long LastKnownId { get; }

        event Action? Changed;

        void Start();

        void Stop();

        void SetPageActive(bool isActive);

        Task EnsurePreloadedAsync(CancellationToken cancellationToken = default);

        Task ForceRefreshAsync(CancellationToken cancellationToken = default);
    }
}
