using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace JoesScanner.ViewModels
{
    public sealed class CommunicationsViewModel : BindableObject
    {
        private readonly ICommunicationsService _communicationsService;
        private readonly ISettingsService _settings;
        private readonly ICommsBadgeService _commsBadge;

        private CancellationTokenSource? _pollCts;
        private bool _isPolling;

        private bool _isBusy;
        private string _statusText = string.Empty;

        private long _lastSeq;
        private long _lastMessageId;
        private int _pollTick;

        // Every 60 polls (10 seconds each) force a full snapshot.
        private const int SnapshotEveryPolls = 60;

        public ObservableCollection<CommsMessage> Messages { get; } = new();

        public event Action? ScrollToBottomRequested;

        public ICommand RefreshCommand { get; }

        public CommunicationsViewModel(ICommunicationsService communicationsService, ISettingsService settings, ICommsBadgeService commsBadge)
        {
            _communicationsService = communicationsService ?? throw new ArgumentNullException(nameof(communicationsService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _commsBadge = commsBadge ?? throw new ArgumentNullException(nameof(commsBadge));

            RefreshCommand = new Command(async () => await FullRefreshAsync());
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => SetIsBusy(value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetStatusText(value);
        }


        public void MarkAllKnownAsSeenOnNavigate()
        {
            try { _commsBadge.MarkAllKnownAsSeen(); } catch { }
        }


        public async Task OnPageOpenedAsync()
        {
            if (_isPolling)
                return;

            _isPolling = true;

            // Entering the page counts as viewing messages. Clear the badge immediately.
            try { _commsBadge.MarkAllKnownAsSeen(); } catch { }

            try { _pollCts?.Cancel(); } catch { }
            _pollCts = new CancellationTokenSource();

            // If messages were preloaded at app start, keep the existing state so we can
            // begin with an incremental sync instead of forcing a full snapshot.
            var hasExistingState = Messages.Count > 0 && _lastSeq > 0;

            if (!hasExistingState)
            {
                _lastSeq = 0;
                _lastMessageId = 0;
                _pollTick = 0;

                // Initial snapshot
                await SyncOnceAsync(forceSnapshot: true, forceShowBusy: true, markSeen: true);
            }
            else
            {
                // Quick incremental sync to catch anything since preload.
                await SyncOnceAsync(forceSnapshot: false, forceShowBusy: false, markSeen: true);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (_pollCts != null && !_pollCts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), _pollCts.Token);

                        _pollTick++;
                        var forceSnapshot = (_pollTick % SnapshotEveryPolls) == 0;

                        await SyncOnceAsync(forceSnapshot, forceShowBusy: false, markSeen: true);
                    }
                }
                catch
                {
                }
            });
        }

                // Called at app start to make sure the communications tab is ready immediately.
        // This loads a snapshot once but does not start the polling loop and does not mark messages as seen.
        public async Task PreloadOnAppStartAsync()
        {
            if (Messages.Count > 0)
                return;

            // Best effort preload. The comms endpoint requires a valid server-side session row.
            // On fresh startup the app may generate a new token before the first telemetry ping/app_event reaches the server.
            // Retry briefly and suppress transient auth errors.
            var delaysMs = new[] { 250, 500, 900, 1200, 1500, 2000, 2000, 2000 };

            for (var attempt = 0; attempt < delaysMs.Length; attempt++)
            {
                try
                {
                    if (_pollCts == null || _pollCts.IsCancellationRequested)
                        _pollCts = new CancellationTokenSource();

                    _lastSeq = 0;
                    _lastMessageId = 0;
                    _pollTick = 0;

                    await SyncOnceAsync(forceSnapshot: true, forceShowBusy: false, markSeen: false, suppressAuthErrors: true);
                    if (Messages.Count > 0 || StatusText == "No messages yet.")
                        return;
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delaysMs[attempt]), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        public Task OnPageClosedAsync()
        {
            _isPolling = false;
            try { _pollCts?.Cancel(); } catch { }
            return Task.CompletedTask;
        }

        private void SetIsBusy(bool value)
        {
            if (MainThread.IsMainThread)
            {
                SetIsBusyCore(value);
                return;
            }

            MainThread.BeginInvokeOnMainThread(() => SetIsBusyCore(value));
        }

        private void SetIsBusyCore(bool value)
        {
            if (_isBusy == value)
                return;

            _isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
        }

        private void SetStatusText(string? value)
        {
            var normalized = value ?? string.Empty;
            if (MainThread.IsMainThread)
            {
                SetStatusTextCore(normalized);
                return;
            }

            MainThread.BeginInvokeOnMainThread(() => SetStatusTextCore(normalized));
        }

        private void SetStatusTextCore(string value)
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
                return;

            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
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

        private async Task FullRefreshAsync()
        {
            try
            {
                if (_pollCts == null || _pollCts.IsCancellationRequested)
                    _pollCts = new CancellationTokenSource();

                _lastSeq = 0;
                _lastMessageId = 0;

                await MainThread.InvokeOnMainThreadAsync(() => Messages.Clear());

                await SyncOnceAsync(forceSnapshot: true, forceShowBusy: true, markSeen: true);
            }
            catch
            {
            }
        }

        private async Task SyncOnceAsync(bool forceSnapshot, bool forceShowBusy, bool markSeen, bool suppressAuthErrors = false)
        {
            if (_pollCts == null)
                return;

            if (forceShowBusy)
                IsBusy = true;

            try
            {
                var serverUrl = _settings.AuthServerBaseUrl ?? string.Empty;
                var token = _settings.AuthSessionToken ?? string.Empty;

                var result = await _communicationsService.SyncAsync(
                    serverUrl,
                    token,
                    _lastSeq,
                    forceSnapshot,
                    _pollCts.Token);

                if (!result.Ok)
                {
                    if (suppressAuthErrors && IsAuthNotReadyMessage(result.Message))
                        return;

                    StatusText = string.IsNullOrWhiteSpace(result.Message) ? "Unable to load messages." : result.Message;
                    return;
                }

                var didAdd = false;

                if (result.HasSnapshot)
                {
                    var ordered = result.Snapshot
                        .OrderByDescending(m => m.CreatedAtUtc)
                        .ToList();

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        Messages.Clear();
                        foreach (var m in ordered)
                            Messages.Add(m);
                    });

                    if (ordered.Count > 0)
                    {
                        _lastMessageId = ordered.Max(m => m.Id);
                        didAdd = true;
                    }
                }
                else if (result.Changes.Count > 0)
                {
                    var deletes = result.Changes
                        .Where(c => string.Equals(c.Type, "delete", StringComparison.OrdinalIgnoreCase))
                        .Select(c => c.MessageId)
                        .ToList();

                    var upserts = result.Changes
                        .Where(c => string.Equals(c.Type, "upsert", StringComparison.OrdinalIgnoreCase) && c.Message != null)
                        .Select(c => c.Message!)
                        .ToList();

                    foreach (var msg in upserts)
                    {
                        if (msg.Id > _lastMessageId)
                            _lastMessageId = msg.Id;
                    }

                    if (deletes.Count > 0 || upserts.Count > 0)
                    {
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            foreach (var id in deletes)
                            {
                                var existing = Messages.FirstOrDefault(x => x.Id == id);
                                if (existing != null)
                                    Messages.Remove(existing);
                            }

                            foreach (var msg in upserts)
                            {
                                var existingIndex = -1;
                                for (var i = 0; i < Messages.Count; i++)
                                {
                                    if (Messages[i].Id == msg.Id)
                                    {
                                        existingIndex = i;
                                        break;
                                    }
                                }

                                if (existingIndex >= 0)
                                {
                                    Messages[existingIndex] = msg;
                                    continue;
                                }

                                // Keep newest at top.
                                var insertAt = Messages.Count;
                                for (var i = 0; i < Messages.Count; i++)
                                {
                                    if (Messages[i].CreatedAtUtc < msg.CreatedAtUtc)
                                    {
                                        insertAt = i;
                                        break;
                                    }
                                }

                                Messages.Insert(insertAt, msg);
                            }
                        });

                        if (upserts.Count > 0)
                            didAdd = true;
                    }
                }

                _lastSeq = result.NextSeq > _lastSeq ? result.NextSeq : _lastSeq;

                // Only treat messages as seen when the page is open.
                if (markSeen && _lastMessageId > 0)
                    _commsBadge.MarkSeenUpTo(_lastMessageId);

                if (Messages.Count == 0)
                    StatusText = "No messages yet.";
                else
                    StatusText = string.Empty;

                if (didAdd)
                {
                    if (MainThread.IsMainThread)
                        ScrollToBottomRequested?.Invoke();
                    else
                        MainThread.BeginInvokeOnMainThread(() => ScrollToBottomRequested?.Invoke());
                }

                // MarkSeenUpTo already handled above.
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
