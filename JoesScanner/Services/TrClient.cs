using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace JoesScanner.Services;

public sealed class TrClient
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public TrClient(string host, int port)
    {
        // http only — you’re calling 192.168.42.9
        _base = new Uri($"http://{host}:{port}/", UriKind.Absolute);
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        })
        {
            BaseAddress = _base,
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "JoesScanner/1.0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", _base.ToString());
    }

    // Minimal DTOs matching /calljson
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

    private sealed class CallResponse
    {
        public int draw { get; set; }
        public int recordsTotal { get; set; }
        public int recordsFiltered { get; set; }
        public List<CallRow> data { get; set; } = new();
    }

    public async Task<List<CallRow>> GetLatestAsync(int length, CancellationToken ct)
    {
        // This payload mirrors your browser’s fetch body, but short
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

        using var content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "calljson") { Content = content };
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();

        var cr = await res.Content.ReadFromJsonAsync<CallResponse>(_json, ct) ?? new CallResponse();
        return cr.data;
    }
}
