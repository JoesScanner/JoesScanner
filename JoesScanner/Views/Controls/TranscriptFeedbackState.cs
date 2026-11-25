using JoesScanner.Models;

namespace JoesScanner.Views.Controls;

internal readonly record struct TranscriptPulse(long Generation, bool Animated);

// One instance per realized label. A rebind or newer correction retires
// callbacks from an earlier presentation, including a previous use of the cell.
internal sealed class TranscriptFeedbackState
{
    internal const int DurationMilliseconds = 650;
    private long _generation;
    private long _seenCorrection;
    internal CallItem? Card { get; private set; }

    internal void Bind(CallItem? card)
    {
        Card = card;
        Cancel();
    }

    internal void Cancel()
    {
        _generation++;
        _seenCorrection = Card?.TranscriptionCorrectionVersion ?? 0;
    }

    internal TranscriptPulse? Start(CallItem card, long correction, bool reducedMotion)
    {
        if (!ReferenceEquals(card, Card) || correction <= _seenCorrection)
            return null;
        _seenCorrection = correction;
        return new(++_generation, Animated: !reducedMotion);
    }

    internal bool IsCurrent(TranscriptPulse pulse) => pulse.Generation == _generation;
}
