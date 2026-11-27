using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

internal sealed partial class RecorderFixture
{
    private volatile bool recoveryMode;
    private volatile bool failHttp;
    private PageRule? pageRule;

    private sealed record PageRule(int Start, bool Fail)
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void SetPageRule(PageRule? rule) => Interlocked.Exchange(ref pageRule, rule)?.Release.TrySetResult();

    private bool ApplyRecoveryCommand(string[] words)
    {
        switch (words)
        {
            case ["page", "clear"]:
                SetPageRule(null);
                break;
            case ["page", "hold" or "fail", var offset] when int.TryParse(offset, out var start) && start >= 0:
                SetPageRule(new PageRule(start, words[1] == "fail"));
                break;
            case ["batch", var size] when int.TryParse(size, out var count) && count is >= 1 and <= 500 &&
                (calls.Length == 0 || queueCount > 0):
                // Many calls can share a recorder timestamp. IDs retain their tie order.
                var second = Math.Max(lastQueueSecond, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var batch = new Call[count];
                for (var i = 0; i < count; i++)
                {
                    var n = ++queueCount;
                    batch[i] = new Call($"{(profile == null ? runId : "paired")}-P{n:D3}",
                        DateTimeOffset.FromUnixTimeSeconds(second), $"{CallLabel(n)}.", null, 1000, n);
                }
                lastQueueSecond = second;
                calls = [.. batch.Reverse(), .. calls];
                foreach (var call in batch) LogQueueRelease("batch", call);
                break;
            default:
                return false;
        }
        Log("recovery_control", new { profile, words });
        return true;
    }

    private async Task WriteRecoveryPageAsync(HttpListenerResponse response, string requestBody, Call[] snapshot,
        int draw, int start, int length)
    {
        string? range;
        if (requestBody.TrimStart().StartsWith('{'))
        {
            var columns = JsonNode.Parse(requestBody)?["columns"] as JsonArray;
            range = (string?)columns?.FirstOrDefault(c => (string?)c?["name"] == "Date")?["search"]?["value"];
        }
        else
            range = HttpUtility.ParseQueryString(requestBody)["columns[2][search][value]"];

        var matches = snapshot;
        if (!string.IsNullOrEmpty(range))
        {
            var dates = range.Split('|');
            if (dates.Length != 2 || !ReadUtc(dates[0], out var lower) || !ReadUtc(dates[1], out var upper))
                throw new FormatException("Expected a UTC recorder date range.");
            matches = snapshot.Where(c => c.At >= lower && c.At <= upper).ToArray();
        }

        var rule = Volatile.Read(ref pageRule);
        var controlled = rule?.Start == start && length > 0;
        Log("recovery_request", new { profile, draw, start, length, range, filtered = matches.Length,
            action = controlled ? (rule!.Fail ? "fail" : "hold") : "serve" });
        if (controlled && rule!.Fail)
            response.StatusCode = 503;
        else
        {
            if (controlled) await rule!.Release.Task;
            var page = matches.Skip(start).Take(length).ToArray();
            await WriteAsync(response, "application/json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                draw, recordsTotal = snapshot.Length, recordsFiltered = matches.Length,
                serverTimeZoneOffset = "+00:00", data = page.Select(c => c.ToRow(profile))
            }));
            Log("poll", new { draw, start, length, ids = page.Select(c => c.Id) });
        }
        Log("response", new { method = "POST", path = "/calljson", draw, start, status = response.StatusCode });

        static bool ReadUtc(string value, out DateTimeOffset utc) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }
}
