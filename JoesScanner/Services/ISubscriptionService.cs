namespace JoesScanner.Services
{
    public sealed class SubscriptionCheckResult
    {
        public bool IsAllowed { get; }
        public string? ErrorCode { get; }
        public string? Message { get; }
        public int? VerifiedOnlineTier { get; init; }
        public int? EffectiveTier { get; init; }
        internal bool RequiresIntervention => ErrorCode is "missing_credentials" or "credential_storage_unavailable" or
            "expired" or "invalid_authorization" or "http_401" or "http_403";

        public SubscriptionCheckResult(bool isAllowed, string? errorCode = null, string? message = null)
        {
            IsAllowed = isAllowed;
            ErrorCode = errorCode;
            Message = message;
        }
    }

    public interface ISubscriptionService
    {
        Task<SubscriptionCheckResult> EnsureSubscriptionAsync(CancellationToken cancellationToken);
        Task<SubscriptionCheckResult> EnsureSubscriptionForRecorderAsync(string server, CancellationToken cancellationToken) =>
            EnsureSubscriptionAsync(cancellationToken);

        Task MaintainHostedSubscriptionAsync(string server, CancellationToken token) => Task.CompletedTask;
    }
}
