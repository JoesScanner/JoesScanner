#if ANDROID
using global::Android.Content;

namespace JoesScanner.Platforms.Android.Services
{
    // Handles explicit media notification actions. Route changes use a runtime receiver.
    // Maps Play to Connect, Stop to Disconnect via AndroidMediaCenter.
    [global::Android.Content.BroadcastReceiverAttribute(Enabled = true, Exported = false)]
    [global::Android.App.IntentFilterAttribute(new[]
    {
        MediaActionReceiver.ActionPlay,
        MediaActionReceiver.ActionStop,
        MediaActionReceiver.ActionNext,
        MediaActionReceiver.ActionPrevious
    })]
    public sealed class MediaActionReceiver : global::Android.Content.BroadcastReceiver
    {
        public const string ActionPlay = "com.joesscanner.action.PLAY";
        public const string ActionStop = "com.joesscanner.action.STOP";
        public const string ActionNext = "com.joesscanner.action.NEXT";
        public const string ActionPrevious = "com.joesscanner.action.PREV";

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context is null || intent?.Action is null)
                return;

            var action = intent.Action;

            try
            {
                AndroidMediaCenter.HandleAction(intent.GetLongExtra(AndroidMediaCenter.ExtraMonitoringRun, 0), action);
            }
            catch
            {
                // Swallow exceptions in receiver to avoid process-kill risk.
            }
        }
    }
}
#endif
