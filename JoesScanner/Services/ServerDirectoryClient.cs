using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JoesScanner.Helpers;
using JoesScanner.Models;

namespace JoesScanner.Services;

internal sealed record DirectorySnapshot(string Source, IReadOnlyList<ServerDirectoryEntry> Entries, IReadOnlySet<int>? Suspended);

internal sealed record DirectorySite(Uri? MyServers, string TermsVersion, string Terms);

internal enum DirectorySubmissionStatus { Pending, SignInRequired, Conflict, RateLimited, Rejected }

internal sealed record DirectoryListingDraft(string Name, string Address, string MapAnchor, bool ScannerLogin, bool FirewallAuth);

internal sealed record DirectoryRequest(WordPressSession Session, CancellationToken PrivacyGeneration, CancellationToken AccountGeneration)
{
    internal bool TryApply(Action apply) => Session.TryApplyIdentity(() =>
    {
        PrivacyGeneration.ThrowIfCancellationRequested();
        AccountGeneration.ThrowIfCancellationRequested();
        apply();
    });
}

internal sealed class ServerDirectoryClient
{
    private readonly ISettingsService _settings;
    private readonly PrivacyNetworkPolicy _privacy;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private CancellationTokenSource _account = new();

    internal ServerDirectoryClient(ISettingsService settings, HttpMessageHandler? transport = null)
    {
        _settings = settings;
        _privacy = PrivacyNetworkPolicy.For(settings);
        _http = _privacy.CreateCloudClient(transport);
        _http.Timeout = TimeSpan.FromSeconds(12);
        _settings.AccountIdentityChanged += (_, _) =>
        {
            CancellationTokenSource previous;
            lock (_gate)
            {
                previous = _account;
                _account = new();
            }
            previous.Cancel();
            previous.Dispose();
        };
    }

    internal bool CanShare => _privacy.AllowsCloud;

    internal bool HasAccount
    {
        get
        {
            if (!CanShare)
                return false;
            var session = WordPressSession.Capture(_settings);
            return ReadLogin(session) != null;
        }
    }

    internal DirectoryRequest Begin()
    {
        CancellationToken account;
        lock (_gate)
            account = _account.Token;
        return new(WordPressSession.Capture(_settings), _privacy.CloudToken(), account);
    }

    internal static string Source(WordPressSession session) => session.Endpoint("servers").AbsoluteUri;

