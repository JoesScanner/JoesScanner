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

        // Heartbeats keep a session active on the server. Drive these from monitoring state,
        // not from app process lifetime, so background playback can work without creating
        // multi-hour "phantom" sessions.
        void StartMonitoringHeartbeat(string streamServerUrl);
        void StopMonitoringHeartbeat(string reason);


        Task ResetSessionAsync(string reason, CancellationToken cancellationToken);

        Task AdoptSessionTokenAsync(string newSessionToken, string reason, CancellationToken cancellationToken);

        Task TryFlushQueueAsync(CancellationToken cancellationToken);
    }
}
