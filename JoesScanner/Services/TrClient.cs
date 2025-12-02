using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace JoesScanner.Services;

// Lightweight HTTP client wrapper for talking to a Trunking Recorder instance.
public sealed class TrClient
{
    // Underlying HTTP client instance configured for the TR server.
    private readonly HttpClient _http;

    // Base URI for the TR server.
    private readonly Uri _base;

    // Shared JSON options matching the calljson endpoint.
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // Creates a client pointing at the given host and port.
    // Example: host = "192.168.42.9", port = 8080.
    public TrClient(string host, int port)
    {
        // HTTP only because this is targeting a LAN endpoint such as 192.168.42.9.
        _base = new Uri($"http://{host}:{port}/", UriKind.Absolute);

        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate
        })
        {
            BaseAddress = _base,
            Timeout = TimeSpan.FromSeconds(10)
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "application/json, text/javascript, */*; q=0.01");

        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Requested-With",
            "XMLHttpRequest");

        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "JoesScanner/1.0");

        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer",
            _base.ToString());
    }

    // Minimal DTO representing a single row from /calljson.
    public sealed class CallRow
    {
        public long DT_RowId { get; set; }
        public string? Time { get; set; }
        public string? Date { get; set; }
        public string? TargetID { get; set; }
        public string? TargetLabel { get; set; }
        public string? SystemID { get; set; }
        public string? SiteID { get; set; }
        public string? VoiceReceiver { get; set; }
        public string? CallText { get; set; }
        public string? StartTime { get; set; }
    }

    // Envelope that matches the DataTables style response from /calljson.
    private sealed class CallResponse
    {
        public int draw { get; set; }
        public int recordsTotal { get; set; }
        public int recordsFiltered { get; set; }
        public List<CallRow> data { get; set; } = new();
    }

    // Calls /calljson and returns the most recent "length" rows.
    public async Task<List<CallRow>> GetLatestAsync(int length, CancellationToken ct)
    {
        // Payload that matches what the browser sends, simplified for this use case.
        var payload = new
        {
            draw = 1,
            columns = Array.Empty<object>(),
            order = Array.Empty<object>(),
            start = 0,
            length,
            search = new { value = "", regex = false },
            SmartSort = false
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload, _json),
            Encoding.UTF8,
            "application/json");

        using var req = new HttpRequestMessage(HttpMethod.Post, "calljson")
        {
            Content = content
        };

        using var res = await _http.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        res.EnsureSuccessStatusCode();

        var cr = await res.Content.ReadFromJsonAsync<CallResponse>(_json, ct)
                 ?? new CallResponse();

        return cr.data;
    }
}
