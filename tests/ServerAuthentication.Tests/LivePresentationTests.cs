using System.Collections.ObjectModel;
using JoesScanner.Models;
using JoesScanner.ViewModels;
using JoesScanner.Views.Controls;

internal static partial class MobileRecoveryTests
{
    internal static Task LivePinsRespectDisplayProjection()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var matchingId = new CallItem { ServerId = b, BackendId = "same-id" };
        var active = new CallItem { ServerId = a, BackendId = "same-id", IsPlaying = true };
        var source = new ObservableCollection<CallItem>(Enumerable.Range(0, LiveCallDisplay.CardLimit - 1)
            .Select(index => new CallItem { ServerId = a, BackendId = index.ToString() }).Prepend(matchingId));
        var favorites = new HashSet<CallItem> { matchingId };
        var display = new LiveCallDisplay(source, favorite: favorites.Contains);
        display.SetServers([a, b]);
        display.Observe();
        var reader = display.CombinedReader;
        reader.SetVisible(true);
        reader.ObserveViewport(3, -31, userScroll: true);
        display.RecordVisibleAdmission(source[0]);
        var initialAnchor = reader.ResolveAnchor();
        var order = source.ToArray();
        var aCards = display.ForServer(a);

        Check(source.Count == LiveCallDisplay.CardLimit && !source.Contains(active) && !aCards.Contains(active) &&
            ReferenceEquals(display.PinFor(active), active) && ReferenceEquals(display.PinFor(active, recorder: a), active) &&
            display.PinFor(active, recorder: b) == null && display.PinFor(active, favoritesOnly: true) == null,
            "A Live clip outside the display cap must pin only its canonical reference and matching recorder, even with an equal backend ID.");

        favorites.Add(active);
        display.RefreshFavorites();
        Check(ReferenceEquals(display.PinFor(active, favoritesOnly: true), active) && !display.Favorites.Contains(active),
            "An off-window active clip can pin in Favorites as soon as its recorder-scoped membership allows.");
        favorites.Remove(active);
        display.RefreshFavorites();
        Check(display.PinFor(active, favoritesOnly: true) == null,
            "Removing membership must hide the active clip from Favorites without touching the active clip.");

        display.SetSeparate(true);
        display.SetServers([b, a]);
        display.Select(b);
        Check(display.SelectedServer == b && display.PinFor(active, recorder: b) == null,
            "Tab positions and the other recorder's identical recording ID must not select a pin.");
        favorites.Add(active);
        display.SetServers([b]);
        Check(display.PinFor(active, recorder: a) == null && display.PinFor(active, favoritesOnly: true) == null &&
            ReferenceEquals(display.PinFor(active), active),
            "A retired recorder panel and its Favorites projection must hide their pin while Combined can still identify the playing clip.");

