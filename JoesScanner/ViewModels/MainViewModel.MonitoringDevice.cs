using JoesScanner.Models;
using JoesScanner.Services;

namespace JoesScanner.ViewModels;

public partial class MainViewModel
{
    internal MonitoringDeviceService MonitoringDevice { get; }

    private void ObserveTransmission(DurableLiveQueue.Run scope, CallItem call, LiveCallSnapshot snapshot,
        DurableLiveQueue.Observation observation, CancellationToken connection)
    {
        if (!MonitoringDevice.Options.TransmissionNotifications || !observation.FirstAdmission || scope.ServerId is not { } server)
            return;

        try
        {
            var allowed = _recorderRuns.TryGetValue(scope.Owner, out var recorder) && MayUseNetwork(recorder.Connection.Profile);
            var metadata = snapshot.Metadata;
            MonitoringDevice.OnRecording(new NotificationRecording(server, TalkgroupKey.From(server, metadata),
                Favorites.Contains(server, FavoriteTalkgroupIdentity.From(metadata)),
                call.RecordingTimeUtc, metadata.DurationSeconds ?? call.CallDurationSeconds, observation.FirstAdmission,
                call.IsCurrentTransmission && !observation.IsRecoveryBacklog),
                allowed, connection);
        }
        catch (Exception error)
        {
            AppLog.Basic(() => $"Transmission notification could not be prepared ({DiagnosticRedaction.Describe(error)}).");
        }
    }

    internal NotificationTalkgroup[] RecentNotificationTalkgroups()
    {
        var names = _profiles.ToDictionary(profile => profile.Id, profile => profile.Name);
        return Calls.Select(call =>
        {
            if (TalkgroupKey.From(call.ServerId, call.CardMetadata) is not { } key || !names.TryGetValue(key.ServerId, out var recorder))
                return null;
            var label = string.Join(" · ", new[] { recorder, call.CardMetadata?.System, call.CardMetadata?.Site, call.CardMetadata?.Talkgroup }
                .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase)));
            return new NotificationTalkgroup(key, FavoriteValue.CleanLabel(label));
        }).OfType<NotificationTalkgroup>().DistinctBy(item => item.Key).OrderBy(item => item.Label).ToArray();
    }
}
