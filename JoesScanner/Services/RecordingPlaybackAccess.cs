using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JoesScanner.Helpers;
using JoesScanner.Models;

namespace JoesScanner.Services;

internal sealed class RecordingAccessException(string message) : InvalidOperationException(message);

// Playback authorization is separate from HTTP firewall authentication.
internal static class RecordingPlaybackAccess
{
    internal sealed record HistoryGrant(string Server, string? Account, int? Tier, DateTimeOffset VerifiedAt,
        DateTime? ExpiresUtc, TimeProvider Clock, bool Independent = false)
    {
        internal DateTimeOffset? Cutoff => Tier == 1 ? Clock.GetUtcNow().AddHours(-24) : null;

        internal bool Allows(DateTimeOffset? recordedAt)
        {
            if (Tier != 1)
                return true;
            var now = Clock.GetUtcNow();
            return recordedAt >= now.AddHours(-24) && recordedAt <= now;
        }

        internal void Validate(ISettingsService settings)
        {
            if (!IsCurrent(settings))
                throw VerificationRequired();
        }

        internal bool IsCurrent(ISettingsService settings)
        {
            if (Tier == null)
                return !HostedServerRules.RequiresApiFirewallCredentials(settings, Server);

            var now = Clock.GetUtcNow().UtcDateTime;
            return (Independent || IsSelected(settings, Server)) && AccountKey(settings, Server, Independent) == Account &&
                settings.SubscriptionLastStatusOk && settings.SubscriptionLastValidatedOnline &&
                settings.SubscriptionTierLevel == Tier && !(ExpiresUtc <= now) && !(settings.SubscriptionExpiresUtc <= now);
        }
    }

    internal static bool IsSelected(ISettingsService settings, string server) =>
        Uri.TryCreate(server.TrimEnd('/'), UriKind.Absolute, out var requested) &&
        Uri.TryCreate((settings.ServerUrl ?? "").TrimEnd('/'), UriKind.Absolute, out var selected) &&
        Uri.Compare(requested, selected, UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped, StringComparison.Ordinal) == 0;

    internal static async Task<HistoryGrant> VerifyHistoryAsync(ISettingsService settings,
        ISubscriptionService subscription, string server, CancellationToken token,
        TimeProvider? clock = null, bool protectedSource = false, bool independent = false)
    {
        clock ??= TimeProvider.System;
        if (!protectedSource && !HostedServerRules.RequiresApiFirewallCredentials(settings, server))
            return new(server, null, null, clock.GetUtcNow(), null, clock);

        var account = AccountKey(settings, server, independent);
        if ((!independent && !IsSelected(settings, server)) || account == null)
            throw VerificationRequired();

        SubscriptionCheckResult result;
        try
        {
            result = await (independent ? subscription.EnsureSubscriptionForRecorderAsync(server, token)
                : subscription.EnsureSubscriptionAsync(token)).ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            throw VerificationRequired();
        }

        token.ThrowIfCancellationRequested();
        if (!result.IsAllowed || result.VerifiedOnlineTier is not (1 or 2))
            throw VerificationRequired();

        var grant = new HistoryGrant(server, account, result.VerifiedOnlineTier, clock.GetUtcNow(),
            settings.SubscriptionExpiresUtc, clock, independent);
        grant.Validate(settings);
        return grant;
    }

    internal static RecordingAccessException VerificationRequired() =>
        new("Hosted history requires a verified active subscription.");

    internal static DateTimeOffset RoundStartToSecond(DateTimeOffset lower)
    {
        var second = DateTimeOffset.FromUnixTimeSeconds(lower.ToUnixTimeSeconds());
        return second < lower ? second.AddSeconds(1) : second;
    }

    internal static string DateSearch(DateTimeOffset lower, DateTimeOffset upper, TimeSpan? recorderOffset = null)
    {
        lower = RoundStartToSecond(lower);
        var from = recorderOffset is { } offset ? lower.ToOffset(offset).DateTime : lower.LocalDateTime;
        var to = recorderOffset is { } endOffset ? upper.ToOffset(endOffset).DateTime : upper.LocalDateTime;
        return from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "|" +
            to.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    internal static string? AccountKey(ISettingsService settings, string server, bool independent = false)
    {
        string user;
        if (!independent && HostedServerRules.IsHostedStreamServerUrl(server))
            user = settings.BasicAuthUsername;
        else if (!settings.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out user, out _))
            return null;
        return AccountKeyForUsername(user);
    }

    internal static string? RecorderAccountKey(ISettingsService settings, Guid recorderId, string endpoint) =>
        settings.TryGetRecorderCredentials(recorderId, endpoint, out var username, out _)
            ? AccountKeyForUsername(username) : null;

    internal static string? AccountKeyForUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? null :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToUpperInvariant())));

    internal static void RequireAccount(ISettingsService settings, string server, string? account, bool independent = false)
    {
        if (account == null || (!independent && !IsSelected(settings, server)) || AccountKey(settings, server, independent) != account)
            throw new RecordingAccessException("This recording could not be authorized for the current hosted account.");
    }

    // Returns a lower bound for a subsequent metadata lookup on Standard.
    internal static async Task<DateTimeOffset?> VerifyAsync(ISettingsService settings, ISubscriptionService subscription,
        string server, DateTimeOffset? recordedAt, RecordingAccessContext context, bool currentLive,
        bool requireAccount, CancellationToken token, TimeProvider? clock = null, bool lookup = false, bool independent = false)
    {
        if (!context.Protected && !HostedServerRules.RequiresApiFirewallCredentials(settings, server))
            return null;

        var account = AccountKey(settings, server, independent);
        if ((!independent && !IsSelected(settings, server)) || account == null || requireAccount && context.Account == null ||
            context.Account != null && context.Account != account)
            throw new RecordingAccessException("This recording could not be authorized for the current hosted account.");

        if (currentLive && context.Origin == RecordingOrigin.Live && !lookup)
            return null;

        var grant = await VerifyHistoryAsync(settings, subscription, server, token, clock, protectedSource: true, independent: independent)
            .ConfigureAwait(false);
        if (grant.Account != account)
            throw VerificationRequired();
        if (!grant.Allows(recordedAt))
            throw new RecordingAccessException("This recording is outside the Standard 24-hour history window.");
        return grant.Cutoff;
    }
}
