using System.Globalization;

namespace JoesScanner.Converters
{
    // Converts a boolean value to its inverse for data binding scenarios.
    public class InverseBooleanConverter : IValueConverter
    {
        // Converts an incoming boolean to its opposite; if the value is not a bool, it is returned unchanged.
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var b = value as bool?;
            if (b.HasValue)
            {
                return !b.Value;
            }

            return value;
        }

        // Converts back from the target value by inverting the boolean again; non-boolean values are returned unchanged.
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var b = value as bool?;
            if (b.HasValue)
            {
                return !b.Value;
            }

            return value;
        }
    }
}
