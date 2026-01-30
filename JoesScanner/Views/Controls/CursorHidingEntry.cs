using Microsoft.Maui.Controls;

namespace JoesScanner.Views.Controls
{
    // Entry that can hide the iOS text cursor when the field is in password mode.
    // This prevents the blinking caret while still allowing typing.
    public sealed class CursorHidingEntry : Entry
    {
        public static readonly BindableProperty HideCursorWhenPasswordProperty =
            BindableProperty.Create(
                nameof(HideCursorWhenPassword),
                typeof(bool),
                typeof(CursorHidingEntry),
                true);

        public bool HideCursorWhenPassword
        {
            get => (bool)GetValue(HideCursorWhenPasswordProperty);
            set => SetValue(HideCursorWhenPasswordProperty, value);
        }
    }
}
