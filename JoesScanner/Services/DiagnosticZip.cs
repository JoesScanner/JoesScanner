using System.IO.Compression;
using System.Text.RegularExpressions;

namespace JoesScanner.Services;

internal static class DiagnosticZip
{
    private static readonly Regex LogFileName = new(
        @"\Aapp_log_[0-9]{4}-[0-9]{2}-[0-9]{2}\.txt\z", RegexOptions.CultureInvariant);

    internal static async Task<string> CreateAsync(string cacheDirectory, string logsDirectory,
        IReadOnlyList<string> snapshot, int maxLogFiles)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, $"joesscanner_logs_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.zip");
        var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await using (output.ConfigureAwait(false))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                using (var snapshotSource = new StringReader(string.Join(Environment.NewLine, snapshot)))
                    await WriteEntryAsync(zip, "snapshot.txt", snapshotSource).ConfigureAwait(false);

                if (maxLogFiles > 0 && Directory.Exists(logsDirectory))
                {
                    // Only the app's dated text logs are eligible for export.
                    var logs = new DirectoryInfo(logsDirectory).EnumerateFiles("app_log_*.txt")
                        .Where(file => LogFileName.IsMatch(file.Name) &&
                            (file.Attributes & FileAttributes.ReparsePoint) == 0)
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .Take(maxLogFiles);

                    foreach (var file in logs)
                    {
                        FileStream input;
                        try
                        {
                            input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
                        }
                        catch (FileNotFoundException)
                        {
                            continue;
                        }
                        using var reader = new StreamReader(input);
                        await WriteEntryAsync(zip, $"files/{file.Name}", reader).ConfigureAwait(false);
                    }
                }
            }
            return path;
        }
        catch
        {
            // The path was created by this invocation and has not been shared yet.
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, TextReader source)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        await using var writer = new StreamWriter(entry.Open());
        await DiagnosticRedaction.WriteSanitizedLogAsync(source, writer).ConfigureAwait(false);
    }
}
