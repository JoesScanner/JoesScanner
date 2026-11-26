using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

internal sealed class RecorderFixture
{
    private const int ClipSeconds = 8;
    private readonly string runId = Guid.NewGuid().ToString("N")[..8];
    private readonly byte[] lowTone = RecorderWave.Create(44 + ClipSeconds * 16000, 440);
    private readonly byte[] highTone = RecorderWave.Create(44 + ClipSeconds * 16000, 880);
    // The console has one writer. A poll takes one snapshot, including during a B update.
    private volatile Call[] calls = [];

    private int queueCount;
    private long lastQueueSecond;

    private sealed record Call(string Id, DateTimeOffset At, string Text, string? Audio,
        int DurationMs = ClipSeconds * 1000, int Number = 0)
    {
        public JsonObject ToRow()
        {
            var row = JsonSerializer.SerializeToNode(MobileRecoveryTests.Row(Id, At, Audio))!.AsObject();
            row["CallText"] = Text;
            row["CallDuration"] = (int)Math.Ceiling(DurationMs / 1000d);
            row["TargetID"] = "101";
            row["TargetLabel"] = Number == 0 ? "Fixture Dispatch" : $"Fixture {Number:D3}";
            row["SystemID"] = "1";
            row["SystemLabel"] = "Fixture System";
            row["SiteID"] = "1";
            row["SiteLabel"] = "Fixture Site";
            row["VoiceReceiver"] = "Fixture Receiver";
            return row;
        }
    }

    internal static async Task RunAsync(int port)
    {
        var fixture = new RecorderFixture();
        using var listener = new HttpListener();
        var origin = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(origin);
        listener.Start();
        Log("ready", new { origin, fixture.runId, commands = "a, b, update-b, send, burst, hold, attach, revise, quit" });
        var serving = fixture.ServeAsync(listener);
        try
        {
            while (Console.ReadLine() is { } line)
            {
                var command = line.Trim().ToLowerInvariant();
                if (command == "quit") break;
                fixture.Release(command);
            }
        }
        finally
        {
            listener.Stop();
            await serving;
            Log("stopped", new { fixture.runId });
        }
    }

