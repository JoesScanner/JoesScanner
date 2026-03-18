using Microsoft.Maui.Controls;
using JoesScanner.Models;

namespace JoesScanner.Services
{
    public sealed class CommunicationsSyncCoordinator : ICommunicationsSyncCoordinator
    {
        private static readonly TimeSpan BackgroundPollInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromSeconds(10);
        private const int SnapshotEveryPolls = 120;

        private readonly ICommunicationsService _communicationsService;
        private readonly ISettingsService _settings;
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private readonly object _stateLock = new();

        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private int _pageActiveCount;
        private long _lastSeq;
        private int _pollTick;
        private List<CommsMessage> _messages = new();
        private string _statusText = string.Empty;
        private long _lastKnownId;

        public CommunicationsSyncCoordinator(ICommunicationsService communicationsService, ISettingsService settings)
        {
            _communicationsService = communicationsService ?? throw new ArgumentNullException(nameof(communicationsService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _lastKnownId = AppStateStore.GetLong("comms_last_known_id", 0L);
        }

        public IReadOnlyList<CommsMessage> Messages
        {
            get
            {
                lock (_stateLock)
                {
                    return _messages.ToList();
                }
            }
        }

        public string StatusText
        {
            get
            {
                lock (_stateLock)
                {
                    return _statusText;
                }
            }
        }

        public long LastKnownId
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastKnownId;
                }
            }
        }

        public event Action? Changed;

