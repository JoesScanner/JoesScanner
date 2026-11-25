namespace JoesScanner.Services;

internal static class PlaybackAudioCachePreferences
{
    private const string Key = "playback_audio_cache_mib";

    internal static int Read()
    {
        try
        {
            var stored = AppStateStore.GetLong(Key, PlaybackAudioCache.DefaultLimitMiB);
            if (stored is > 0 and <= int.MaxValue && PlaybackAudioCache.LimitsMiB.Contains((int)stored))
                return (int)stored;
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Audio cache preference could not be read ({ex.GetType().Name}).");
        }
        return PlaybackAudioCache.DefaultLimitMiB;
    }

    internal static void Save(int limitMiB) => AppStateStore.SetLong(Key, limitMiB);
}
