using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace JoesScanner.Services;

internal sealed class FavoritesAccountRejectedException(HttpStatusCode status)
    : HttpRequestException("The Favorites account was rejected.", null, status);

internal sealed class FavoritesSyncClient(HttpClient http)
{
    internal async Task<FavoriteSyncResponse> ExchangeAsync(Uri endpoint, string username, string password,
        FavoriteSyncRequest change, CancellationToken token)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0)
            throw new HttpRequestException("Favorites sign-in requires the configured HTTPS account endpoint.");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("JoesScannerAccount",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        request.Content = JsonContent.Create(change, options: FavoritesJson.Options);
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        if (await IsAccountRejectionAsync(response, token).ConfigureAwait(false))
            throw new FavoritesAccountRejectedException(response.StatusCode);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<FavoriteSyncResponse>(FavoritesJson.Options, token).ConfigureAwait(false);
        if (result is not { UserId: > 0, Results: not null, Records: not null } ||
            (change.OwnerId is { } owner && owner != result.UserId) ||
            !ValidResponse(change, result))
            throw new InvalidDataException("The Favorites service sent an unexpected response.");
        return result;
    }

    internal async Task ReconcileAsync(FavoritesRepository db, FavoritesOwner owner, Uri endpoint, string username,
        string password, Func<Action, bool> apply, CancellationToken token)
    {
        FavoriteChange[]? batch = null;
        if (!apply(() => batch = db.Prepare(owner.Key, 50)))
            return;

        string? after = null;
        do
        {
            token.ThrowIfCancellationRequested();
            var response = await ExchangeAsync(endpoint, username, password,
                new(owner.UserId, batch!, after), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!apply(() => db.Apply(owner.Key, response)))
                return;
            after = response.Next;
            batch = [];
        } while (after != null);
    }

    private static async Task<bool> IsAccountRejectionAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) ||
            response.Content.Headers.ContentType?.MediaType != "application/json")
            return false;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(FavoritesJson.Options, token).ConfigureAwait(false);
            return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String &&
                code.GetString() is "joes_favorites_authentication_required" or "joes_favorites_account_changed";
        }
        catch (JsonException) { return false; }
    }

    private static bool ValidResponse(FavoriteSyncRequest request, FavoriteSyncResponse response)
    {
        if (response.Records.Length > 200 || response.Results.Length != request.Changes.Length)
            return false;
        var remaining = request.Changes.ToDictionary(change => change.Value.Id, StringComparer.Ordinal);
        foreach (var result in response.Results)
        {
            if (result == null || result.Id == null || !remaining.Remove(result.Id, out var sent) ||
                result.MutationId != sent.MutationId || (result.Accepted && result.Record?.MutationId != sent.MutationId) ||
                (result.Record is { } remote && (!ValidRecord(remote) || remote.Value.Id != result.Id)))
                return false;
        }

        var prior = request.After;
        foreach (var record in response.Records)
        {
            if (!ValidRecord(record) || string.CompareOrdinal(record.Value.Id, prior) <= 0)
                return false;
            prior = record.Value.Id;
        }
        return response.Next == null || (response.Records.Length > 0 && response.Next == prior);
    }

    private static bool ValidRecord(FavoriteRecord? record)
    {
        if (record is not { Revision: > 0, Value: { } value } ||
            !FavoriteValue.IsKey(value.Id) || !FavoriteValue.IsHex(record.MutationId, 32))
            return false;
        if (record.Deleted)
            return true;
        if (string.IsNullOrWhiteSpace(value.Label))
            return false;
        return value.IsGroup || value.ServerKey is { } server && value.Identity is { Talkgroup.Length: > 0 } identity &&
            identity.System != null && identity.Site != null && identity.Receiver != null &&
            FavoriteValue.TalkgroupId(server, identity) == value.Id;
    }
}
