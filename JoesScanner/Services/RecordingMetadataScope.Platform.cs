using JoesScanner.Helpers;

namespace JoesScanner.Services;

internal sealed partial class DirectoryRecorderPolicy
{
    private RecorderOperation _hostedAccount = new();

    // Both callers hold the settings account-identity lock. SQLite commit only takes
    // the returned lifetime lock, so it never waits on a settings write transaction.
    internal RecorderOperation CaptureHostedAccount(ISettingsService settings, long version)
    {
        RecorderOperation? captured = null;
        if (!settings.TryApplyAccountIdentity(version, () => captured = _hostedAccount))
            throw RecordingPlaybackAccess.VerificationRequired();
        return captured!;
    }

    internal void InvalidateHostedAccount()
    {
        _hostedAccount.Retire();
        _hostedAccount = new();
    }
}

public sealed partial class RecordingMetadataScope
{
    internal static RecordingMetadataScope Capture(ISettingsService settings, Guid? recorderId, string endpoint,
        bool hosted, string? account, long? accountVersion, RecorderOperation? operation)
    {
        operation?.Validate();
        var identity = hosted ? DirectoryRecorderPolicy.For(settings).CaptureHostedAccount(settings,
            accountVersion ?? throw RecordingPlaybackAccess.VerificationRequired()) : null;
        return new(recorderId, endpoint, hosted, account, CommitOwned);

        void CommitOwned(Action commit, CancellationToken token)
        {
            void ApplyRecorder()
            {
                if (operation == null)
                {
                    token.ThrowIfCancellationRequested();
                    commit();
                }
                else
                    operation.Apply(commit, token);
            }
            if (identity == null)
                ApplyRecorder();
            else
                identity.Apply(ApplyRecorder, token);
        }
    }

    internal static RecordingMetadataScope ForRecorder(ISettingsService settings, Guid? id, string endpoint,
        RecorderOperation? operation)
    {
        if (id is { } profile)
            operation ??= DirectoryRecorderPolicy.For(settings).Capture(profile, new Uri(endpoint));
        var hosted = HostedServerRules.RequiresApiFirewallCredentials(settings, endpoint);
        var version = hosted ? settings.ReadAccountSession().AccountVersion : (long?)null;
        var account = hosted ? RecordingPlaybackAccess.AccountKey(settings, endpoint, independent: id.HasValue)
            : id is { } recorder ? RecordingPlaybackAccess.RecorderAccountKey(settings, recorder, endpoint) : null;
        return Capture(settings, id, endpoint, hosted, account, version, operation);
    }
}
