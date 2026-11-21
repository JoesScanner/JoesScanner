using System.Globalization;
using JoesScanner.Models;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services;

internal static class MobileAudioPreferencesStore
{
    internal const string LegacyMixKey = "mobile_mix_audio_with_other_apps";
    internal const string ModeKey = "mobile_audio_mode";
    internal const string DisconnectKey = "mobile_audio_on_disconnect";
    internal const string RecoveryKey = "audio_interruption_recovery";
    internal const string CustomRecoveryKey = "audio_interruption_custom_minutes";

    internal static MobileAudioPreferences Read(bool isIos) =>
        AppStateStore.InTransaction(transaction =>
        {
            var rawMode = AppStateStore.Read(transaction, ModeKey);
            var mode = Parse(rawMode, MobileAudioPreferences.Default.Mode);
            var disconnect = Parse(AppStateStore.Read(transaction, DisconnectKey), MobileAudioPreferences.Default.OnDisconnect);
            var option = Parse(AppStateStore.Read(transaction, RecoveryKey), InterruptionRecoveryPreference.Default.Option);
            var custom = int.TryParse(AppStateStore.Read(transaction, CustomRecoveryKey), NumberStyles.None, CultureInfo.InvariantCulture,
                out var minutes) && InterruptionRecoveryPreference.AllowsCustomMinutes(minutes) ? minutes : InterruptionRecoveryPreference.Default.CustomMinutes;
            if (string.IsNullOrEmpty(rawMode))
            {
                // Read the stored row directly. The old getter substituted true for a missing row.
                var legacy = AppStateStore.Read(transaction, LegacyMixKey);
                if (bool.TryParse(legacy, out var mix) || legacy is "0" or "1")
                {
                    mix |= legacy == "1";
                    mode = !mix ? MobileAudioMode.Exclusive : isIos ? MobileAudioMode.MixNormally : MobileAudioMode.MixAndDuck;
                }
            }

            var preferences = new MobileAudioPreferences(mode, disconnect) { InterruptionRecovery = new(option, custom) };
            if (string.IsNullOrEmpty(rawMode))
                Write(transaction, preferences);
            return preferences;
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
        AppStateStore.Write(transaction, RecoveryKey, preferences.InterruptionRecovery.Option.ToString());
        AppStateStore.Write(transaction, CustomRecoveryKey, preferences.InterruptionRecovery.CustomMinutes.ToString(CultureInfo.InvariantCulture));
    }

    private static T Parse<T>(string? raw, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(raw, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
}
