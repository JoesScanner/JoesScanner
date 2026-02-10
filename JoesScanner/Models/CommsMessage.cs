namespace JoesScanner.Models
{
    using Microsoft.Maui.Controls;

    public sealed class CommsMessage
    {
        public long Id { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        public DateTime? UpdatedAtUtc { get; init; }

        public string AuthorLabel { get; init; } = string.Empty;

        public string HeadingText { get; init; } = string.Empty;

        public string MessageText { get; init; } = string.Empty;

        // Pre-parsed message body with clickable links.
        // If MessageText contains HTML <a href="...">text</a> or plain URLs, this will render them as tappable spans.
        public FormattedString? MessageFormatted { get; init; }

        public string DisplayTimeLocal
        {
            get
            {
                var local = CreatedAtUtc.ToLocalTime();
                return local.ToString("MMM d, yyyy h:mm tt");
            }
        }
    }
}
