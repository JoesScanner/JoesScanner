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
            private set
            {
                if (_isBusy == value)
                    return;
                _isBusy = value;
                OnPropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (_statusText == value)
                    return;
                _statusText = value;
                OnPropertyChanged();
            }
        }

        public async Task OnPageOpenedAsync()
        {
            if (_isPolling)
                return;

            _isPolling = true;

            try { _pollCts?.Cancel(); } catch { }
            _pollCts = new CancellationTokenSource();

            _lastSeq = 0;
            _lastMessageId = 0;
            _pollTick = 0;

            // Initial snapshot
            await SyncOnceAsync(forceSnapshot: true, forceShowBusy: true);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (_pollCts != null && !_pollCts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), _pollCts.Token);

                        _pollTick++;
                        var forceSnapshot = (_pollTick % SnapshotEveryPolls) == 0;

                        await SyncOnceAsync(forceSnapshot, forceShowBusy: false);
                    }
                }
                catch
                {
                }
            });
        }

        public Task OnPageClosedAsync()
        {
            _isPolling = false;
            try { _pollCts?.Cancel(); } catch { }
            return Task.CompletedTask;
        }

        private async Task FullRefreshAsync()
        {
            try
            {
                if (_pollCts == null || _pollCts.IsCancellationRequested)
                    _pollCts = new CancellationTokenSource();

                _lastSeq = 0;
                _lastMessageId = 0;

                MainThread.BeginInvokeOnMainThread(() => Messages.Clear());

                await SyncOnceAsync(forceSnapshot: true, forceShowBusy: true);
            }
            catch
            {
            }
        }

        private async Task SyncOnceAsync(bool forceSnapshot, bool forceShowBusy)
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
                    StatusText = string.IsNullOrWhiteSpace(result.Message) ? "Unable to load messages." : result.Message;
                    return;
                }

                var didAdd = false;

                if (result.HasSnapshot)
                {
                    var ordered = result.Snapshot
                        .OrderBy(m => m.CreatedAtUtc)
                        .ToList();

                    MainThread.BeginInvokeOnMainThread(() =>
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
                    foreach (var change in result.Changes)
                    {
                        if (string.Equals(change.Type, "delete", StringComparison.OrdinalIgnoreCase))
                        {
                            var id = change.MessageId;
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                var existing = Messages.FirstOrDefault(x => x.Id == id);
                                if (existing != null)
                                    Messages.Remove(existing);
                            });
                        }
                        else if (string.Equals(change.Type, "upsert", StringComparison.OrdinalIgnoreCase) && change.Message != null)
                        {
                            var msg = change.Message;
                            if (msg.Id > _lastMessageId)
                                _lastMessageId = msg.Id;

                            MainThread.BeginInvokeOnMainThread(() =>
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
                                    return;
                                }

                                // Keep chronological order (oldest at top).
                                var insertAt = Messages.Count;
                                for (var i = 0; i < Messages.Count; i++)
                                {
                                    if (Messages[i].CreatedAtUtc > msg.CreatedAtUtc)
                                    {
                                        insertAt = i;
                                        break;
                                    }
                                }

                                Messages.Insert(insertAt, msg);
                            });

                            didAdd = true;
                        }
                    }
                }

                _lastSeq = result.NextSeq > _lastSeq ? result.NextSeq : _lastSeq;

                // Viewing the communications page counts as having seen the latest messages.
                if (_lastMessageId > 0)
                    _commsBadge.MarkSeenUpTo(_lastMessageId);

                if (Messages.Count == 0)
                    StatusText = "No messages yet.";
                else
                    StatusText = string.Empty;

                if (didAdd)
                    ScrollToBottomRequested?.Invoke();

                // When the communications page is open, treat messages as seen.
                if (_lastMessageId > 0)
                    _commsBadge.MarkSeenUpTo(_lastMessageId);
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
