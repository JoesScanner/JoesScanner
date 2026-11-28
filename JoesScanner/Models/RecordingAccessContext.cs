using JoesScanner.Services;

namespace JoesScanner.Models;

internal enum RecordingOrigin { Unknown, Live, History }
internal sealed record RecordingAccessContext(RecordingOrigin Origin, bool Protected, string? Account = null);

internal readonly record struct RecordingIdentity(Guid? Recorder, string Endpoint, string Id, bool Protected, string Account)
{
    internal static RecordingIdentity? From(CallItem card)
    {
        var state = card.LiveState;
        if (string.IsNullOrWhiteSpace(card.BackendId) || state != null && state.Id != card.BackendId)
            return null;
        var endpoint = RecordingStorageReference.ServerKey(state?.ServerUrl ?? card.RecordingServer);
        return endpoint == null ? null : new RecordingIdentity(card.ServerId, endpoint, card.BackendId,
            card.RecordingAccess?.Protected == true, card.RecordingAccess?.Account ?? string.Empty);
    }

    private static long? RecordedSecond(CallItem card) =>
        card.RecordingTimeUtc?.ToUnixTimeSeconds() ?? card.LiveState?.Read().RecordingUnixSeconds;

    internal static CallItem? Find(IEnumerable<CallItem> cards, CallItem original)
    {
        var identity = From(original);
        var second = RecordedSecond(original);
        return cards.FirstOrDefault(card => ReferenceEquals(card, original) ||
            identity.HasValue && second.HasValue && card.BackendId == original.BackendId &&
            RecordedSecond(card) == second && From(card) == identity);
    }
}
