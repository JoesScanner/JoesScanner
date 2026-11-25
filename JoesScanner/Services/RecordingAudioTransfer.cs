using System.Buffers.Binary;

namespace JoesScanner.Services;

internal static class RecordingAudioTransfer
{
    internal static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".aac", ".m4a", ".mp4", ".ogg", ".opus", ".flac", ".wma", ".aiff", ".aif", ".amr", ".3gp"
    };

    internal static async Task<string> CopyAsync(HttpResponseMessage response, Stream target, string extension,
        long maximumBytes, Func<long?> availableBytes, bool speculative, CancellationToken token)
    {
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
            type?.EndsWith("json", StringComparison.OrdinalIgnoreCase) == true)
            throw Unavailable("The recorder returned a text response for its audio.");

        var expected = response.Content.Headers.ContentLength;
        CheckSpace(availableBytes(), expected ?? 0, maximumBytes, speculative);
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[81920];
        var headerLength = 0;
        while (headerLength < 32)
        {
            var read = await source.ReadAsync(buffer.AsMemory(headerLength, 32 - headerLength), token).ConfigureAwait(false);
            if (read == 0)
                break;
            headerLength += read;
        }
        var format = Identify(buffer.AsSpan(0, headerLength), extension);
        if (format == null)
            throw Unavailable("The recording file was empty or did not contain supported audio.");

        var requiredLength = buffer.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            ? (long)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4)) + 8 : 0;
        CheckSpace(availableBytes(), headerLength, maximumBytes, speculative);
        await target.WriteAsync(buffer.AsMemory(0, headerLength), token).ConfigureAwait(false);
        long total = headerLength;
        var nextSpaceCheck = total + PlaybackAudioCache.MiB;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > maximumBytes)
                throw Unavailable("The recording exceeds the available playback storage.");
            if (total >= nextSpaceCheck)
            {
                CheckSpace(availableBytes(), read, maximumBytes, speculative);
                nextSpaceCheck = total + PlaybackAudioCache.MiB;
            }
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (expected != null && expected != total || requiredLength > total)
            throw Unavailable("The recording download was incomplete.");
        return format;
    }

    private static void CheckSpace(long? available, long bytes, long maximum, bool speculative)
    {
        var reserve = speculative ? PlaybackAudioCache.LowStorageBytes : 0;
        if (bytes > maximum || available < bytes + reserve)
            throw Unavailable("The recording exceeds the available playback storage.");
    }

    private static string? Identify(ReadOnlySpan<byte> bytes, string extension)
    {
        if (bytes.Length < 12)
            return null;
        if ((bytes.StartsWith("RIFF"u8) || bytes.StartsWith("RF64"u8)) && bytes[8..].StartsWith("WAVE"u8))
            return ".wav";
        if (bytes.StartsWith("ID3"u8))
            return ".mp3";
        if (bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0 && (bytes[1] & 0x06) != 0)
            return ".mp3";
        if (bytes[0] == 0xff && (bytes[1] & 0xf6) == 0xf0)
            return ".aac";
        if (bytes[4..].StartsWith("ftyp"u8))
            return extension.ToLowerInvariant() is ".mp4" or ".3gp" ? extension.ToLowerInvariant() : ".m4a";
        if (bytes.StartsWith("OggS"u8))
            return extension.Equals(".opus", StringComparison.OrdinalIgnoreCase) ? ".opus" : ".ogg";
        if (bytes.StartsWith("fLaC"u8))
            return ".flac";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11]))
            return ".wma";
        if (bytes.StartsWith("FORM"u8) && (bytes[8..].StartsWith("AIFF"u8) || bytes[8..].StartsWith("AIFC"u8)))
            return ".aiff";
        return bytes.StartsWith("#!AMR"u8) ? ".amr" : null;
    }

    private static RecordingAudioUnavailableException Unavailable(string message) => new(detail: message);
}
