#if ANDROID
namespace JoesScanner.Platforms.Android.Services
{
    // Simple static bridge so Android services can invoke the current app handlers.
    // The service runs in the same process, but it cannot safely resolve DI from a static context.
    public static class MediaCommandBridge
    {
        public static Func<Task>? OnPlay { get; set; }
        public static Func<Task>? OnStop { get; set; }
        public static Func<Task>? OnNext { get; set; }
        public static Func<Task>? OnPrevious { get; set; }

        public static void Clear()
        {
            OnPlay = null;
            OnStop = null;
            OnNext = null;
            OnPrevious = null;
        }
    }
}
#endif
