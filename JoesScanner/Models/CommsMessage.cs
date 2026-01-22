namespace JoesScanner.Models
{
    public sealed class CommsMessage
    {
        public long Id { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        public DateTime? UpdatedAtUtc { get; init; }

        public string AuthorLabel { get; init; } = string.Empty;

        public string MessageText { get; init; } = string.Empty;

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