        Check(display.PinFor(null) == null && reader.ResolveAnchor() == initialAnchor && !reader.IsFollowing &&
            reader.UnseenCount == 1 && source.SequenceEqual(order),
            "Changing or clearing the pin must preserve New Calls, the reader anchor, and the canonical collection.");
        display.Detach();
        return Task.CompletedTask;
    }

    internal static Task LiveTranscriptCorrectionsAreVersioned()
    {
        var state = new LiveCallState(RecorderA, "live");
        var card = new CallItem { LiveState = state, BackendId = state.Id, Transcription = CallItem.MissingTranscriptionMessage };
        var snapshot = new LiveCallSnapshot(new(), null, 0);
        var corrections = new List<long>();
        card.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(CallItem.TranscriptionCorrectionVersion))
                corrections.Add(card.TranscriptionCorrectionVersion);
        };

        state.Observe(snapshot with { Transcription = "First meaningful line." }, 2);
        Check(card.ApplyLiveUpdate() && corrections.Count == 0,
            "The first usable transcription on a pending card is ordinary content, with no correction event.");
        state.Observe(snapshot with { Transcription = "Corrected street." }, 4);
        Check(card.ApplyLiveUpdate(), "The first correction must update the same card.");
        state.Observe(snapshot with { Transcription = "  Corrected  street. " }, 5);
        Check(!card.ApplyLiveUpdate(), "An observation with equivalent normalized text must not pulse again.");

        var deferred = new LiveCallState(RecorderA, state.Id);
        deferred.Observe(snapshot with { Transcription = "Stale HTTP text.", Metadata = new() { DurationSeconds = 2 } }, 3);
        state.MergeRecorderState(deferred);
        Check(!card.ApplyLiveUpdate() && card.Transcription == "Corrected street." && card.CallDurationSeconds == 2,
            "Merging a detached or rehydrated state must preserve its earlier transcript version even when it fills metadata.");
        var newer = new LiveCallState(RecorderA, state.Id);
        newer.Observe(snapshot with { Transcription = "Corrected street and unit." }, 6);
        state.MergeRecorderState(newer);
        Check(card.ApplyLiveUpdate() && corrections.SequenceEqual([1L, 2L]),
            "Two genuine corrections on the same recording must each publish their own event.");

        state.Observe(snapshot with { Transcription = "Older direct update." }, 4);
        state.Observe(snapshot with { Transcription = CallItem.MissingTranscriptionMessage }, 10);
        state.MergeRecorderState(deferred);
        Check(!card.ApplyLiveUpdate() && card.Transcription == "Corrected street and unit." && corrections.Count == 2,
            "Older observations and missing text must never undo current content or publish correction feedback.");
        return Task.CompletedTask;
    }

    internal static Task LiveTranscriptFeedbackLifecycle()
    {
        var source = new LiveCallState(RecorderA, "shared");
        var card = new CallItem { LiveState = source, BackendId = source.Id, Transcription = "Initial text." };
        var replacement = new CallItem { BackendId = source.Id, ServerId = Guid.NewGuid() };
        var feedback = new TranscriptFeedbackState();
        feedback.Bind(card);
        Check(feedback.Start(card, 0, reducedMotion: false) == null, "Opening or rebinding a card must have no pulse.");

        Correct("First correction.", 1);
        var first = feedback.Start(card, card.TranscriptionCorrectionVersion, reducedMotion: false);
        Check(first is { Animated: true } animated && feedback.IsCurrent(animated), "Animated feedback should be allowed by default.");
        Correct("Second correction.", 2);
        var second = feedback.Start(card, card.TranscriptionCorrectionVersion, reducedMotion: true);
        Check(second is { Animated: false } still && feedback.IsCurrent(still) && !feedback.IsCurrent(first!.Value),
            "A newer correction should retire the old fade and use a brief steady tint under reduced motion.");
        Check(feedback.Start(card, card.TranscriptionCorrectionVersion, reducedMotion: true) == null,
            "Duplicate callback publication must not extend the tint.");

        feedback.Bind(replacement);
        Check(!feedback.IsCurrent(second!.Value) && feedback.Start(card, 3, reducedMotion: false) == null,
            "Recycling a label across recorders with the same ID must retire its tint and deferred events.");
        feedback.Bind(card);
        Check(feedback.Start(card, card.TranscriptionCorrectionVersion, reducedMotion: false) == null,
            "Returning to the same recording must not replay its old correction.");
        Correct("Third correction.", 3);
        var third = feedback.Start(card, card.TranscriptionCorrectionVersion, reducedMotion: false)!.Value;
        feedback.Cancel();
        Check(!feedback.IsCurrent(third) && feedback.Start(card, card.TranscriptionCorrectionVersion, reducedMotion: false) == null,
            "Stop or a view refresh must retire pending visual callbacks for the current card.");
        feedback.Bind(null);
        Check(feedback.Start(card, 4, reducedMotion: false) == null,
            "An unloaded presentation cannot receive a pulse.");
        return Task.CompletedTask;

        void Correct(string text, long version)
        {
            source.Observe(new(new(), text, 0), version);
            Check(card.ApplyLiveUpdate(), "A fixture correction should apply.");
        }
    }
}
