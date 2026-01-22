using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace JoesScanner.Services
{
    public sealed class CommsBadgeService : ICommsBadgeService
    {
        private const string LastSeenIdKey = "CommsLastSeenMessageId";
        private const string LastKnownIdKey = "CommsLastKnownMessageId";

        private readonly ISettingsService _settings;
        private readonly ICommunicationsService _comms;

        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        private long _lastSeq;
        private int _pollTick;

        private long _lastSeenId;
        private long _lastKnownId;
        private bool _hasUnread;

        // Poll every 15 seconds, and force a snapshot about every 30 minutes.
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
        private const int SnapshotEveryPolls = 120;

        public event Action? Changed;

        public bool HasUnread => _hasUnread;

        public CommsBadgeService(ISettingsService settings, ICommunicationsService comms)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _comms = comms ?? throw new ArgumentNullException(nameof(comms));

            _lastSeenId = Preferences.Get(LastSeenIdKey, 0L);
            _lastKnownId = Preferences.Get(LastKnownIdKey, 0L);

            RecomputeHasUnread(raiseEvent: false);
        }

        public void Start()
        {
            if (_loopTask != null && !_loopTask.IsCompleted)
                return;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            _lastSeq = 0;
            _pollTick = 0;

            _loopTask = Task.Run(() => PollLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
        }

        public void MarkSeenUpTo(long messageId)
        {
            if (messageId <= 0)
                return;

            if (messageId > _lastSeenId)
            {
                _lastSeenId = messageId;
                Preferences.Set(LastSeenIdKey, _lastSeenId);
            }

            if (messageId > _lastKnownId)
            {
                _lastKnownId = messageId;
                Preferences.Set(LastKnownIdKey, _lastKnownId);
            }

            RecomputeHasUnread(raiseEvent: true);
        }

        public void UpdateLastKnown(long messageId)
        {
            if (messageId <= 0)
                return;

            if (messageId <= _lastKnownId)
                return;

            _lastKnownId = messageId;
            Preferences.Set(LastKnownIdKey, _lastKnownId);
            RecomputeHasUnread(raiseEvent: true);
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

        private async Task PollLoopAsync(CancellationToken token)
        {
            // Poll immediately so the badge can update without waiting a full interval.
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    // Best effort only. Connection and retry behavior is handled elsewhere.
                }

                try
                {
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private async Task PollOnceAsync(CancellationToken token)
        {
            _pollTick++;
            var forceSnapshot = (_pollTick % SnapshotEveryPolls) == 0 || _lastSeq == 0;

            var serverUrl = (_settings.AuthServerBaseUrl ?? string.Empty).Trim();
            var sessionToken = (_settings.AuthSessionToken ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(sessionToken))
                return;

            var result = await _comms.SyncAsync(
                serverUrl,
                sessionToken,
                _lastSeq,
                forceSnapshot,
                token).ConfigureAwait(false);

            if (!result.Ok)
                return;

            long maxId = 0;

            if (result.HasSnapshot && result.Snapshot.Count > 0)
            {
                maxId = result.Snapshot.Max(m => m.Id);
            }
            else if (result.Changes.Count > 0)
            {
                foreach (var ch in result.Changes)
                {
                    if (!string.Equals(ch.Type, "upsert", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Some servers only send MessageId for incremental upserts.
                    var id = ch.Message?.Id ?? ch.MessageId;
                    if (id > maxId)
                        maxId = id;
                }
            }

            if (maxId > 0)
                UpdateLastKnown(maxId);

            if (result.NextSeq > _lastSeq)
                _lastSeq = result.NextSeq;
        }
    }
}
