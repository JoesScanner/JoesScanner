using System.Collections.Concurrent;

namespace JoesScanner.Services
{
    public sealed class ToneAlertService : IToneAlertService, IDisposable
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _hot = new();
        private readonly Timer _timer;

        public ToneAlertService()
        {
            // Light cleanup pulse, also drives UI refresh for expiring highlights.
            _timer = new Timer(_ => CleanupAndPulse(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public event Action<string>? ToneDetected;
        public event Action? HotTalkgroupsChanged;

        public void NotifyToneDetected(string audioUrl)
        {
            if (string.IsNullOrWhiteSpace(audioUrl))
                return;

            try
            {
                AppLog.Add(() => $"AudioTone: notify detected. url={audioUrl}");
            }
            catch { }

            try
            {
                ToneDetected?.Invoke(audioUrl);
            }
            catch
            {
            }
        }

        public void SetTalkgroupHot(string hotKey, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(hotKey))
                return;

            if (duration <= TimeSpan.Zero)
                return;

            var until = DateTimeOffset.UtcNow.Add(duration);
            _hot.AddOrUpdate(hotKey, until, (_, __) => until);

            try
            {
                AppLog.Add(() => $"ToneHot: set key={hotKey} untilUtc={until:O}");
            }
            catch { }

            PulseChanged();
        }

        public bool IsTalkgroupHot(string hotKey)
        {
            if (string.IsNullOrWhiteSpace(hotKey))
                return false;

            if (!_hot.TryGetValue(hotKey, out var until))
                return false;

            return until > DateTimeOffset.UtcNow;
        }

        private void CleanupAndPulse()
        {
            var now = DateTimeOffset.UtcNow;
            var removedCount = 0;

            foreach (var kvp in _hot)
            {
                if (kvp.Value <= now)
                {
                    if (_hot.TryRemove(kvp.Key, out _))
                        removedCount++;
                }
            }

            if (removedCount > 0)
            {
                try
                {
                    AppLog.Add(() => $"ToneHot: expired talkgroups count={removedCount}");
                }
                catch
                {
                }

                PulseChanged();
            }
        }

        private void PulseChanged()
        {
            try
            {
                HotTalkgroupsChanged?.Invoke();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try { _timer.Dispose(); } catch { }
        }
    }
}
