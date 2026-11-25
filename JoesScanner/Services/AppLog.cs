using Microsoft.Maui.Storage;

namespace JoesScanner.Services;

public static class AppLog
{
    private const string LegacyPreference = "app_log_enabled";
    private const string StateFile = "app_log_detailed.state";
    private static readonly object Mode = new();
    private static readonly SemaphoreSlim PreferenceWrite = new(1, 1);
    private static readonly Lazy<IReadOnlyList<string>> Locations = new(FindLocations);
    private static readonly DiagnosticLog Store = new(() => Locations.Value, TimeProvider.System);
    private static bool _modeChosen;
    private static readonly Lazy<Task> Startup = new(() => Task.Run(async () =>
    {
        var detail = ReadPreference();
        lock (Mode)
        {
            if (!_modeChosen)
                Store.Detailed = detail;
        }
        _ = Task.Run(Store.RunAsync);
        await PersistPreferenceAsync().ConfigureAwait(false);
    }));

    // Existing trace call sites use this flag for optional, more expensive diagnostics.
    public static bool IsEnabled => Store.Detailed;

    internal static Task InitializeAsync() => Startup.Value;

    public static void SetEnabled(bool enabled)
    {
        lock (Mode)
        {
            _modeChosen = true;
            Store.Detailed = enabled;
        }
        _ = Startup.Value;
        _ = Task.Run(PersistPreferenceAsync);
    }

    public static void Basic(string message) => Record(message, detailed: false);
    public static void Basic(Func<string> message) => Record(message, detailed: false);
    public static void Add(string message) => Record(message, detailed: true);
    public static void Add(Func<string> message) => Record(message, detailed: true);
    public static void DebugWriteLine(string message) => Add(message);
    public static void DebugWriteLine(Func<string> message) => Add(message);

    private static void Record(Func<string> message, bool detailed)
    {
        if (detailed && !Store.Detailed)
            return;
        try { Record(message(), detailed); } catch { }
    }

    private static void Record(string message, bool detailed)
    {
        if (detailed && !Store.Detailed)
            return;
        Store.Add(message, detailed);
        _ = Startup.Value;
    }

    public static string[] GetSnapshot(int maxLines) => Store.Snapshot(maxLines);
    public static Task ClearAllAsync() => Task.Run(Store.ClearAsync);

    public static async Task<string?> ExportLogsAsync(int maxLogFiles = 50, int snapshotMaxLines = 500)
    {
        try
        {
            await Startup.Value.ConfigureAwait(false);
            return await Task.Run(async () =>
            {
                await Store.FlushAsync(prune: true).ConfigureAwait(false);
                var cache = AppPaths.TryGetCacheDirectory();
                if (string.IsNullOrWhiteSpace(cache))
                    return null;
                return await DiagnosticZip.CreateAsync(cache, Locations.Value, Store.Snapshot(snapshotMaxLines).Reverse().ToArray(),
                    maxLogFiles, TimeProvider.System).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch { return null; }
    }

    private static bool ReadPreference()
    {
        var roots = Locations.Value;
        if (roots.Count > 0 && ReadState(roots[0], StateFile) is { } current)
            return current;
        if (roots.Count > 0 && ReadState(roots[0], "app_log_enabled.state") is { } legacy)
            return legacy;
        try
        {
            if (Preferences.ContainsKey(LegacyPreference))
                return Preferences.Get(LegacyPreference, false);
        }
        catch { }
        var markers = roots.Select(root => ReadState(root, "app_log_enabled.txt")).ToArray();
        return !markers.Contains(false) && markers.Contains(true);
    }

    private static bool? ReadState(string root, string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(root, name)).Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "on" => true,
                "0" or "false" or "off" => false,
                _ => null
            };
        }
        catch { return null; }
    }

    private static async Task PersistPreferenceAsync()
    {
        await PreferenceWrite.WaitAsync().ConfigureAwait(false);
        try
        {
            var detailed = Store.Detailed;
            try
            {
                var root = Locations.Value.FirstOrDefault();
                if (root == null)
                    return;
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, StateFile);
                File.WriteAllText(path + ".new", detailed ? "1" : "0");
                File.Move(path + ".new", path, overwrite: true);
            }
            catch { }
        }
        finally { PreferenceWrite.Release(); }
    }

    private static IReadOnlyList<string> FindLocations()
    {
        var list = new List<string>();
        try { list.Add(Path.Combine(AppPaths.GetAppDataDirectorySafe(), "Logs")); } catch { }
        try
        {
            if (FileSystem.AppDataDirectory is { Length: > 0 } path)
                list.Add(Path.Combine(path, "Logs"));
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (fallback.Length > 0)
                list.Add(Path.Combine(fallback, "JoesScanner", "Logs"));
        }
        catch { }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
