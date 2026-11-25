using JoesScanner.Helpers;
using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class MainViewModel
{
    private Task<PlaybackAudioLease?> AcquireRecordingAudioAsync(CallItem item, CancellationToken token,
        bool automatic = false, bool speculative = false)
    {
        var server = item.RecordingServer ?? item.LiveState?.ServerUrl ?? _settingsService.ServerUrl ?? "";
        var protectedSource = IsProtectedRecording(item, server);
        var account = RequireCurrentRecordingAccount(item);
        var accountSession = protectedSource ? _settingsService.ReadAccountSession() : default;
        var authority = protectedSource ? accountSession.BaseUrl : null;
        var key = RecordingCacheKey.Create(item.ServerId, server, item.BackendId, account, authority);
        (string Server, bool Protected, DateTimeOffset? RecordedAt) access = default;
        return _audioCache.AcquireAsync(key, Authorize, Fetch, token, speculative);

        void RequireAccount()
        {
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
                ? new RecorderRequestScope(origin, item.ServerId) : (RecorderRequestScope?)null;
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
                    return lastUrl = item.AudioUrl;
                }

                var durable = ScopeOf(item);
                var refreshed = await RefreshRecorderAudioAsync(item, server, access.RecordedAt, refreshToken);
                if (item.LiveState is { } state && durable is { } run && IsCurrentLiveScope(run) &&
                    TryGetDurableRecording(item, run, out _))
                    await _durableQueue.ObserveAsync(run, state.Id, null, state.Read(), refreshToken, existingOnly: true);
                refreshToken.ThrowIfCancellationRequested();
                RequireAccount();
                if (refreshed != null)
                    item.AudioUrl = lastUrl = refreshed;
                return refreshed;
            }
        }
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
