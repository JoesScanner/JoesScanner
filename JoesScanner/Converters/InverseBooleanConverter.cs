using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace JoesScanner.Converters
{
    public class InverseBooleanConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var b = value as bool?;
            if (b.HasValue)
            {
                return !b.Value;
            }

            return value;
        }

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
