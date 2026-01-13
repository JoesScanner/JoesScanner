using JoesScanner.Models;
using Microsoft.Maui.Controls;
using JoesScanner.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;

namespace JoesScanner.ViewModels
{
    // View model for the History page.
    // Loads a fixed list of recent calls and allows the user to anchor playback
    // to a selected local time using hour, minute, and second controls.
    public sealed class HistoryViewModel : BindableObject
    {
        private readonly ICallHistoryService _callHistoryService;
        private readonly IAudioPlaybackService _audioPlaybackService;

        private CancellationTokenSource? _audioCts;

        private bool _isLoading;
        private bool _isQueuePlaybackRunning;

        private string _statusText = string.Empty;

        private int _hour12;
        private int _minute;
        private int _second;

        private CallItem? _selectedCall;
        private int _lastPlayedIndex = -1;

        private bool _suppressSelectionPlayback;

        public event Action<CallItem, ScrollToPosition>? ScrollRequested;

        public HistoryViewModel(ICallHistoryService callHistoryService, IAudioPlaybackService audioPlaybackService)
        {
            _callHistoryService = callHistoryService ?? throw new ArgumentNullException(nameof(callHistoryService));
            _audioPlaybackService = audioPlaybackService ?? throw new ArgumentNullException(nameof(audioPlaybackService));

            Calls = new ObservableCollection<CallItem>();

            SearchCommand = new Command(async () => await SearchAsync(), () => !IsLoading && Calls.Count > 0);
            PlayCommand = new Command(async () => await PlayFromSelectedAsync(), () => !IsLoading && Calls.Count > 0);
            StopCommand = new Command(async () => await StopAsync(), () => !IsLoading);
            NextCommand = new Command(async () => await SkipNextAsync(), () => !IsLoading && Calls.Count > 0);
            PreviousCommand = new Command(async () => await SkipPreviousAsync(), () => !IsLoading && Calls.Count > 0);
            PlayCallCommand = new Command<CallItem>(async (call) => await PlayFromCallAsync(call), (call) => !IsLoading && call != null);

            HourMinusCommand = new Command(() => AdjustHours(-1), () => !IsLoading);
            HourPlusCommand = new Command(() => AdjustHours(1), () => !IsLoading);

            MinuteMinusCommand = new Command(() => AdjustMinutes(-10), () => !IsLoading);
            MinutePlusCommand = new Command(() => AdjustMinutes(10), () => !IsLoading);

            SecondMinusCommand = new Command(() => AdjustSeconds(-10), () => !IsLoading);
            SecondPlusCommand = new Command(() => AdjustSeconds(10), () => !IsLoading);

            SetTimeFromNow();
        }

        public ObservableCollection<CallItem> Calls { get; }

        public ICommand SearchCommand { get; }
        public ICommand PlayCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand PreviousCommand { get; }
        public ICommand PlayCallCommand { get; }

        public ICommand HourMinusCommand { get; }
        public ICommand HourPlusCommand { get; }

        public ICommand MinuteMinusCommand { get; }
        public ICommand MinutePlusCommand { get; }

        public ICommand SecondMinusCommand { get; }
        public ICommand SecondPlusCommand { get; }

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value)
                    return;

                _isLoading = value;
                OnPropertyChanged();

                RefreshCommandStates();
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

        // 12 hour control, range 1 to 12.
        public int Hour12
        {
            get => _hour12;
            set
            {
                var v = Math.Clamp(value, 1, 12);
                if (_hour12 == v)
                    return;

                _hour12 = v;
                OnPropertyChanged();
            }
        }

