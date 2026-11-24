using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JoesScanner.Models;

namespace JoesScanner.Services;

internal sealed record FavoriteTalkgroupIdentity(string System, string Site, string Receiver, string Talkgroup)
{
    internal static FavoriteTalkgroupIdentity? From(CallCardMetadata? metadata)
    {
        if (metadata == null)
            return null;

        var identity = new FavoriteTalkgroupIdentity(Identity(metadata.SystemId, metadata.System),
            Identity(metadata.SiteId, metadata.Site), Identity(null, metadata.Receiver),
            Identity(metadata.TalkgroupId, metadata.Talkgroup));
        return identity.Talkgroup.Length == 0 ||
            new[] { identity.System, identity.Site, identity.Receiver, identity.Talkgroup }.Any(field => Encoding.UTF8.GetByteCount(field) > 768)
            ? null : identity;
    }

    private static string Identity(string? id, string? label) =>
        Normalize(id) is { Length: > 0 } value ? "id:" + value :
        Normalize(label) is { Length: > 0 } name ? "label:" + name : string.Empty;

    private static string Normalize(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        return value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || value.Any(char.IsControl)
            ? string.Empty : value.ToUpperInvariant();
    }
}

internal sealed record FavoriteValue(string Id, string? Label = null, string? ServerKey = null,
    FavoriteTalkgroupIdentity? Identity = null, string? GroupId = null)
{
    internal bool IsGroup => Id.StartsWith('g');

    internal static FavoriteValue Group(string name) => new("g" + Guid.NewGuid().ToString("N"), CleanLabel(name));

    internal static FavoriteValue Talkgroup(string serverKey, FavoriteTalkgroupIdentity identity, string? name, string? groupId = null)
    {
        var label = CleanLabel(name ?? string.Empty);
        if (label.Length == 0)
            label = CleanLabel(identity.Talkgroup);
        return new(TalkgroupId(serverKey, identity), label, serverKey, identity, groupId);
    }

    internal static string TalkgroupId(string serverKey, FavoriteTalkgroupIdentity identity)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\u001f', serverKey, identity.System, identity.Site,
            identity.Receiver, identity.Talkgroup));
        return "f" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static string CleanLabel(string name)
    {
        var label = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return string.Concat(label.EnumerateRunes().Take(128));
    }

    internal static bool IsKey(string? key) => key is { Length: 65 } && key[0] == 'f' && IsHex(key.AsSpan(1), 64)
        || key is { Length: 33 } && key[0] == 'g' && IsHex(key.AsSpan(1), 32);

    internal static bool IsHex(ReadOnlySpan<char> value, int length) =>
        value.Length == length && value.IndexOfAnyExcept("0123456789abcdef") < 0;
}

internal sealed record FavoriteChange(string MutationId, long ExpectedRevision, FavoriteValue Value, bool Deleted);
internal sealed record FavoriteRecord(long Revision, string MutationId, FavoriteValue Value, bool Deleted);
internal sealed record FavoriteSyncResult(string Id, string MutationId, bool Accepted, FavoriteRecord? Record);
internal sealed record FavoriteSyncRequest(long? OwnerId, FavoriteChange[] Changes, string? After = null);
internal sealed record FavoriteSyncResponse(long UserId, FavoriteSyncResult[] Results, FavoriteRecord[] Records, string? Next);
internal sealed record FavoriteEdit(FavoriteValue Value, bool Deleted, long BaseRevision);
internal sealed record FavoritesCollection(FavoriteValue[] Values, FavoriteEdit[] Conflicts, bool HasPending);
internal sealed record FavoritesOwner(string Authority, long UserId)
{
    internal string Key => Authority + "|" + UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal static class FavoritesJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
