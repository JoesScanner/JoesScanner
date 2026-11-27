using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed partial class RecorderFixture
{
    private volatile HistoryArchive? archive;

    private void ConfigureArchive(string[] words)
    {
        if (profile != null && archive == null && words is ["archive", "seed", var epoch] &&
            long.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 510 and <= 253402300799)
        {
            archive = new HistoryArchive(profile, DateTimeOffset.FromUnixTimeSeconds(seconds));
        }
        else if (archive == null || !archive.Configure(words))
        {
            Log("rejected", new { reason = "Invalid archive command or archive not seeded." });
            return;
        }
        Log("archive_control", new { profile, words });
    }

    private sealed class HistoryArchive(string recorder, DateTimeOffset newest)
    {
        private const int Count = 510;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> missing = new();
        private volatile bool duplicates;
        private volatile bool empty;
        private int nextDelayMs;
        private int failNext;

        public bool Configure(string[] words)
        {
            switch (words)
            {
                case ["archive", "duplicates", "on" or "off"]:
                    duplicates = words[2] == "on";
                    return true;
                case ["archive", "empty", "on" or "off"]:
                    empty = words[2] == "on";
                    return true;
                case ["archive", "delay", var ms] when int.TryParse(ms, out var delay) && delay is >= 0 and <= 12000:
                    Interlocked.Exchange(ref nextDelayMs, delay);
                    return true;
                case ["archive", "fail"]:
                    Interlocked.Exchange(ref failNext, 1);
                    return true;
                case ["archive", "missing" or "ready", var id] when int.TryParse(id, out var n) && n is >= 1 and <= Count:
                    if (words[1] == "missing") missing[n] = 0;
                    else missing.TryRemove(n, out _);
                    return true;
                default:
                    return false;
            }
        }

        private int Duration(int n) => n switch
        {
            1 => recorder == "south" ? 180000 : 20000, 2 => 2400, 3 => 3100, 4 => 4200,
            >= 226 and <= 250 => 8700 + n,
            _ => 1300 + n
        };

        private DateTimeOffset Time(int n) => newest.AddSeconds(1 - n);

        private Call Recording(int n) => new($"H{n:D3}", Time(n), $"{recorder} archive {n:D3}.",
            missing.ContainsKey(n) ? null : $"/archive/{n}.wav", Duration(n), n);

        public async Task<bool> TrySearchAsync(HttpListenerResponse response, string body, int draw, int start, int length)
        {
            if (!body.TrimStart().StartsWith('{') || JsonNode.Parse(body)?["columns"] is not JsonArray columns)
                return false;
            var values = columns.OfType<JsonObject>().ToDictionary(column => (string)column["name"]!,
                column => (string?)column["search"]?["value"] ?? "", StringComparer.Ordinal);
            // Live reconciliation also sends dates, but does not select a site, receiver or talkgroup.
            var site = values.GetValueOrDefault("Site", "");
            var receiver = values.GetValueOrDefault("Receiver", "");
            var targets = values.GetValueOrDefault("Target", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (site.Length == 0 && receiver.Length == 0 && targets.Length == 0)
                return false;
            var dates = values.GetValueOrDefault("Date", "").Split('|');
            if (dates.Length != 2 || !DateTimeOffset.TryParse(dates[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from) ||
                !DateTimeOffset.TryParse(dates[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
                throw new FormatException("Expected a bounded History date filter.");

            var all = Enumerable.Range(1, Count).ToList();
            if (duplicates) all.Insert(250, 250);
            var filteredArchive = empty ? Enumerable.Empty<int>() : all;
            var matches = filteredArchive.Where(n => Time(n) >= from && Time(n) < until &&
                (site == "" || site == (recorder == "south" ? "202" : "201")) &&
                (receiver == "" || receiver == $"{recorder} receiver") &&
                (targets.Length == 0 || targets.Contains(n % 2 == 1 ? "101" : "102"))).ToArray();
            var rows = matches.Skip(start).Take(length).Select(n => Recording(n).ToRow(recorder)).ToArray();
            var delay = Interlocked.Exchange(ref nextDelayMs, 0);
            var fail = Interlocked.Exchange(ref failNext, 0) != 0;
            Log("history_request", new { recorder, draw, start, length, from, until, receiver, site, targets, delay, fail });
            if (delay > 0) await Task.Delay(delay);
            if (fail) response.StatusCode = 503;
            else await WriteAsync(response, "application/json", JsonSerializer.SerializeToUtf8Bytes(new
                { draw, recordsTotal = all.Count, recordsFiltered = matches.Length, data = rows }));
            Log("history_response", new { recorder, draw, start, length, status = response.StatusCode,
                filtered = matches.Length, ids = fail ? [] : rows.Select(row => (string)row["DT_RowId"]!).ToArray(), newest });
            return true;
        }

        public async Task WriteAudioAsync(HttpListenerResponse response, string path)
        {
            var file = Path.GetFileNameWithoutExtension(path);
            if (!int.TryParse(file, out var n) || n is < 1 or > Count || Recording(n).Audio != path)
            {
                response.StatusCode = 404;
                return;
            }
            var hz = (recorder == "south" ? 600 : 300) + 23 * (n % 127);
            var clip = RecorderWave.Create(44 + 16 * Duration(n), hz);
            await WriteAsync(response, "audio/wav", clip);
            Log("history_audio", new { recorder, n, durationMs = Duration(n), hz, bytes = clip.Length });
        }
    }
}
