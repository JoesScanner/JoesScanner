internal static class FileSystem
{
    internal static string CacheDirectory { get; set; } = "";
}

namespace Microsoft.Maui.ApplicationModel { }

namespace Microsoft.Maui.ApplicationModel.DataTransfer
{
    internal sealed record ShareFile(string FullPath);

    internal sealed class ShareFileRequest
    {
        public string? Title { get; init; }
        public ShareFile? File { get; init; }
    }

    internal sealed class Share
    {
        internal static Share Default { get; } = new();
        internal List<string> Shared { get; } = [];

        public Task RequestAsync(ShareFileRequest request)
        {
            Shared.Add(request.File!.FullPath);
            return Task.CompletedTask;
        }
    }
}

namespace JoesScanner.Services
{
    internal static class UiDialogs
    {
        internal static List<string> Alerts { get; } = [];

        internal static Task AlertAsync(string title, string message, string button)
        {
            Alerts.Add(message);
            return Task.CompletedTask;
        }
    }

    public interface IHistoryLookupsRepository
    {
        Task<(HistoryLookupData? Data, DateTime? FetchedUtc)> GetAsync(string serverKey, CancellationToken token = default);
        Task UpsertAsync(string serverKey, HistoryLookupData data, DateTime fetchedUtc, CancellationToken token = default);
    }
}
