using System.Net;

namespace JoesScanner.Services;

internal sealed class RecordingAudioUnavailableException(Exception? cause = null, string? detail = null)
    : InvalidOperationException((detail ?? "Recording audio is unavailable.") + " Retry it or skip it to continue.", cause);

internal static class RecordingAudioRecovery
{
    internal static async Task<string?> DownloadAsync(HttpClient client, string url, PlaybackAudioCache.Transfer transfer,
        CancellationToken token, Action<HttpRequestMessage>? setScope = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        if (uri.UserInfo.Length != 0)
            uri = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        setScope?.Invoke(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await transfer.StoreAsync(response, Path.GetExtension(uri.AbsolutePath), token).ConfigureAwait(false);
    }

    internal static async Task<string> AcquireAsync(string? initialUrl, Func<CancellationToken, Task<string?>> refresh,
        Func<string, CancellationToken, Task<string?>> acquire, CancellationToken token, TimeProvider? clock = null)
    {
        Exception? lastError = null;
        var url = initialUrl;
        // At most two refreshes per playback request. No native playback is
        // retried here, so an audible recording cannot be repeated by this loop.
        for (var refreshes = 0; ; refreshes++)
        {
            token.ThrowIfCancellationRequested();
            var acquiring = false;
            try
            {
                if (refreshes != 0)
                {
                    url = await refresh(token);
                    lastError = null;
                }
                acquiring = true;
                if (!string.IsNullOrWhiteSpace(url) && await acquire(url, token) is { Length: > 0 } path)
                    return path;
            }
            catch (FirewallAuthenticationException ex) when (acquiring && ex.StatusCode == HttpStatusCode.Forbidden &&
                Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Query.Length != 0)
            {
                // A host-local signed URL can return the same 403 as firewall
                // denial. Re-resolve metadata using the ordinary firewall policy.
                lastError = ex;
            }
            catch (HttpRequestException ex) when (ex is not FirewallAuthenticationException &&
                (ex.StatusCode is null or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests || (int?)ex.StatusCode >= 500))
            {
                lastError = ex;
            }
            if (refreshes == 2)
            {
                var reason = lastError switch
                {
                    HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone } =>
                        "The recorder refused the audio URL or could not find the audio file.",
                    HttpRequestException => "The recorder or audio host could not be reached.",
                    _ => "The recorder did not provide playable audio."
                };
                throw new RecordingAudioUnavailableException(lastError, reason);
            }
            if (refreshes != 0)
                await Task.Delay(TimeSpan.FromSeconds(1), clock ?? TimeProvider.System, token);
        }
    }
}
