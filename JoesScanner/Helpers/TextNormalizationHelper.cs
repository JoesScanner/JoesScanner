namespace JoesScanner.Helpers
{
    public static class TextNormalizationHelper
    {
        public static string NormalizeSmartQuotes(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return value
                .Replace('\u2018', '\'')
                .Replace('\u2019', '\'')
                .Replace('\u201C', '"')
                .Replace('\u201D', '"');
        }
    }
}
