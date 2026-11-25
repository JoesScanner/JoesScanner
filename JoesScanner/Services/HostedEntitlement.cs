using System.Runtime.CompilerServices;

namespace JoesScanner.Services;

internal sealed class HostedEntitlement
{
    private static readonly ConditionalWeakTable<ISettingsService, HostedEntitlement> Instances = new();
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);
    private readonly ISettingsService _settings;

    private HostedEntitlement(ISettingsService settings) => _settings = settings;

    internal static HostedEntitlement For(ISettingsService settings) =>
        Instances.GetValue(settings, static source => new(source));

    internal event Action? Changed;

    internal void NotifyChanged() => Changed?.Invoke();

    internal static DateTime? Earliest(DateTime? first, DateTime? second) =>
        first == null || second < first ? second : first;

    internal static DateTimeOffset? Deadline(HostedSubscriptionSnapshot snapshot, DateTimeOffset now)
    {
        if (!snapshot.IsAllowed || snapshot.Tier is not (1 or 2) ||
            snapshot.CheckedUtc is not { } checkedAt || checkedAt > now.UtcDateTime)
            return null;

        // CheckedUtc is the last successful verification. Transient failures never move it.
        var end = Earliest(Earliest(snapshot.ExpiresUtc, snapshot.RenewalUtc), checkedAt + GracePeriod);
        return end is { } value && value > now.UtcDateTime ? new DateTimeOffset(value, TimeSpan.Zero) : null;
    }

    internal HostedSubscriptionSnapshot? Current(TimeProvider? clock = null)
    {
        var snapshot = _settings.ReadHostedSubscription();
        return Deadline(snapshot, (clock ?? TimeProvider.System).GetUtcNow()) != null ? snapshot : null;
    }

    internal Access Protect(CancellationToken token, TimeProvider? clock = null,
        DateTimeOffset? recordedAt = null, bool history = false, int? expectedTier = null, long? accountVersion = null) =>
        new(this, token, clock ?? TimeProvider.System, recordedAt, history, expectedTier, accountVersion);

    // Only protected work receives this token. As new grants arrive the timer is
    // extended, or the operation is canceled if the account, tier or deadline excludes it.
    internal sealed class Access : IDisposable
    {
        private readonly HostedEntitlement _owner;
        private readonly long _accountVersion;
        private readonly TimeProvider _clock;
        private readonly DateTimeOffset? _recordedAt;
        private readonly bool _history;
        private readonly int? _expectedTier;
        private readonly CancellationToken _parent;
        private readonly CancellationTokenSource _lifetime;
        private readonly ITimer _deadline;
        private readonly object _gate = new();
        private bool _disposed;

        internal Access(HostedEntitlement owner, CancellationToken token, TimeProvider clock,
            DateTimeOffset? recordedAt, bool history, int? expectedTier, long? accountVersion)
        {
            _owner = owner;
            _accountVersion = accountVersion ?? owner._settings.ReadAccountSession().AccountVersion;
            _clock = clock;
            _recordedAt = recordedAt;
            _history = history;
            _expectedTier = expectedTier;
            _parent = token;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = _lifetime.Token;
            _deadline = clock.CreateTimer(_ => Refresh(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            owner.Changed += Refresh;
            owner._settings.AccountIdentityChanged += OnAccountChanged;
            Refresh();
        }

        internal CancellationToken Token { get; }

        internal void Validate()
        {
            Refresh();
            _parent.ThrowIfCancellationRequested();
            if (Token.IsCancellationRequested)
                throw RecordingPlaybackAccess.VerificationRequired();
        }

        private void OnAccountChanged(object? sender, EventArgs args) => Refresh();

        private void Refresh()
        {
            if (Token.IsCancellationRequested)
                return;

            DateTimeOffset? end = null;
            try
            {
                _owner._settings.TryApplyAccountIdentity(_accountVersion, () =>
                {
                    var now = _clock.GetUtcNow();
                    var snapshot = _owner._settings.ReadHostedSubscription();
                    end = Deadline(snapshot, now);
                    if (_expectedTier.HasValue && snapshot.Tier != _expectedTier)
                        end = null;
                    if (_history && snapshot.Tier == 1)
                        end = _recordedAt is { } recordedAt && recordedAt <= now && recordedAt > now.AddHours(-24)
                            ? (recordedAt.AddHours(24) < end ? recordedAt.AddHours(24) : end)
                            : null;

                    ApplyDeadline(end);
                });
            }
            catch (Exception ex)
            {
                AppLog.Basic(() => $"Hosted authorization could not be checked ({DiagnosticRedaction.Describe(ex)}).");
                end = null;
            }
            if (end == null)
                ApplyDeadline(null);
        }

        private void ApplyDeadline(DateTimeOffset? end)
        {
            lock (_gate)
            {
                if (_disposed || Token.IsCancellationRequested)
                    return;
                var remaining = end - _clock.GetUtcNow();
                if (remaining is { } due && due > TimeSpan.Zero)
                    _deadline.Change(due, Timeout.InfiniteTimeSpan);
                else
                    _ = _lifetime.CancelAsync();
            }
        }

        internal async Task RetireAsync()
        {
            Task canceled;
            lock (_gate)
                canceled = _lifetime.CancelAsync();
            try { await canceled.ConfigureAwait(false); }
            catch (Exception ex)
            {
                AppLog.Basic(() => $"Hosted authorization cleanup failed ({DiagnosticRedaction.Describe(ex)}).");
            }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            _owner.Changed -= Refresh;
            _owner._settings.AccountIdentityChanged -= OnAccountChanged;
            lock (_gate)
            {
                _disposed = true;
                _deadline.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
