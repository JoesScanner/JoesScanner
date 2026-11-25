using System.Net;
using JoesScanner.Services;

internal static class DiagnosticReliabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 18, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static async Task ModesAndUnavailableStorage()
    {
        var folder = Directory.CreateTempSubdirectory("JoesScanner.Logs-");
        try
        {
            var logs = Path.Combine(folder.FullName, "Logs");
            await File.WriteAllTextAsync(logs, "Temporarily blocks the diagnostics directory.");
            var log = new DiagnosticLog(() => [logs], new FixedClock());
            log.Add("Connected recorder failed with HTTP 503.", detailed: false);
            log.Add("Trace disabled sentinel", detailed: true);
            await log.FlushAsync(prune: true);
            Check(log.Snapshot(100).Length == 1 && log.Snapshot(1)[0].Contains("HTTP 503"),
                "Basic diagnostics must be visible in memory with Detailed off and disk unavailable.");

            File.Delete(logs);
            log.Detailed = true;
            log.Add("Recorder retry attempt 2.", detailed: true);
            log.Add("Proxy-Authorization: Basic password-fixture\n  folded-token", detailed: false);
            log.Add("Directory email verification code is 123456", detailed: false);
            log.Add("HTTP 502 response_json={malformed raw-secret", detailed: true);
            log.Add($"Audio failed with {DiagnosticRedaction.Describe(new HttpRequestException(
                "Signed URL or private text with no reliable label", null, HttpStatusCode.BadGateway))}.", detailed: false);
            await log.FlushAsync(prune: true);
            var segment = DiagnosticLog.Files([logs]).Single().Info;
            var text = await File.ReadAllTextAsync(segment.FullName);
            Check(text.Contains("HTTP 503") && text.Contains("Recorder retry attempt 2") &&
                text.Contains("HttpRequestException (HTTP 502)") && log.Snapshot(100).Length == 6,
                "A writable directory must receive retained Basic events and the new Detailed events.");
            Check(!new[] { "Trace disabled sentinel", "password-fixture", "folded-token", "123456", "raw-secret",
                "Signed URL or private text" }.Any(text.Contains), "Only redacted text can enter the file or memory.");
            await log.ClearAsync();
            Check(log.Snapshot(1).Length == 0 && !DiagnosticLog.Files([logs]).Any(), "Manual clear removes files and memory.");
        }
        finally { folder.Delete(recursive: true); }
    }

    internal static async Task RetentionAndRotation()
    {
        var folder = Directory.CreateTempSubdirectory("JoesScanner.Retention-");
        try
        {
            var current = Directory.CreateDirectory(Path.Combine(folder.FullName, "Logs")).FullName;
            var legacy = Directory.CreateDirectory(Path.Combine(folder.FullName, "OldLogs")).FullName;
            var expired = Path.Combine(legacy, "app_log_2026-11-16.txt");
            var unrelated = Path.Combine(current, "download.mp3");
            File.WriteAllText(expired, "old");
            File.WriteAllText(unrelated, "recording");
            var names = Enumerable.Range(0, 26).Select(index => Path.Combine(index % 2 == 0 ? current : legacy,
                SegmentName(Now.AddDays(-6).AddMinutes(index), index))).ToArray();
            foreach (var name in names)
            {
                using var file = File.Create(name);
                file.SetLength(1_000_000);
            }

            var log = new DiagnosticLog(() => [current, legacy], new FixedClock());
            await log.FlushAsync(prune: true);
            Check(!File.Exists(expired) && !File.Exists(names[0]) && File.Exists(names[^1]) && File.Exists(unrelated),
                "Age pruning and the aggregate byte cap must remove the oldest diagnostics and preserve other files.");
            Check(DiagnosticLog.Files([current, legacy]).Sum(file => file.Info.Length) == DiagnosticLog.MaximumBytes,
                "The cap applies across current and legacy log directories.");
            var payload = new string('x', 3900);
            for (var i = 0; i < 650; i++)
                log.Add($"Bounded event {i} {payload}", detailed: false);
            await log.FlushAsync();
            var files = DiagnosticLog.Files([current, legacy]).ToArray();
            var newSegments = files.Where(file => file.Start == Now).ToArray();
            Check(newSegments.Length >= 2 && newSegments.All(file => file.Info.Length <= 1_000_000) &&
                files.Sum(file => file.Info.Length) <= DiagnosticLog.MaximumBytes,
                "Large bursts must rotate before crossing the shared storage cap.");
            Check(log.Snapshot(10_000).Length == 500 && log.Snapshot(1)[0].Contains("Bounded event 649"),
                "Bursts must keep a bounded memory buffer and the most recent entries.");
        }
        finally { folder.Delete(recursive: true); }
    }

    private static string SegmentName(DateTimeOffset timestamp, int id) =>
        $"app_log_{timestamp.UtcDateTime:yyyy-MM-dd_HHmmssfff}_{id:x32}.txt";

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
