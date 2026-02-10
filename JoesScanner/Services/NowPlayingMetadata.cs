namespace JoesScanner.Services
{
    // Snapshot of the fields that can appear in Bluetooth and lock screen media metadata.
    // Not all devices display all fields.
    public sealed class NowPlayingMetadata
    {
        public string Artist { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Composer { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
    }
}
