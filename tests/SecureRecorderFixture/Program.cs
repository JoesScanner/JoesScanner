using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

if (args is not [var endpoint, var origin, var cert, var key] ||
    !ushort.TryParse(endpoint, out var port) || !ushort.TryParse(origin, out var sourcePort) ||
    port == 0 || sourcePort == 0 || port == sourcePort)
    throw new ArgumentException("Usage: SecureRecorderFixture <TLS port> <fixture port> <certificate.pem> <key.pem>");

using var leaf = X509Certificate2.CreateFromPemFile(cert, key);
using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
{
    BaseAddress = new Uri($"http://127.0.0.1:{sourcePort}/")
};
var sockets = new RecorderSocket();
using var host = new HostBuilder().ConfigureWebHost(web => web.UseKestrel(server =>
    server.Listen(IPAddress.Loopback, port, listen =>
    {
        listen.Protocols = HttpProtocols.Http1;
        listen.Use(next => async connection =>
        {
            // Record whether the client attempted cleartext on this TLS-only port.
            var input = connection.Transport.Input;
            var first = await input.ReadAsync(connection.ConnectionClosed);
            var bytes = first.Buffer;
            if (!bytes.IsEmpty)
            {
                var prefix = bytes.Slice(0, 1).ToArray()[0];
                Evidence.Log("connection", new { connection.ConnectionId, tlsHello = prefix == 22, prefix });
            }
            input.AdvanceTo(bytes.Start, bytes.Start);
            await next(connection);
        });
        listen.UseHttps(leaf);
    })).Configure(app =>
    {
        app.UseWebSockets();
        app.Run(async ctx =>
        {
            var req = ctx.Request;
            var path = req.Path.Value ?? "";
            if (req.Headers.ContainsKey("Authorization"))
                ctx.Response.StatusCode = 403;
            else if (path == "/Calls" && HttpMethods.IsGet(req.Method))
                await sockets.AcceptAsync(ctx);
            else if (HttpMethods.IsPost(req.Method) && path == "/calljson" || HttpMethods.IsGet(req.Method) &&
                (path is "/sitesjson" or "/receiversjson" or "/talkgroupsjson" ||
                 path.StartsWith("/audio/", StringComparison.Ordinal) || path.StartsWith("/archive/", StringComparison.Ordinal)))
            {
                using var forward = new HttpRequestMessage(new HttpMethod(req.Method), path);
                if (HttpMethods.IsPost(req.Method))
                {
                    using var body = new MemoryStream();
                    await req.Body.CopyToAsync(body, ctx.RequestAborted);
                    forward.Content = new ByteArrayContent(body.ToArray());
                    forward.Content.Headers.TryAddWithoutValidation("Content-Type", req.ContentType);
                }
                try
                {
                    using var reply = await http.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                    ctx.Response.StatusCode = (int)reply.StatusCode;
                    ctx.Response.ContentType = reply.Content.Headers.ContentType?.ToString();
                    ctx.Response.ContentLength = reply.Content.Headers.ContentLength;
                    await reply.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                }
                catch (HttpRequestException) when (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 502; }
            }
            else ctx.Response.StatusCode = 404;
            Evidence.Log("https", new { ctx.Connection.Id, req.Method, path, status = ctx.Response.StatusCode });
        });
    })).Build();
await host.StartAsync();
Evidence.Log("ready", new { port, sourcePort });
try
{
    while (Console.ReadLine() is { } line && line != "quit")
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words is ["offline"] or ["online"])
            await sockets.SetAvailableAsync(words[0] == "online");
        else if (words is ["malformed"] or ["incomplete"])
            await sockets.SendAsync(words[0], [], words[0] == "malformed" ? "{" : "{\"CallText\":\"Missing fixture identity\"}");
        else if (words.Length >= 2 && words[0] is "row" or "array" or "data" &&
            (words[0] != "row" || words.Length == 2))
        {
            // The same recorder snapshot supplies the socket and HTTPS representations.
            var ids = words[1..];
            using var lookup = new FormUrlEncodedContent(new Dictionary<string,string>
                { ["draw"] = "0", ["start"] = "0", ["length"] = int.MaxValue.ToString() });
            try
            {
                using var reply = await http.PostAsync("calljson", lookup);
                reply.EnsureSuccessStatusCode();
                var rows = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!["data"]!.AsArray()
                    .ToDictionary(n => (string)n!["DT_RowId"]!);
                if (ids.All(rows.ContainsKey))
                {
                    var selected = ids.Select(id => rows[id]).ToArray();
                    var payload = words[0] switch
                    {
                        "row" => selected[0]!.ToJsonString(),
                        "array" => JsonSerializer.Serialize(selected),
                        _ => JsonSerializer.Serialize(new { data = selected })
                    };
                    await sockets.SendAsync(words[0], ids, payload);
                    continue;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { Evidence.Log("snapshot_unavailable", new { error = ex.GetType().Name }); }
            Evidence.Log("rejected", new { command = words[0] });
        }
        else Evidence.Log("rejected", new { command = words.FirstOrDefault() });
    }
}
finally
{
    await sockets.SetAvailableAsync(false);
    await host.StopAsync();
}

internal static class Evidence
{
    public static void Log(string kind, object details) =>
        Console.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, kind, details }));
}

internal sealed class RecorderSocket
{
    private readonly SemaphoreSlim gate = new(1);
    private WebSocket? active;
    private string? connection;
    private bool available = true;

    public async Task AcceptAsync(HttpContext ctx)
    {
        WebSocket socket;
        await gate.WaitAsync(ctx.RequestAborted);
        try
        {
            if (!available || active != null || !ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = available ? 400 : 503;
                Evidence.Log("upgrade_rejected", new { ctx.Connection.Id, status = ctx.Response.StatusCode });
                return;
            }
            socket = await ctx.WebSockets.AcceptWebSocketAsync();
            active = socket;
            connection = ctx.Connection.Id;
            Evidence.Log("upgrade", new { connection, path = "/Calls", status = ctx.Response.StatusCode });
        }
        finally { gate.Release(); }

        try
        {
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open &&
                (await socket.ReceiveAsync(buffer.AsMemory(), ctx.RequestAborted)).MessageType != WebSocketMessageType.Close) { }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            await gate.WaitAsync();
            try
            {
                if (ReferenceEquals(active, socket)) active = null;
                socket.Dispose();
                Evidence.Log("disconnected", new { ctx.Connection.Id });
            }
            finally { gate.Release(); }
        }
    }

    public async Task SetAvailableAsync(bool enabled)
    {
        await gate.WaitAsync();
        try
        {
            available = enabled;
            if (!enabled) { active?.Abort(); active = null; }
            Evidence.Log("socket_control", new { available });
        }
        finally { gate.Release(); }
    }

    public async Task SendAsync(string format, string[] ids, string text)
    {
        await gate.WaitAsync();
        try
        {
            if (active is not { State: WebSocketState.Open } socket)
            {
                Evidence.Log("no_socket", new { format, ids });
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            Evidence.Log("delivery", new { connection, format, ids });
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        { Evidence.Log("send_failed", new { connection, format, ids, error = ex.GetType().Name }); }
        finally { gate.Release(); }
    }
}