        public int Minute
        {
            get => _minute;
            set
            {
                var v = Math.Clamp(value, 0, 59);
                if (_minute == v)
                    return;

                _minute = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MinuteText));
            }
        }

        public int Second
        {
            get => _second;
            set
            {
                var v = Math.Clamp(value, 0, 59);
                if (_second == v)
                    return;

                _second = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SecondText));
            }
        }

        public string MinuteText => Minute.ToString("00", CultureInfo.InvariantCulture);
        public string SecondText => Second.ToString("00", CultureInfo.InvariantCulture);

        public CallItem? SelectedCall
        {
            get => _selectedCall;
            set
            {
                if (_selectedCall == value)
                    return;

                _selectedCall = value;
                OnPropertyChanged();

                if (_suppressSelectionPlayback)
                    return;

                // Selection should not auto start playback. Tapping a row does that.
            }
        }

        public async Task OnPageOpenedAsync()
        {
            if (IsLoading)
                return;

            try
            {
                IsLoading = true;
                StatusText = "Loading history...";

                var latest = await _callHistoryService.GetLatestCallsAsync(25, CancellationToken.None);

                Calls.Clear();
                foreach (var call in latest)
                {
                    call.IsHistory = true;
                    call.IsPlaying = false;
                    Calls.Add(call);
                }

                SetTimeFromNow();

                _suppressSelectionPlayback = true;
                SelectedCall = Calls.Count > 0 ? Calls[0] : null;
                _suppressSelectionPlayback = false;

                StatusText = Calls.Count > 0 ? string.Empty : "No history calls returned.";
            }
            catch (Exception ex)
            {
                StatusText = "History load error: " + ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        async Task SearchAsync()
        {
            if (Calls.Count == 0)
                return;

            var targetLocal = ResolveSelectedLocalTimeToMostRecentPast();

            var bestIndex = 0;
            var bestDelta = TimeSpan.MaxValue;

            for (var i = 0; i < Calls.Count; i++)
            {
                var delta = (Calls[i].Timestamp - targetLocal).Duration();
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIndex = i;
                }
            }

            _suppressSelectionPlayback = true;
            SelectedCall = Calls[bestIndex];
            _suppressSelectionPlayback = false;

            StatusText = "Showing closest call to " + targetLocal.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);

            ScrollRequested?.Invoke(Calls[bestIndex], ScrollToPosition.End);
        }

        DateTime ResolveSelectedLocalTimeToMostRecentPast()
        {
            var now = DateTime.Now;

            // Interpret Hour12 with no am/pm as the most recent past occurrence.
            // Example: if Hour12=3 and now is 2:00 PM, we choose 3:xx:xx AM (today) not 3:xx:xx PM (future).
            var candidates = new List<DateTime>(2);

            foreach (var h24 in GetCandidateHours24(Hour12))
            {
                var dt = new DateTime(now.Year, now.Month, now.Day, h24, Minute, Second, DateTimeKind.Local);
                if (dt > now)
                    dt = dt.AddDays(-1);

                candidates.Add(dt);
            }

            var best = candidates[0];
            for (var i = 1; i < candidates.Count; i++)
            {
                if (candidates[i] > best && candidates[i] <= now)
                    best = candidates[i];
            }

            return best;
        }

        static IEnumerable<int> GetCandidateHours24(int hour12)
        {
            hour12 = Math.Clamp(hour12, 1, 12);

            if (hour12 == 12)
            {
                yield return 0;
                yield return 12;
                yield break;
            }

            yield return hour12;
            yield return hour12 + 12;
        }

        async Task PlayFromSelectedAsync()
        {
            if (Calls.Count == 0)
                return;

            var call = SelectedCall ?? Calls[0];
            await PlayFromCallAsync(call);
        }

        async Task PlayFromCallAsync(CallItem? call)
        {
            if (call == null)
                return;

            var index = IndexOfCall(call);
            if (index < 0)
                return;

            await StartQueuePlaybackFromIndexAsync(index);
        }

        async Task SkipNextAsync()
        {
            if (Calls.Count == 0)
                return;

            var current = _lastPlayedIndex >= 0 ? _lastPlayedIndex : IndexOfCall(SelectedCall);
            if (current <= 0)
                return;

            await StartQueuePlaybackFromIndexAsync(current - 1);
        }

        async Task SkipPreviousAsync()
        {
            if (Calls.Count == 0)
                return;

            var current = _lastPlayedIndex >= 0 ? _lastPlayedIndex : IndexOfCall(SelectedCall);
            if (current < 0 || current >= Calls.Count - 1)
                return;

            await StartQueuePlaybackFromIndexAsync(current + 1);
        }

        async Task StartQueuePlaybackFromIndexAsync(int startIndex)
        {
            if (startIndex < 0 || startIndex >= Calls.Count)
                return;

            await StopAsync();

            _audioCts = new CancellationTokenSource();
            var ct = _audioCts.Token;

            _isQueuePlaybackRunning = true;
            RefreshCommandStates();

            _lastPlayedIndex = startIndex;

            _suppressSelectionPlayback = true;
            SelectedCall = Calls[startIndex];
            _suppressSelectionPlayback = false;

            try
            {
                for (var i = startIndex; i >= 0; i--)
                {
                    ct.ThrowIfCancellationRequested();

                    _lastPlayedIndex = i;

                    foreach (var c in Calls)
                        c.IsPlaying = false;

                    var call = Calls[i];
                    call.IsPlaying = true;

                    if (!string.IsNullOrWhiteSpace(call.AudioUrl))
                    {
                        await _audioPlaybackService.PlayAsync(call.AudioUrl, 1.0, ct);
                    }

                    call.IsPlaying = false;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                StatusText = "Playback error: " + ex.Message;
            }
            finally
            {
                foreach (var c in Calls)
                    c.IsPlaying = false;

                _isQueuePlaybackRunning = false;
                RefreshCommandStates();
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _audioCts?.Cancel();
            }
            catch
            {
            }

            _audioCts = null;

            foreach (var c in Calls)
                c.IsPlaying = false;

            try
            {
                await _audioPlaybackService.StopAsync();
            }
            catch
            {
            }

            _isQueuePlaybackRunning = false;
            RefreshCommandStates();
        }

        int IndexOfCall(CallItem? call)
        {
            if (call == null)
                return -1;

            for (var i = 0; i < Calls.Count; i++)
            {
                if (ReferenceEquals(Calls[i], call))
                    return i;

                if (!string.IsNullOrWhiteSpace(call.BackendId) && Calls[i].BackendId == call.BackendId)
                    return i;
            }

            return -1;
        }

        void SetTimeFromNow()
        {
            var now = DateTime.Now;
            Hour12 = now.Hour % 12;
            if (Hour12 == 0)
                Hour12 = 12;

            Minute = now.Minute;
            Second = now.Second;
        }

        void AdjustSeconds(int deltaSeconds)
        {
            var total = Second + deltaSeconds;

            while (total >= 60)
            {
                total -= 60;
                AdjustMinutes(1);
            }

            while (total < 0)
            {
                total += 60;
                AdjustMinutes(-1);
            }

            Second = total;
        }

        void AdjustMinutes(int deltaMinutes)
        {
            var total = Minute + deltaMinutes;

            while (total >= 60)
            {
                total -= 60;
                AdjustHours(1);
            }

            while (total < 0)
            {
                total += 60;
                AdjustHours(-1);
            }

            Minute = total;
        }

        void AdjustHours(int deltaHours)
        {
            var h = Hour12 + deltaHours;

            while (h > 12)
                h -= 12;

            while (h < 1)
                h += 12;

            Hour12 = h;
        }

        void RefreshCommandStates()
        {
            (SearchCommand as Command)?.ChangeCanExecute();
            (PlayCommand as Command)?.ChangeCanExecute();
            (StopCommand as Command)?.ChangeCanExecute();
            (NextCommand as Command)?.ChangeCanExecute();
            (PreviousCommand as Command)?.ChangeCanExecute();
            (PlayCallCommand as Command)?.ChangeCanExecute();

            (HourMinusCommand as Command)?.ChangeCanExecute();
            (HourPlusCommand as Command)?.ChangeCanExecute();
            (MinuteMinusCommand as Command)?.ChangeCanExecute();
            (MinutePlusCommand as Command)?.ChangeCanExecute();
            (SecondMinusCommand as Command)?.ChangeCanExecute();
            (SecondPlusCommand as Command)?.ChangeCanExecute();
        }
    }
}
