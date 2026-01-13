#if ANDROID
using System;
using global::Android.Content;

namespace JoesScanner.Platforms.Android.Services
{
    // Handles media notification actions and "becoming noisy" events.
    // Maps Play to Connect, Stop to Disconnect via AndroidMediaCenter.
    [global::Android.Content.BroadcastReceiverAttribute(Enabled = true, Exported = false)]
    [global::Android.App.IntentFilterAttribute(new[]
    {
        MediaActionReceiver.ActionPlay,
        MediaActionReceiver.ActionStop,
        MediaActionReceiver.ActionNext,
        MediaActionReceiver.ActionPrevious,
        MediaActionReceiver.ActionAudioBecomingNoisy
    })]
    public sealed class MediaActionReceiver : global::Android.Content.BroadcastReceiver
    {
        public const string ActionPlay = "com.joesscanner.action.PLAY";
        public const string ActionStop = "com.joesscanner.action.STOP";
        public const string ActionNext = "com.joesscanner.action.NEXT";
        public const string ActionPrevious = "com.joesscanner.action.PREV";

        // Must be a literal for attribute usage.
        public const string ActionAudioBecomingNoisy = "android.media.AUDIO_BECOMING_NOISY";

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context is null || intent?.Action is null)
                return;

            var action = intent.Action;

            // Treat route changes (headphones unplugged / BT disconnect) as Stop/Disconnect.
            if (string.Equals(action, ActionAudioBecomingNoisy, StringComparison.Ordinal))
                action = ActionStop;

            try
            {
                // IMPORTANT: AndroidMediaCenter.HandleAction expects (Context, action)
                AndroidMediaCenter.HandleAction(context, action);
            }
            catch
            {
                // Swallow exceptions in receiver to avoid process-kill risk.
            }
        }
    }
}
#endif
