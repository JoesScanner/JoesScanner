using JoesScanner.Models;

namespace JoesScanner.Services;

internal enum LiveAudioSourceMode { AllEnabledServers, SelectedOnly }

internal enum LiveAudioSourceKind { None, All, Recorder, Favorites }

internal readonly record struct LiveAudioSource(LiveAudioSourceKind Kind, Guid? RecorderId = null)
{
    internal static LiveAudioSource All => new(LiveAudioSourceKind.All);
    internal static LiveAudioSource Favorites => new(LiveAudioSourceKind.Favorites);
    internal static LiveAudioSource Recorder(Guid id) => new(LiveAudioSourceKind.Recorder, id);

    internal bool IncludesRecorder(Guid? id) =>
        Kind is LiveAudioSourceKind.All or LiveAudioSourceKind.Favorites ||
        Kind == LiveAudioSourceKind.Recorder && RecorderId == id;

    internal static LiveAudioSource ReadSelection()
    {
        try
        {
            var stored = AppStateStore.GetString("live_selected_audio_source");
            return stored == "favorites" ? Favorites : Guid.TryParseExact(stored, "N", out var id) ? Recorder(id) : default;
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Live audio source could not be read ({ex.GetType().Name}).");
            return default;
        }
    }

    internal void SaveSelection() => AppStateStore.SetString("live_selected_audio_source",
        Kind == LiveAudioSourceKind.Favorites ? "favorites" : RecorderId?.ToString("N") ?? string.Empty);
}

internal sealed record LiveAutomaticAudioFilter(long AfterArrival = 0, long RecoveredSinceUtcTicks = 0,
    IReadOnlyDictionary<(Guid ServerId, FavoriteTalkgroupIdentity Identity), long>? FavoriteEdges = null)
{
    internal bool IncludesFavorite(Guid? serverId, CallCardMetadata? metadata, long arrival) =>
        FavoriteEdges == null || serverId is { } id && FavoriteTalkgroupIdentity.From(metadata) is { } favorite &&
        FavoriteEdges.TryGetValue((id, favorite), out var edge) && arrival > edge;

    internal bool Includes(CallItem item)
    {
        var arrival = item.LiveArrivalSequence ?? 0;
        return arrival > AfterArrival && IncludesFavorite(item.ServerId, item.CardMetadata, arrival) &&
            (RecoveredSinceUtcTicks == 0 || item.RecordingAccess?.Origin == RecordingOrigin.Live ||
             item.RecordingTimeUtc?.UtcTicks >= RecoveredSinceUtcTicks);
    }
}
