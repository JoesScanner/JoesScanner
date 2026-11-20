using JoesScanner.Models;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

internal static class MobileAudioPreferencesStore
{
    internal const string LegacyMixKey = "mobile_mix_audio_with_other_apps";
    internal const string ModeKey = "mobile_audio_mode";
    internal const string DisconnectKey = "mobile_audio_on_disconnect";

    internal static MobileAudioPreferences Read(bool isIos) =>
        AppStateStore.InTransaction(transaction =>
        {
            var rawMode = AppStateStore.Read(transaction, ModeKey);
            var mode = Parse(rawMode, MobileAudioPreferences.Default.Mode);
            var disconnect = Parse(AppStateStore.Read(transaction, DisconnectKey), MobileAudioPreferences.Default.OnDisconnect);
            if (string.IsNullOrEmpty(rawMode))
            {
                // Read the stored row directly. The old getter substituted true for a missing row.
                var legacy = AppStateStore.Read(transaction, LegacyMixKey);
                if (bool.TryParse(legacy, out var mix) || legacy is "0" or "1")
                {
                    mix |= legacy == "1";
                    mode = !mix ? MobileAudioMode.Exclusive : isIos ? MobileAudioMode.MixNormally : MobileAudioMode.MixAndDuck;
                }
                Write(transaction, new(mode, disconnect));
            }

            return new MobileAudioPreferences(mode, disconnect);
        });

    internal static void Write(MobileAudioPreferences preferences) =>
        AppStateStore.InTransaction(transaction =>
        {
            Write(transaction, preferences);
            return true;
        }, durable: true);

    internal static void Write(SqliteTransaction transaction, MobileAudioPreferences preferences)
    {
        AppStateStore.Write(transaction, ModeKey, preferences.Mode.ToString());
        AppStateStore.Write(transaction, DisconnectKey, preferences.OnDisconnect.ToString());
    }

    private static T Parse<T>(string? raw, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(raw, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
}
