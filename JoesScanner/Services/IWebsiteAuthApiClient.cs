using System.Net;

namespace JoesScanner.Services
{
    public interface IWebsiteAuthApiClient
    {
        Task<WebsiteAuthApiResult> AuthenticateAsync(WebsiteAuthApiRequest request, CancellationToken cancellationToken);
    }

    public sealed record WebsiteAuthApiRequest(
        Uri Endpoint,
        string Username,
        string Password,
        string DevicePlatform,
        string DeviceType,
        string DeviceModel,
        string AppVersion,
        string AppBuild,
        string OsVersion,
        string DeviceId,
        string SessionToken);

    public sealed record WebsiteAuthApiResult(
        WebsiteAuthApiResultKind Kind,
        HttpStatusCode? HttpStatus,
        string Message,
        WebsiteAuthResponseDto? Response,
        string? RawBody);

    public enum WebsiteAuthApiResultKind
    {
        Success,
        HttpError,
        ApiDenied,
        InvalidResponse,
        Timeout,
        Unreachable,
        Canceled
    }

    public sealed class WebsiteAuthResponseDto
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Message { get; set; }
        public string? SessionToken { get; set; }
        public WebsiteAuthSubscriptionDto? Subscription { get; set; }
    }

    public sealed class WebsiteAuthSubscriptionDto
    {
        public bool Active { get; set; }
        public string? Status { get; set; }
        public string? Level { get; set; }
        public string? LevelLabel { get; set; }
        public string? PriceId { get; set; }
        public string? PeriodEndAt { get; set; }
        public string? TrialEndsAt { get; set; }
        public string? ExpiresAt { get; set; }
    }
}
