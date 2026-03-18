using System.Globalization;

namespace JoesScanner.Helpers
{
    public static class DateParseHelper
    {
        public static DateTime? TryParseUtc(string? raw)
        {
            var s = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(s))
                return null;

            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto) ||
                DateTimeOffset.TryParse(s, out dto))
            {
                return dto.UtcDateTime;
            }

            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ||
                DateTime.TryParse(s, out dt))
            {
                if (dt.Kind == DateTimeKind.Unspecified)
                    dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

                return dt.ToUniversalTime();
            }

            return null;
        }
    }
}
