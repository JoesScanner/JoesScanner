using System.IO.Compression;

namespace JoesScanner.Services;

internal static class DiagnosticZip
{
    internal static async Task<string> CreateAsync(string cacheDirectory, IReadOnlyList<string> logsDirectories,
        IReadOnlyList<string> snapshot, int maxLogFiles, TimeProvider clock)
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

                if (maxLogFiles > 0)
                {
                    var cutoff = clock.GetUtcNow() - DiagnosticLog.MaximumAge;
                    var logs = DiagnosticLog.Files(logsDirectories).Where(file => file.Start > cutoff)
                        .OrderByDescending(file => file.Start).Take(maxLogFiles);
                    long remaining = DiagnosticLog.MaximumBytes;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var (file, _) in logs)
                    {
                        if (file.Length > remaining || !names.Add(file.Name))
                            continue;
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
                        remaining -= file.Length;
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
