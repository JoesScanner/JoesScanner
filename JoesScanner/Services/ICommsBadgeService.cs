namespace JoesScanner.Services
{
    public interface ICommsBadgeService
    {
        bool HasUnread { get; }

        event Action? Changed;

        void Start();

        void Stop();

        void MarkSeenUpTo(long messageId);

        void MarkAllKnownAsSeen();

        void UpdateLastKnown(long messageId);
    }
}
