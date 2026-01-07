namespace JoesScanner.Services
{
    public sealed class SubscriptionCheckResult
    {
        public bool IsAllowed { get; }
        public string? ErrorCode { get; }
        public string? Message { get; }

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
    }
}
