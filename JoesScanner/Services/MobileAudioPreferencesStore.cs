using JoesScanner.Models;

namespace JoesScanner.Services;

internal static class MobileAudioPreferencesStore
{
    internal const string LegacyMixKey = "mobile_mix_audio_with_other_apps";
    private const string ModeKey = "mobile_audio_mode";
    private const string DisconnectKey = "mobile_audio_on_disconnect";
    private static readonly object Gate = new();

    internal static MobileAudioPreferences Read(bool isIos)
    {
        lock (Gate)
        {
            var rawMode = AppStateStore.GetString(ModeKey);
            var mode = Parse(rawMode, MobileAudioPreferences.Default.Mode);
            if (rawMode.Length == 0)
            {
                // Read the stored row directly. The old getter substituted true for a missing row.
                var legacy = AppStateStore.GetString(LegacyMixKey);
                if (bool.TryParse(legacy, out var mix))
                    mode = !mix ? MobileAudioMode.Exclusive : isIos ? MobileAudioMode.MixNormally : MobileAudioMode.MixAndDuck;

                AppStateStore.SetString(ModeKey, mode.ToString());
            }

            return new(mode, Parse(AppStateStore.GetString(DisconnectKey), MobileAudioPreferences.Default.OnDisconnect));
        }
    }

    internal static void Write(MobileAudioPreferences preferences)
    {
        lock (Gate)
        {
            AppStateStore.SetString(ModeKey, preferences.Mode.ToString());
            AppStateStore.SetString(DisconnectKey, preferences.OnDisconnect.ToString());
        }
    }

    private static T Parse<T>(string raw, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(raw, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
}
