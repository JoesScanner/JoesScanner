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
    public InterruptionRecoveryPreference InterruptionRecovery { get; init; } = InterruptionRecoveryPreference.Default;

    public static MobileAudioPreferences Default { get; } =
        new(MobileAudioMode.MixAndDuck, AudioDisconnectionBehavior.PausePlayback);
}

public enum InterruptionRecoveryOption
{
    Immediately,
    OneMinute,
    TwoMinutes,
    FiveMinutes,
    TenMinutes,
    FifteenMinutes,
    ThirtyMinutes,
    SixtyMinutes,
    Custom,
    Never
}

public readonly record struct InterruptionRecoveryPreference(InterruptionRecoveryOption Option, int CustomMinutes = 5)
{
    public const int MinimumCustomMinutes = 1;
    public const int MaximumCustomMinutes = 1440;

    public static InterruptionRecoveryPreference Default => new(InterruptionRecoveryOption.FiveMinutes);

    public static bool AllowsCustomMinutes(int minutes) => minutes is >= MinimumCustomMinutes and <= MaximumCustomMinutes;

    public bool ShouldJump(TimeSpan elapsed) => Option != InterruptionRecoveryOption.Never && elapsed >= TimeSpan.FromMinutes(Option switch
    {
        InterruptionRecoveryOption.Immediately => 0,
        InterruptionRecoveryOption.OneMinute => 1,
        InterruptionRecoveryOption.TwoMinutes => 2,
        InterruptionRecoveryOption.TenMinutes => 10,
        InterruptionRecoveryOption.FifteenMinutes => 15,
        InterruptionRecoveryOption.ThirtyMinutes => 30,
        InterruptionRecoveryOption.SixtyMinutes => 60,
        InterruptionRecoveryOption.Custom when AllowsCustomMinutes(CustomMinutes) => CustomMinutes,
        _ => 5
    });
}
