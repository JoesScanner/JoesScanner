namespace JoesScanner.Models;

internal enum RecordingOrigin { Unknown, Live, History }
internal sealed record RecordingAccessContext(RecordingOrigin Origin, bool Protected, string? Account = null);
