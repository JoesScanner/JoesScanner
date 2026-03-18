using JoesScanner.Models;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace JoesScanner.ViewModels
{
    public sealed class CommunicationsViewModel : BindableObject
    {
        private readonly ICommunicationsSyncCoordinator _coordinator;
        private readonly ICommsBadgeService _commsBadge;

        private bool _pageActive;
        private bool _isBusy;
        private string _statusText = string.Empty;
        private long _lastRenderedMessageId;

        public ObservableCollection<CommsMessage> Messages { get; } = new();

        public event Action? ScrollToBottomRequested;

        public ICommand RefreshCommand { get; }

        public CommunicationsViewModel(ICommunicationsSyncCoordinator coordinator, ICommsBadgeService commsBadge)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _commsBadge = commsBadge ?? throw new ArgumentNullException(nameof(commsBadge));

            _coordinator.Changed += OnCoordinatorChanged;
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
            if (_pageActive)
                return;

            _pageActive = true;
            _coordinator.SetPageActive(true);
            _coordinator.Start();

            try { _commsBadge.MarkAllKnownAsSeen(); } catch { }

            await _coordinator.EnsurePreloadedAsync().ConfigureAwait(false);
            await ApplyCoordinatorStateAsync(markSeen: true).ConfigureAwait(false);
        }

        public Task OnPageClosedAsync()
        {
            if (_pageActive)
            {
                _pageActive = false;
                _coordinator.SetPageActive(false);
            }

            return Task.CompletedTask;
        }

        private void OnCoordinatorChanged()
        {
            _ = ApplyCoordinatorStateAsync(markSeen: _pageActive);
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

        private async Task FullRefreshAsync()
        {
            IsBusy = true;
            try
            {
                await _coordinator.ForceRefreshAsync().ConfigureAwait(false);
                await ApplyCoordinatorStateAsync(markSeen: _pageActive).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ApplyCoordinatorStateAsync(bool markSeen)
        {
            var snapshot = _coordinator.Messages.ToList();
            var statusText = _coordinator.StatusText;
            var latestMessageId = snapshot.Count > 0 ? snapshot.Max(m => m.Id) : 0;
            var shouldScroll = latestMessageId > _lastRenderedMessageId;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                Messages.Clear();
                foreach (var message in snapshot)
                    Messages.Add(message);
            });

            _lastRenderedMessageId = latestMessageId;
            StatusText = statusText;

            if (markSeen && latestMessageId > 0)
                _commsBadge.MarkSeenUpTo(latestMessageId);

            if (shouldScroll)
            {
                if (MainThread.IsMainThread)
                    ScrollToBottomRequested?.Invoke();
                else
                    MainThread.BeginInvokeOnMainThread(() => ScrollToBottomRequested?.Invoke());
            }
        }
    }
}
