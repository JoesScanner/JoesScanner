using System.ComponentModel;
using JoesScanner.Models;

namespace JoesScanner.Views.Controls;

public sealed class LiveTranscriptLabel : Label
{
    private const string AnimationName = "TranscriptCorrection";
    private readonly TranscriptFeedbackState _feedback = new();
    private bool _allowFeedback;
    private bool _loaded;

    public LiveTranscriptLabel()
    {
        Loaded += (_, _) =>
        {
            _loaded = true;
            ObserveCard();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            ObserveCard();
        };
        HandlerChanging += (_, _) =>
        {
            _loaded = false;
            ObserveCard();
        };
    }

    internal void ShowCorrectionFeedback(bool allowed)
    {
        _allowFeedback = allowed;
        ObserveCard();
    }

    internal bool IsInVisibleList()
    {
        var inList = false;
        for (Element? view = this; view != null; view = view.Parent)
        {
            if (view is VisualElement { IsVisible: false })
                return false;
            inList |= view is CollectionView;
        }
        return inList;
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        ObserveCard();
    }

    private void ObserveCard()
    {
        var card = _allowFeedback && _loaded ? BindingContext as CallItem : null;
        var previous = _feedback.Card;
        if (ReferenceEquals(previous, card))
            return;
        if (previous != null)
            previous.PropertyChanged -= OnCardChanged;
        _feedback.Bind(card);
        ClearTint();
        if (card != null)
            card.PropertyChanged += OnCardChanged;
    }

    internal void CancelCorrection()
    {
        _feedback.Cancel();
        ClearTint();
    }

    private void ClearTint()
    {
        this.AbortAnimation(AnimationName);
        BackgroundColor = Colors.Transparent;
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CallItem.TranscriptionCorrectionVersion) || sender is not CallItem card)
            return;
        var version = card.TranscriptionCorrectionVersion;
        if (!Dispatcher.IsDispatchRequired)
        {
            Highlight(card, version);
            return;
        }
        var label = new WeakReference<LiveTranscriptLabel>(this);
        Dispatcher.Dispatch(() =>
        {
            if (label.TryGetTarget(out var target))
                target.Highlight(card, version);
        });
    }

    private void Highlight(CallItem card, long version)
    {
        if (!IsInVisibleList() || _feedback.Start(card, version, ReducedMotion()) is not { } pulse)
            return;
        this.AbortAnimation(AnimationName);
        Paint(pulse, 1);
        if (pulse.Animated)
        {
            new Animation(strength => Paint(pulse, strength), 1, 0).Commit(this, AnimationName,
                length: TranscriptFeedbackState.DurationMilliseconds, easing: Easing.CubicOut,
                finished: (_, _) => Paint(pulse, 0));
        }
        else
        {
            var label = new WeakReference<LiveTranscriptLabel>(this);
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(TranscriptFeedbackState.DurationMilliseconds), () =>
            {
                if (label.TryGetTarget(out var target))
                    target.Paint(pulse, 0);
            });
        }
    }

    private void Paint(TranscriptPulse pulse, double strength)
    {
        if (_feedback.IsCurrent(pulse))
            BackgroundColor = strength <= 0 ? Colors.Transparent : Color.FromRgba(96, 165, 250, (int)(56 * strength));
    }

    private static bool ReducedMotion()
    {
        try
        {
#if WINDOWS
            return !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
#elif ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                return !global::Android.Animation.ValueAnimator.AreAnimatorsEnabled();
            var resolver = global::Android.App.Application.Context.ContentResolver;
            return global::Android.Provider.Settings.Global.GetFloat(resolver,
                global::Android.Provider.Settings.Global.AnimatorDurationScale, 1) == 0;
#elif IOS || MACCATALYST
            return UIKit.UIAccessibility.IsReduceMotionEnabled;
#else
            return true;
#endif
        }
        catch
        {
            return true;
        }
    }
}
