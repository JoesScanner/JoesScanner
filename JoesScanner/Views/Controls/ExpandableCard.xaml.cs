using System;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace JoesScanner.Views.Controls
{
    public partial class ExpandableCard : ContentView
    {
        public ExpandableCard()
        {
            InitializeComponent();
            UpdateDerivedState();
        }

        public static readonly BindableProperty TitleProperty =
            BindableProperty.Create(nameof(Title), typeof(string), typeof(ExpandableCard), string.Empty, propertyChanged: OnAnyChanged);

        public static readonly BindableProperty SubtitleProperty =
            BindableProperty.Create(nameof(Subtitle), typeof(string), typeof(ExpandableCard), string.Empty, propertyChanged: OnAnyChanged);

        public static readonly BindableProperty IsExpandedProperty =
            // Default collapsed so the Settings page loads compact.
            BindableProperty.Create(nameof(IsExpanded), typeof(bool), typeof(ExpandableCard), false, propertyChanged: OnAnyChanged);

        public static readonly BindableProperty CardContentProperty =
            BindableProperty.Create(nameof(CardContent), typeof(View), typeof(ExpandableCard), null);

        public static readonly BindableProperty HeaderRightContentProperty =
            BindableProperty.Create(nameof(HeaderRightContent), typeof(View), typeof(ExpandableCard), null, propertyChanged: OnAnyChanged);

        public static readonly BindableProperty CardPaddingProperty =
            BindableProperty.Create(nameof(CardPadding), typeof(Thickness), typeof(ExpandableCard), new Thickness(12));

        public static readonly BindableProperty CardMarginProperty =
            BindableProperty.Create(nameof(CardMargin), typeof(Thickness), typeof(ExpandableCard), new Thickness(0, 0, 0, 12));

        public static readonly BindableProperty CardStrokeProperty =
            BindableProperty.Create(nameof(CardStroke), typeof(Color), typeof(ExpandableCard), Colors.Transparent);

        public static readonly BindableProperty CardStrokeThicknessProperty =
            BindableProperty.Create(nameof(CardStrokeThickness), typeof(double), typeof(ExpandableCard), 1d);

        public static readonly BindableProperty CardBackgroundColorProperty =
            BindableProperty.Create(nameof(CardBackgroundColor), typeof(Color), typeof(ExpandableCard), Colors.Transparent);

        public static readonly BindableProperty CardCornerRadiusProperty =
            BindableProperty.Create(nameof(CardCornerRadius), typeof(float), typeof(ExpandableCard), 8f);

        public static readonly BindableProperty TitleColorProperty =
            BindableProperty.Create(nameof(TitleColor), typeof(Color), typeof(ExpandableCard), Colors.White);

        public static readonly BindableProperty SubtitleColorProperty =
            BindableProperty.Create(nameof(SubtitleColor), typeof(Color), typeof(ExpandableCard), Colors.White);

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Subtitle
        {
            get => (string)GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        public View CardContent
        {
            get => (View)GetValue(CardContentProperty);
            set => SetValue(CardContentProperty, value);
        }

        public View HeaderRightContent
        {
            get => (View)GetValue(HeaderRightContentProperty);
            set => SetValue(HeaderRightContentProperty, value);
        }

        public Thickness CardPadding
        {
            get => (Thickness)GetValue(CardPaddingProperty);
            set => SetValue(CardPaddingProperty, value);
        }

        public Thickness CardMargin
        {
            get => (Thickness)GetValue(CardMarginProperty);
            set => SetValue(CardMarginProperty, value);
        }

        public Color CardStroke
        {
            get => (Color)GetValue(CardStrokeProperty);
            set => SetValue(CardStrokeProperty, value);
        }

        public double CardStrokeThickness
        {
            get => (double)GetValue(CardStrokeThicknessProperty);
            set => SetValue(CardStrokeThicknessProperty, value);
        }

        public Color CardBackgroundColor
        {
            get => (Color)GetValue(CardBackgroundColorProperty);
            set => SetValue(CardBackgroundColorProperty, value);
        }

        public float CardCornerRadius
        {
            get => (float)GetValue(CardCornerRadiusProperty);
            set => SetValue(CardCornerRadiusProperty, value);
        }

        public Color TitleColor
        {
            get => (Color)GetValue(TitleColorProperty);
            set => SetValue(TitleColorProperty, value);
        }

        public Color SubtitleColor
        {
            get => (Color)GetValue(SubtitleColorProperty);
            set => SetValue(SubtitleColorProperty, value);
        }

        public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

        public bool HasHeaderRightContent => HeaderRightContent != null;

        public string ChevronText => IsExpanded ? "▾" : "▸";

        private static void OnAnyChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is ExpandableCard card)
                card.UpdateDerivedState();
        }

        private void UpdateDerivedState()
        {
            OnPropertyChanged(nameof(HasSubtitle));
            OnPropertyChanged(nameof(HasHeaderRightContent));
            OnPropertyChanged(nameof(ChevronText));
        }

        private void OnHeaderTapped(object sender, TappedEventArgs e)
        {
            IsExpanded = !IsExpanded;
        }
    }
}