    internal async Task<DirectorySnapshot> FetchAsync(DirectoryRequest operation, CancellationToken token = default)
    {
        using var lifetime = Link(operation, token);
        var payload = await _http.GetFromJsonAsync<DirectoryResponse>(
            operation.Session.Endpoint("servers"), lifetime.Token).ConfigureAwait(false);
        if (payload is not { Ok: true, Servers: not null })
            throw new InvalidDataException("The server directory response was incomplete.");

        var suspended = payload.Suspended == null ? null : new HashSet<int>();
        foreach (var item in payload.Suspended ?? [])
        {
            if (item is not { Id: > 0, Status: "suspended" } || !suspended!.Add(item.Id))
                throw new InvalidDataException("The directory suspension response was invalid.");
        }
        List<ServerDirectoryEntry> entries = [];
        HashSet<int> ids = [];
        var source = Source(operation.Session);
        foreach (var item in payload.Servers)
        {
            if (item is not { DirectoryId: > 0 } || !ids.Add(item.DirectoryId) ||
                !SavedConnectionUrl.TryParse(item.Url, out var uri, out var safe, out var embedded) ||
                embedded != null || uri!.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("The directory returned an invalid recorder.");
            if (suspended?.Contains(item.DirectoryId) == true)
                continue;
            item.Url = safe;
            item.InfoUrl = SavedConnectionUrl.SafeInfoUrl(item.InfoUrl);
            item.Source = source;
            entries.Add(item);
        }
        lifetime.Token.ThrowIfCancellationRequested();
        return new(source, entries, suspended);
    }

    internal async Task<DirectorySite> GetSiteAsync(DirectoryRequest operation, CancellationToken token = default)
    {
        using var lifetime = Link(operation, token);
        var endpoint = operation.Session.Endpoint("server-directory/config");
        var response = await _http.GetFromJsonAsync<SiteResponse>(endpoint, lifetime.Token).ConfigureAwait(false);
        if (response is not { TermsVersion.Length: > 0 and <= 20, Terms.Length: > 0 and <= 8000 })
            throw new InvalidDataException("The directory listing terms are unavailable.");
        Uri? page = null;
        if (!string.IsNullOrEmpty(response.MyServers))
        {
            if (!Uri.TryCreate(response.MyServers, UriKind.Absolute, out page) || !TrustedPage(endpoint, page))
                throw new InvalidDataException("The My Servers page is not on the configured account website.");
        }
        lifetime.Token.ThrowIfCancellationRequested();
        return new(page, response.TermsVersion, response.Terms);
    }

    internal static Uri BuildPrefill(Uri accountEndpoint, Uri page, string endpoint, string visibility)
    {
        if (!TrustedPage(accountEndpoint, page) || visibility is not ("public" or "private" or "restricted"))
            throw new InvalidDataException("The My Servers page is not on the configured account website.");
        var address = ShareableAddress(endpoint);
        var query = page.Query.TrimStart('?');
        return new UriBuilder(page)
        {
            Query = (query.Length == 0 ? "" : query + "&") +
                "directory_visibility=" + visibility + "&directory_address=" + Uri.EscapeDataString(address)
        }.Uri;
    }

    private static bool TrustedPage(Uri account, Uri page) =>
        account.Scheme == Uri.UriSchemeHttps && page.Scheme == Uri.UriSchemeHttps &&
        HostedServerRules.SameOrigin(account, page) && page.UserInfo.Length == 0 && page.Fragment.Length == 0 &&
        (page.Query.Length == 0 || page.Query.StartsWith("?page_id=", StringComparison.Ordinal) &&
            int.TryParse(page.Query.AsSpan(9), System.Globalization.NumberStyles.None, null, out var pageId) && pageId > 0);

    internal static string ShareableAddress(string endpoint)
    {
        if (!SavedConnectionUrl.TryParse(endpoint, out var uri, out var safe, out _) || safe.Length > 255 ||
            Uri.UnescapeDataString(uri!.AbsolutePath).Any(char.IsControl) ||
            Uri.UnescapeDataString(uri.AbsolutePath).IndexOfAny(['=', '@', '?', '#', '\\']) >= 0)
            throw new ArgumentException("Enter a recorder address that can be shared without credentials.");
        // Include only the normalized base address. Recorder userinfo, fragments, and query strings stay on device.
        return safe;
    }

    internal async Task<DirectorySubmissionStatus> SubmitAsync(DirectoryRequest operation, DirectorySite agreed,
        DirectoryListingDraft draft, CancellationToken token = default)
    {
        using var lifetime = Link(operation, token);
        var name = draft.Name.Trim();
        var anchor = draft.MapAnchor.Trim();
        if (name.Length is 0 or > 120 || anchor.Length is 0 or > 120)
            throw new ArgumentException("A server name and map anchor are required.");
        var address = ShareableAddress(draft.Address);
        var login = ReadLogin(operation.Session);
        if (login is not { } account)
            return DirectorySubmissionStatus.SignInRequired;

        var cancellation = lifetime.Token;
        cancellation.ThrowIfCancellationRequested();
        // Authenticate to the configured account origin first. The listing request carries only
        // the fresh, single-use grant and explicitly allowed directory fields.
        using var grantResponse = await _http.PostAsJsonAsync(operation.Session.Endpoint("server-directory/submission-grant"),
            new { account = account.Username, password = account.Password }, cancellation).ConfigureAwait(false);
        if (!grantResponse.IsSuccessStatusCode)
            return ClassifyFailure(grantResponse.StatusCode);
        var grant = await grantResponse.Content.ReadFromJsonAsync<GrantResponse>(cancellationToken: cancellation).ConfigureAwait(false);
        if (grant is not { UserId: > 0, Grant.Length: 64 } || !FavoriteValue.IsHex(grant.Grant, 64))
            throw new InvalidDataException("The directory returned an invalid submission grant.");
        if (account.ExpectedUser is { } expected && expected != grant.UserId)
            return DirectorySubmissionStatus.SignInRequired;
        cancellation.ThrowIfCancellationRequested();
        if (!operation.Session.IsCurrentAccount)
            throw new OperationCanceledException(cancellation);
        using var response = await _http.PostAsJsonAsync(operation.Session.Endpoint("server-directory/submissions"), new
        {
            grant = grant.Grant,
            terms_version = agreed.TermsVersion,
            listing = new
            {
                server_name = name,
                server_url = address,
                map_anchor = anchor,
                requires_scanner_login = draft.ScannerLogin ? "1" : "0",
                requires_firewall_auth = draft.FirewallAuth ? "1" : "0"
            }
        }, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return ClassifyFailure(response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<SubmissionResponse>(cancellationToken: cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (response.StatusCode != HttpStatusCode.Created || receipt is not { Id: > 0, Status: "pending" })
            throw new InvalidDataException("The directory did not confirm a pending submission.");
        return DirectorySubmissionStatus.Pending;
    }

    private (string Username, string Password, long? ExpectedUser)? ReadLogin(WordPressSession session)
    {
        (string, string, long?)? login = null;
        try
        {
            session.TryApplyIdentity(() =>
            {
                var owner = _settings.VerifiedFavoritesUserId;
                if ((owner.HasValue || session.Token.Length != 0) &&
                    _settings.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out var name, out var password) &&
                    !string.IsNullOrWhiteSpace(name) && !string.IsNullOrEmpty(password))
                    login = (name, password, owner);
            });
        }
        catch (CredentialStorageException) { }
        return login;
    }

    private static CancellationTokenSource Link(DirectoryRequest operation, CancellationToken token)
    {
        if (!operation.Session.IsCurrentAccount)
            throw new OperationCanceledException(token);
        return CancellationTokenSource.CreateLinkedTokenSource(operation.PrivacyGeneration, operation.AccountGeneration, token);
    }

    private static DirectorySubmissionStatus ClassifyFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DirectorySubmissionStatus.SignInRequired,
        HttpStatusCode.Conflict => DirectorySubmissionStatus.Conflict,
        HttpStatusCode.TooManyRequests => DirectorySubmissionStatus.RateLimited,
        HttpStatusCode.BadRequest => DirectorySubmissionStatus.Rejected,
        _ => throw new HttpRequestException("The directory could not complete the request.", null, status)
    };

    private sealed record DirectoryResponse(bool Ok, ServerDirectoryEntry?[]? Servers, Suspension[]? Suspended);
    private sealed record Suspension(int Id, string Status);
    private sealed record SiteResponse([property: JsonPropertyName("my_servers_url")] string? MyServers,
        [property: JsonPropertyName("terms_version")] string TermsVersion, string Terms);
    private sealed record GrantResponse(string Grant, [property: JsonPropertyName("user_id")] long UserId);
    private sealed record SubmissionResponse(int Id, string Status);
}
