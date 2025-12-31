// Services/IJoesScannerApiClient.cs
using System.Threading;
using System.Threading.Tasks;

namespace JoesScanner.Services
{
    public interface IJoesScannerApiClient
    {
        Task<ApiPingResult> PingAsync(CancellationToken cancellationToken = default);

        Task<ApiAuthResult> AuthenticateAsync(
            string serverUrl,
            string username,
            string password,
            string deviceId,
            CancellationToken cancellationToken = default);
    }

    public sealed class ApiPingResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class ApiAuthResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = string.Empty;
        public string SessionToken { get; init; } = string.Empty;
        public string PlanLabel { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
    }
}
