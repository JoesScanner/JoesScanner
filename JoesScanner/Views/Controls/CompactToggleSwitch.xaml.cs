using System;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace JoesScanner.Views.Controls;

public partial class CompactToggleSwitch : ContentView
{
    public static readonly BindableProperty IsToggledProperty = BindableProperty.Create(
        nameof(IsToggled),
        typeof(bool),
        typeof(CompactToggleSwitch),
        false,
        BindingMode.TwoWay,
        propertyChanged: OnIsToggledChanged);

    public static readonly BindableProperty OnColorProperty = BindableProperty.Create(
        nameof(OnColor),
        typeof(Color),
        typeof(CompactToggleSwitch),
        defaultValue: null,
        propertyChanged: OnVisualPropertyChanged);

    public static readonly BindableProperty OffColorProperty = BindableProperty.Create(
        nameof(OffColor),
        typeof(Color),
        typeof(CompactToggleSwitch),
        defaultValue: null,
        propertyChanged: OnVisualPropertyChanged);

    public static readonly BindableProperty ThumbColorProperty = BindableProperty.Create(
        nameof(ThumbColor),
        typeof(Color),
        typeof(CompactToggleSwitch),
        defaultValue: Colors.White,
        propertyChanged: OnVisualPropertyChanged);

    public static readonly BindableProperty AnimateProperty = BindableProperty.Create(
        nameof(Animate),
        typeof(bool),
        typeof(CompactToggleSwitch),
        true);

    public CompactToggleSwitch()
    {
        InitializeComponent();

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (!IsEnabled)
            {
                return;
            }

            IsToggled = !IsToggled;
        };
        GestureRecognizers.Add(tap);

        Track.SizeChanged += (_, _) => UpdateVisual(animated: false);
        Thumb.SizeChanged += (_, _) => UpdateVisual(animated: false);

        UpdateVisual(animated: false);
    }

    public bool IsToggled
    {
        get => (bool)GetValue(IsToggledProperty);
        set => SetValue(IsToggledProperty, value);
    }

    public Color OnColor
    {
        get => (Color)GetValue(OnColorProperty);
        set => SetValue(OnColorProperty, value);
    }

    public Color OffColor
    {
        get => (Color)GetValue(OffColorProperty);
        set => SetValue(OffColorProperty, value);
    }

    public Color ThumbColor
    {
        get => (Color)GetValue(ThumbColorProperty);
        set => SetValue(ThumbColorProperty, value);
    }

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    protected override void OnPropertyChanged(string propertyName = null)
    {
        base.OnPropertyChanged(propertyName);

        if (propertyName == IsEnabledProperty.PropertyName)
        {
            UpdateEnabledVisual();
        }
    }

    private static void OnIsToggledChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is CompactToggleSwitch s)
        {
            s.UpdateVisual(animated: s.Animate);
        }
    }

    private static void OnVisualPropertyChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is CompactToggleSwitch s)
        {
            s.UpdateVisual(animated: false);
        }
    }

    private void UpdateEnabledVisual()
    {
        Opacity = IsEnabled ? 1.0 : 0.55;
    }

    private void UpdateVisual(bool animated)
    {
        UpdateEnabledVisual();

        var resolvedOn = OnColor ?? ResolvePrimaryColorFallback();
        var resolvedOff = OffColor ?? ResolveOffColorFallback();

        Track.BackgroundColor = IsToggled ? resolvedOn : resolvedOff;
        Thumb.BackgroundColor = ThumbColor ?? Colors.White;

        var trackWidth = Track.Width;
        var thumbWidth = Thumb.Width;

        if (trackWidth <= 0 || thumbWidth <= 0)
        {
            return;
        }

        // Leave a tiny inset on the right to avoid the thumb being clipped by fractional layout
        // rounding on some platforms.
        var padding = 2.0;
        var endInset = 1.0;
        var maxX = Math.Max(0, trackWidth - (padding * 2) - thumbWidth - endInset);
        var targetX = IsToggled ? maxX : 0;

        if (!animated)
        {
            Thumb.TranslationX = targetX;
            return;
        }

        _ = Thumb.TranslateTo(targetX, 0, 80, Easing.CubicOut);
    }

    private static Color ResolvePrimaryColorFallback()
    {
        try
        {
            if (Application.Current?.Resources != null && Application.Current.Resources.TryGetValue("Primary", out var v) && v is Color c)
            {
                return c;
            }
        }
        catch
        {
            // Ignore resource lookup failures.
        }

        return Color.FromArgb("#2563eb");
    }

    private static Color ResolveOffColorFallback()
    {
        return AppThemeColor(Color.FromArgb("#334155"), Color.FromArgb("#1f2937"));
    }

    private static Color AppThemeColor(Color light, Color dark)
    {
        var theme = Application.Current?.RequestedTheme;
        return theme == AppTheme.Dark ? dark : light;
    }
}
