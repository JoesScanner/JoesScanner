using JoesScanner.Models;

namespace JoesScanner.Services
{
    public interface ICommunicationsService
    {
        Task<CommsSyncResult> SyncAsync(
            string authServerBaseUrl,
            string sessionToken,
            long sinceSeq,
            bool forceSnapshot,
            CancellationToken cancellationToken = default);
    }

    public sealed class CommsSyncResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = string.Empty;

        // The server's latest change sequence number at the time of this response.
        public long NextSeq { get; init; }

        // When present, this is a full list of current messages (excluding deleted).
        public List<CommsMessage> Snapshot { get; init; } = new();

        // Lightweight changes since the requested watermark.
        public List<CommsChange> Changes { get; init; } = new();

        public bool HasSnapshot => Snapshot.Count > 0;
    }

    public sealed class CommsChange
    {
        // "upsert" or "delete"
        public string Type { get; init; } = string.Empty;

        public long MessageId { get; init; }

        // Present only for upserts.
        public CommsMessage? Message { get; init; }
    }
}