        public void Start()
        {
            if (_loopTask != null && !_loopTask.IsCompleted)
                return;

            _loopCts?.Cancel();
            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => PollLoopAsync(_loopCts.Token));
        }

        public void Stop()
        {
            try { _loopCts?.Cancel(); } catch { }
        }

        public void SetPageActive(bool isActive)
        {
            lock (_stateLock)
            {
                if (isActive)
                    _pageActiveCount++;
                else if (_pageActiveCount > 0)
                    _pageActiveCount--;
            }
        }

        public async Task EnsurePreloadedAsync(CancellationToken cancellationToken = default)
        {
            Start();
            await SyncOnceAsync(forceSnapshot: true, suppressAuthErrors: true, cancellationToken).ConfigureAwait(false);
        }

        public async Task ForceRefreshAsync(CancellationToken cancellationToken = default)
        {
            Start();

            lock (_stateLock)
            {
                _lastSeq = 0;
                _pollTick = 0;
            }

            await SyncOnceAsync(forceSnapshot: true, suppressAuthErrors: false, cancellationToken).ConfigureAwait(false);
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var forceSnapshot = false;

                    lock (_stateLock)
                    {
                        _pollTick++;
                        forceSnapshot = (_pollTick % SnapshotEveryPolls) == 0 || _lastSeq == 0;
                    }

                    await SyncOnceAsync(forceSnapshot, suppressAuthErrors: false, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(GetCurrentPollInterval(), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private TimeSpan GetCurrentPollInterval()
        {
            lock (_stateLock)
            {
                return _pageActiveCount > 0 ? ForegroundPollInterval : BackgroundPollInterval;
            }
        }

        private async Task SyncOnceAsync(bool forceSnapshot, bool suppressAuthErrors, CancellationToken cancellationToken)
        {
            await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var serverUrl = _settings.AuthServerBaseUrl ?? string.Empty;
                var token = _settings.AuthSessionToken ?? string.Empty;
                long sinceSeq;

                lock (_stateLock)
                {
                    sinceSeq = _lastSeq;
                }

                var result = await _communicationsService.SyncAsync(
                    serverUrl,
                    token,
                    sinceSeq,
                    forceSnapshot,
                    cancellationToken).ConfigureAwait(false);

                if (!result.Ok)
                {
                    if (suppressAuthErrors && IsAuthNotReadyMessage(result.Message))
                        return;

                    var errorText = string.IsNullOrWhiteSpace(result.Message) ? "Unable to load messages." : result.Message;
                    if (TryUpdateStatus(errorText))
                        RaiseChanged();
                    return;
                }

                var changed = false;
                var newMessages = Messages.ToList();
                long lastKnownId;

                if (result.HasSnapshot)
                {
                    newMessages = result.Snapshot
                        .OrderByDescending(m => m.CreatedAtUtc)
                        .ToList();

                    changed |= ReplaceMessagesIfNeeded(newMessages);
                    lastKnownId = newMessages.Count > 0 ? newMessages.Max(m => m.Id) : 0;
                }
                else
                {
                    var deletes = result.Changes
                        .Where(c => string.Equals(c.Type, "delete", StringComparison.OrdinalIgnoreCase))
                        .Select(c => c.MessageId)
                        .ToHashSet();

                    if (deletes.Count > 0)
                    {
                        newMessages.RemoveAll(m => deletes.Contains(m.Id));
                        changed = true;
                    }

                    var upserts = result.Changes
                        .Where(c => string.Equals(c.Type, "upsert", StringComparison.OrdinalIgnoreCase) && c.Message != null)
                        .Select(c => c.Message!)
                        .ToList();

                    if (upserts.Count > 0)
                    {
                        foreach (var msg in upserts)
                        {
                            var existingIndex = newMessages.FindIndex(x => x.Id == msg.Id);
                            if (existingIndex >= 0)
                                newMessages[existingIndex] = msg;
                            else
                                newMessages.Add(msg);
                        }

                        newMessages = newMessages
                            .OrderByDescending(m => m.CreatedAtUtc)
                            .ToList();

                        changed = true;
                    }

                    if (changed)
                        changed |= ReplaceMessagesIfNeeded(newMessages);

                    lastKnownId = newMessages.Count > 0 ? newMessages.Max(m => m.Id) : 0;
                }

                lock (_stateLock)
                {
                    if (result.NextSeq > _lastSeq)
                        _lastSeq = result.NextSeq;
                }

                if (TryUpdateLastKnown(lastKnownId))
                    changed = true;

                var newStatusText = newMessages.Count == 0 ? "No messages yet." : string.Empty;
                if (TryUpdateStatus(newStatusText))
                    changed = true;

                if (changed)
                    RaiseChanged();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (TryUpdateStatus(ex.Message))
                    RaiseChanged();
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private bool ReplaceMessagesIfNeeded(List<CommsMessage> messages)
        {
            lock (_stateLock)
            {
                if (AreSameMessages(_messages, messages))
                    return false;

                _messages = messages;
                return true;
            }
        }

        private bool TryUpdateStatus(string statusText)
        {
            lock (_stateLock)
            {
                if (string.Equals(_statusText, statusText, StringComparison.Ordinal))
                    return false;

                _statusText = statusText;
                return true;
            }
        }

        private bool TryUpdateLastKnown(long messageId)
        {
            if (messageId <= 0)
                return false;

            lock (_stateLock)
            {
                if (messageId <= _lastKnownId)
                    return false;

                _lastKnownId = messageId;
                AppStateStore.SetLong("comms_last_known_id", _lastKnownId);
                return true;
            }
        }

        private static bool AreSameMessages(IReadOnlyList<CommsMessage> left, IReadOnlyList<CommsMessage> right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                var a = left[i];
                var b = right[i];
                if (a.Id != b.Id ||
                    a.CreatedAtUtc != b.CreatedAtUtc ||
                    a.UpdatedAtUtc != b.UpdatedAtUtc ||
                    !string.Equals(a.AuthorLabel, b.AuthorLabel, StringComparison.Ordinal) ||
                    !string.Equals(a.HeadingText, b.HeadingText, StringComparison.Ordinal) ||
                    !string.Equals(a.MessageText, b.MessageText, StringComparison.Ordinal) ||
                    !AreSameFormattedStrings(a.MessageFormatted, b.MessageFormatted))
                {
                    return false;
                }
            }

            return true;
        }


        private static bool AreSameFormattedStrings(FormattedString? left, FormattedString? right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left == null || right == null)
                return false;

            var leftSpans = left.Spans;
            var rightSpans = right.Spans;
            if (leftSpans.Count != rightSpans.Count)
                return false;

            for (var i = 0; i < leftSpans.Count; i++)
            {
                var a = leftSpans[i];
                var b = rightSpans[i];
                if (!string.Equals(a.Text, b.Text, StringComparison.Ordinal) ||
                    !string.Equals(ToArgbHex(a.TextColor), ToArgbHex(b.TextColor), StringComparison.Ordinal) ||
                    a.FontAttributes != b.FontAttributes ||
                    a.FontSize != b.FontSize ||
                    a.TextDecorations != b.TextDecorations)
                {
                    return false;
                }
            }

            return true;
        }

        private static string? ToArgbHex(Color? color)
        {
            if (color == null)
                return null;

            var red = (int)Math.Round(color.Red * 255d);
            var green = (int)Math.Round(color.Green * 255d);
            var blue = (int)Math.Round(color.Blue * 255d);
            var alpha = (int)Math.Round(color.Alpha * 255d);
            return $"{alpha:X2}{red:X2}{green:X2}{blue:X2}";
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); } catch { }
        }

        private static bool IsAuthNotReadyMessage(string? message)
        {
            message = (message ?? string.Empty).Trim();
            if (message.Length == 0)
                return true;

            if (message.Contains("Not authenticated", StringComparison.OrdinalIgnoreCase))
                return true;

            if (message.Contains("HTTP 400", StringComparison.OrdinalIgnoreCase))
                return true;

            if (message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
