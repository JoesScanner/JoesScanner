using System.Security.Cryptography;
using System.Text.Json;

namespace JoesScanner.Services;

public sealed partial class RecordingMetadataScope
{
    private const string Prefix = "calls:";
    private readonly Action<Action, CancellationToken>? _commit;

    internal RecordingMetadataScope(Guid? recorderId, string endpoint, bool hosted, string? account,
        Action<Action, CancellationToken>? commit = null)
    {
        var archive = RecordingStorageReference.ArchiveKey(recorderId, endpoint)
            ?? throw new ArgumentException("Invalid recorder endpoint.", nameof(endpoint));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { archive, hosted ? "hosted" : "custom", account });
        StorageKey = Prefix + Convert.ToHexString(SHA256.HashData(bytes));
        _commit = commit;
    }

    internal string StorageKey { get; }

    internal static string? NormalizeStoredKey(string? value) =>
        value is { Length: 70 } && value.StartsWith(Prefix, StringComparison.Ordinal) &&
        value.AsSpan(Prefix.Length).IndexOfAnyExcept("0123456789ABCDEF") < 0
            ? value : RecordingStorageReference.ServerKey(value);

    internal void Commit(Action commit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_commit == null)
            commit();
        else
            _commit(commit, token);
    }
}
