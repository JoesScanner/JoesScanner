namespace JoesScanner.Models;

public enum MobileAudioMode
{
    // Values match the order of the Settings picker.
    MixNormally = 0,
    MixAndDuck = 1,
    Exclusive = 2
}

public enum AudioDisconnectionBehavior
{
    PausePlayback = 0,
    ContinueThroughPhoneSpeaker = 1
}

public sealed record MobileAudioPreferences(MobileAudioMode Mode, AudioDisconnectionBehavior OnDisconnect)
{
    public static MobileAudioPreferences Default { get; } =
        new(MobileAudioMode.MixAndDuck, AudioDisconnectionBehavior.PausePlayback);
}
