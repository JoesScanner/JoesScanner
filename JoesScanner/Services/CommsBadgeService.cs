namespace JoesScanner.Services
{
    public sealed class CommsBadgeService : ICommsBadgeService
    {
        private readonly ICommunicationsSyncCoordinator _coordinator;

        private long _lastSeenId;
        private long _lastKnownId;
        private bool _hasUnread;

        public event Action? Changed;

        public bool HasUnread => _hasUnread;

        public CommsBadgeService(ICommunicationsSyncCoordinator coordinator)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

            _lastSeenId = AppStateStore.GetLong("comms_last_seen_id", 0L);
            _lastKnownId = AppStateStore.GetLong("comms_last_known_id", 0L);

            _coordinator.Changed += OnCoordinatorChanged;
            SyncLastKnownFromCoordinator();
            RecomputeHasUnread(raiseEvent: false);
        }

        public void Start()
        {
            SyncLastKnownFromCoordinator();
            _coordinator.Start();
        }

        public void Stop()
        {
            _coordinator.Stop();
        }

        public void MarkSeenUpTo(long messageId)
        {
            if (messageId <= 0)
                return;

            if (messageId > _lastSeenId)
            {
                _lastSeenId = messageId;
                AppStateStore.SetLong("comms_last_seen_id", _lastSeenId);
            }

            if (messageId > _lastKnownId)
            {
                _lastKnownId = messageId;
                AppStateStore.SetLong("comms_last_known_id", _lastKnownId);
            }

            RecomputeHasUnread(raiseEvent: true);
        }

        public void MarkAllKnownAsSeen()
        {
            SyncLastKnownFromCoordinator();

            if (_lastKnownId <= 0)
                return;

            MarkSeenUpTo(_lastKnownId);
        }

        public void UpdateLastKnown(long messageId)
        {
            if (messageId <= 0)
                return;

            if (messageId <= _lastKnownId)
                return;

            _lastKnownId = messageId;
            AppStateStore.SetLong("comms_last_known_id", _lastKnownId);
            RecomputeHasUnread(raiseEvent: true);
        }

        private void OnCoordinatorChanged()
        {
            SyncLastKnownFromCoordinator();
        }

        private void SyncLastKnownFromCoordinator()
        {
            try
            {
                UpdateLastKnown(_coordinator.LastKnownId);
            }
            catch
            {
            }
        }

        private void RecomputeHasUnread(bool raiseEvent)
        {
            var newHasUnread = _lastKnownId > _lastSeenId;
            if (newHasUnread == _hasUnread)
                return;

            _hasUnread = newHasUnread;

            if (!raiseEvent)
                return;

            try { Changed?.Invoke(); } catch { }
        }
    }
}
