using JoesScanner.Models;

namespace JoesScanner.Services;

internal static class CallCardLayoutStore
{
    internal const string Key = "call_card_layout";

    public static CallCardLayout Read() => CallCardLayout.Deserialize(AppStateStore.GetString(Key));

    public static void Write(CallCardLayout layout) => AppStateStore.SetString(Key, layout.Serialize());
}
