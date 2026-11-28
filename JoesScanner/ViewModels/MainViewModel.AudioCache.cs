using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class MainViewModel
{
    private async Task<PlaybackAudioLease?> AcquireRecordingAudioAsync(CallItem item, CancellationToken token,
        bool automatic = false, bool speculative = false, RecorderOperation? owner = null)
    {
        var server = item.RecordingServer ?? item.LiveState?.ServerUrl ?? _settingsService.ServerUrl ?? "";
        var protectedSource = IsProtectedRecording(item, server);
        owner ??= CapturePlaybackOwner(item);
        using var lifetime = owner == null ? null : CancellationTokenSource.CreateLinkedTokenSource(token, owner.Token);
        var account = RequireCurrentRecordingAccount(item);
        var accountSession = protectedSource ? _settingsService.ReadAccountSession() : default;
        var authority = protectedSource ? accountSession.BaseUrl : null;
        var key = RecordingCacheKey.Create(item.ServerId, server, item.BackendId, account, authority, item.RecordingTimeUtc);
        (string Server, bool Protected, DateTimeOffset? RecordedAt) access = default;
        var lease = await _audioCache.AcquireAsync(key, Authorize, Fetch, lifetime?.Token ?? token, speculative, owner);
        if (lease != null)
            lease.Owner = owner;
        return lease;

        void RequireAccount()
        {
            owner?.Validate();
            if (RequireCurrentRecordingAccount(item) != account ||
                protectedSource && _settingsService.ReadAccountSession().AccountVersion != accountSession.AccountVersion)
                throw new RecordingAccessException("This recording belongs to a different account.");
        }

        async Task Authorize(bool cached, CancellationToken acquireToken)
        {
            RequireAccount();
            if (!cached)
                RecordingPlaybackAccess.RequireRecorderNetwork(_settingsService, server);
            access = await AuthorizeRecordingAsync(item, acquireToken, cached);
            RequireAccount();
        }

        async Task<string> Fetch(PlaybackAudioCache.Transfer transfer, CancellationToken acquireToken)
        {
            using var authorization = ProtectLiveRecording(item, acquireToken, access.RecordedAt);
            authorization?.Validate();
            RequireAccount();
            var fetchToken = authorization?.Token ?? acquireToken;
            var lastUrl = item.AudioUrl;
            var scope = Uri.TryCreate(server, UriKind.Absolute, out var origin)
                ? new RecorderRequestScope(origin, item.ServerId, owner) : (RecorderRequestScope?)null;
            string path;
            if (automatic)
            {
                // Speculation never refreshes URLs or writes metadata into the durable queue.
                var url = string.IsNullOrWhiteSpace(lastUrl) && !speculative ? await Refresh(fetchToken) : lastUrl;
                path = !string.IsNullOrWhiteSpace(url) && await Download(url, fetchToken) is { Length: > 0 } file
                    ? file : throw new RecordingAudioUnavailableException();
            }
            else
                path = await RecordingAudioRecovery.AcquireAsync(lastUrl, Refresh, Download, fetchToken);
            authorization?.Validate();
            RequireAccount();
            return path;

            Task<string?> Download(string url, CancellationToken downloadToken)
            {
                RequireAccount();
                RecordingPlaybackAccess.RequireRecorderNetwork(_settingsService, server);
                return RecordingAudioRecovery.DownloadAsync(_audioHttpClient, url, transfer, downloadToken, request =>
                {
                    if (scope is { } recorder)
                        request.Options.Set(ServerAuthenticationHandler.Recorder, recorder);
                });
            }

            async Task<string?> Refresh(CancellationToken refreshToken)
            {
                RecordingPlaybackAccess.RequireRecorderNetwork(_settingsService, server);
                if (!string.IsNullOrWhiteSpace(item.AudioUrl) && item.AudioUrl != lastUrl)
                {
                    if (access.Protected)
                        await AuthorizeRecordingAsync(item, refreshToken);
                    RequireAccount();
                    refreshToken.ThrowIfCancellationRequested();
                    return lastUrl = item.LiveState is { } currentState ? currentState.Read().AudioUrl : item.AudioUrl;
                }

                var durable = ScopeOf(item);
                var beforeLookup = item.LiveState?.Read();
                var refreshed = await RefreshRecorderAudioAsync(item, server, access.RecordedAt, refreshToken, owner);
                RequireAccount();
                if (item.LiveState is { } state && durable is { } run && IsCurrentLiveScope(run) &&
                    TryGetDurableRecording(item, run, out _))
                    await _durableQueue.ObserveAsync(run, state.Id, null, state.Read(), refreshToken, existingOnly: true);
                var current = await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    RequireAccount();
                    return item.ApplyRefreshedAudioUrl(refreshed, beforeLookup, refreshToken);
                });
                if (current != null)
                    lastUrl = current;
                return current;
            }
        }
    }

    private RecorderOperation? CapturePlaybackOwner(CallItem item)
    {
        var server = item.RecordingServer ?? item.LiveState?.ServerUrl ?? _settingsService.ServerUrl;
        return item.ServerId is { } id && Uri.TryCreate(server, UriKind.Absolute, out var endpoint)
            ? DirectoryRecorderPolicy.For(_settingsService).Capture(id, endpoint) : null;
    }

    private bool IsProtectedRecording(CallItem item, string server) =>
        item.RecordingAccess?.Protected == true || HostedServerRules.RequiresApiFirewallCredentials(_settingsService, server);

    private bool CanPlayAsCurrentLive(CallItem item) =>
        item.RecordingAccess?.Origin == RecordingOrigin.Live && ScopeOf(item) is { } scope &&
        scope.Owner == item.LivePlaybackOwner;

    private HostedEntitlement.Access? ProtectLiveRecording(CallItem item, CancellationToken token,
        DateTimeOffset? recordedAt = null)
    {
        var server = item.RecordingServer ?? item.LiveState?.ServerUrl ?? _settingsService.ServerUrl ?? "";
        if (!IsProtectedRecording(item, server))
            return null;
        RequireCurrentRecordingAccount(item);
        return HostedEntitlement.For(_settingsService).Protect(token, recordedAt: recordedAt ?? item.RecordingTimeUtc,
            history: !CanPlayAsCurrentLive(item));
    }

    private void RequireLiveRecordingEntitlement(CallItem item)
    {
        var server = item.RecordingServer ?? item.LiveState?.ServerUrl ?? _settingsService.ServerUrl ?? "";
        if (IsProtectedRecording(item, server))
            RecordingPlaybackAccess.RequireCachedEntitlement(_settingsService, server, RequireCurrentRecordingAccount(item),
                item.RecordingTimeUtc, CanPlayAsCurrentLive(item), independent: item.ServerId != null);
    }
}
