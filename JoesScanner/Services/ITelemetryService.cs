using System.Threading;
using System.Threading.Tasks;

namespace JoesScanner.Services
{
    public interface ITelemetryService
    {
        void TrackAppStarted();
        void TrackAppStopping();

        void TrackConnectionAttempt(string streamServerUrl, bool isHostedServer);
        void TrackConnectionStatusChanged(string status, string? detailMessage, string streamServerUrl);

        Task ResetSessionAsync(string reason, CancellationToken cancellationToken);

        Task AdoptSessionTokenAsync(string newSessionToken, string reason, CancellationToken cancellationToken);

        Task TryFlushQueueAsync(CancellationToken cancellationToken);
    }
}