    private void Release(string command)
    {
        var current = calls;
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.FirstOrDefault() is "send" or "hold" or "burst" or "attach" or "revise")
        {
            if ((current.Length == 0 || queueCount > 0) && ApplyQueueCommand(words, current))
                return;
            Log("rejected", new { reason = "Invalid queue command, arguments, or fixture mode." });
            return;
        }
        if (queueCount > 0)
        {
            Log("rejected", new { reason = "Restart for a Phase 1 scenario." });
            return;
        }
        var now = DateTimeOffset.UtcNow;
        Call next;
        switch (command, current.Length)
        {
            case ("a", 0):
                next = new($"{runId}-A", now, "Phase one alpha transmission.", "/audio/a.wav");
                calls = [next];
                break;
            case ("b", 1):
                next = new($"{runId}-B", now, "Phase one bravo awaiting audio.", null);
                calls = [next, current[0]];
                break;
            case ("update-b", 2) when current[0].Audio == null:
                next = current[0] with { Text = "Phase one bravo revised, audio available.", Audio = "/audio/b.wav" };
                calls = [next, current[1]];
                break;
            default:
                Log("rejected", new { reason = "Expected a, b, update-b in order; each can be released once." });
                return;
        }
        Log("release", new { command, next.Id, next.At, next.Text, next.Audio });
    }

    private bool ApplyQueueCommand(string[] words, Call[] current)
    {
        var command = words[0];
        if (command is "attach" or "revise")
        {
            if (words.Length != 2 || !int.TryParse(words[1], out var number))
                return false;
            var index = Array.FindIndex(current, c => c.Number == number);
            if (index < 0)
                return false;
            var prior = current[index];
            if (command == "attach" && prior.Audio != null)
                return false;
            var revised = prior with
            {
                Text = $"Phase 2 call {number:D3} revised.",
                Audio = command == "attach" ? $"/audio/{number}.wav" : prior.Audio
            };
            var snapshot = (Call[])current.Clone();
            snapshot[index] = revised;
            calls = snapshot;
            LogQueueRelease(command, revised);
            return true;
        }

        int count = words.Length - 1;
        if (count is < 1 or > 64 || (command != "burst" && count != 1))
            return false;
        var durations = new int[count];
        for (int i = 0; i < count; i++)
            if (!int.TryParse(words[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out durations[i]) ||
                durations[i] is < 250 or > 60000)
                return false;

        var firstSecond = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - count + 1, lastQueueSecond + 1);
        var batch = new Call[count];
        for (int i = 0; i < count; i++)
        {
            int number = ++queueCount;
            batch[i] = new Call($"{runId}-P{number:D3}", DateTimeOffset.FromUnixTimeSeconds(firstSecond + i),
                $"Phase 2 call {number:D3}.", command == "hold" ? null : $"/audio/{number}.wav", durations[i], number);
        }
        lastQueueSecond = firstSecond + count - 1;
        calls = [.. batch.Reverse(), .. current];
        foreach (var call in batch)
            LogQueueRelease(command, call);
        return true;
    }

    private static int ToneHz(int number) => 300 + 23 * (number % 127);

    private static void LogQueueRelease(string command, Call call) =>
        Log("release", new { command, call.Id, call.At, call.Text, call.Audio, call.DurationMs, frequencyHz = ToneHz(call.Number) });

    private async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception e) when (!listener.IsListening && e is HttpListenerException or ObjectDisposedException)
            {
                break;
            }
            var path = context.Request.Url!.AbsolutePath;
            var method = context.Request.HttpMethod;
            Log("request", new { method, path });
            using var response = context.Response;
            try
            {
                var snapshot = calls;
                if (context.Request.Headers["Authorization"] != null)
                    response.StatusCode = 403;
                else if (method == "POST" && path == "/calljson")
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var (draw, start, length) = ReadPoll(await reader.ReadToEndAsync());
                    var rows = snapshot.Skip(start).Take(length).Select(call => call.ToRow()).ToArray();
                    var body = JsonSerializer.SerializeToUtf8Bytes(new { draw, recordsTotal = snapshot.Length,
                        recordsFiltered = snapshot.Length, data = rows });
                    await WriteAsync(response, "application/json", body);
                    Log("poll", new { draw, start, length, ids = rows.Select(row => row["DT_RowId"]) });
                }
                else if (method == "GET" && snapshot.FirstOrDefault(call => call.Audio == path) is { } call)
                {
                    var clip = call.Number == 0
                        ? (path == "/audio/a.wav" ? lowTone : highTone)
                        : RecorderWave.Create(44 + 16 * call.DurationMs, ToneHz(call.Number));
                    await WriteAsync(response, "audio/wav", clip);
                    Log("audio_download", new { call.Id, path, bytesWritten = clip.Length });
                }
                else
                    response.StatusCode = 404;
                Log("response", new { method, path, status = response.StatusCode, bytes = response.ContentLength64 });
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
            {
                response.StatusCode = 400;
                Log("response", new { method, path, status = 400, reason = "Malformed polling request." });
            }
            catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException)
            {
                Log("interrupted", new { method, path, error = e.GetType().Name });
            }
        }
    }

    private static (int Draw, int Start, int Length) ReadPoll(string body)
    {
        if (body.TrimStart().StartsWith('{'))
        {
            var json = JsonNode.Parse(body)!.AsObject();
            return (Number(json["draw"]?.ToString()), Number(json["start"]?.ToString()), Number(json["length"]?.ToString()));
        }
        var form = HttpUtility.ParseQueryString(body);
        return (Number(form["draw"]), Number(form["start"]), Number(form["length"]));

        static int Number(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : throw new FormatException("Expected a nonnegative DataTables integer.");
    }

    private static async Task WriteAsync(HttpListenerResponse response, string type, byte[] bytes)
    {
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private static void Log(string kind, object details) =>
        Console.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, kind, details }));
}
