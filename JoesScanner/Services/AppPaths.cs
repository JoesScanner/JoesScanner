using System;
using System.IO;
using Microsoft.Maui.Storage;

namespace JoesScanner.Services;

// Centralized helpers for locating app data paths across packaged and unpackaged installs.
// On Windows, some WinRT-backed storage APIs can throw when the app is running without
// package identity (for example, during certain debug runs). We fall back to the user's
// LocalApplicationData folder in that case.
//
// IMPORTANT:
// Do not re-probe WinRT-backed APIs repeatedly. Even if the exception is caught,
// first-chance WinRT exceptions are expensive and can cause noticeable UI latency.
internal static class AppPaths
{
    private static readonly Lazy<string> _appDataDir = new(ResolveAppDataDirectorySafe, isThreadSafe: true);

    public static string GetAppDataDirectorySafe() => _appDataDir.Value;

    private static string ResolveAppDataDirectorySafe()
    {
        try
        {
            var dir = FileSystem.AppDataDirectory;
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }
        catch
        {
        }

        // Fallback for environments where FileSystem.AppDataDirectory is not available.
        // Example: Windows unpackaged execution paths can throw WinRT originate errors.
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fallback = Path.Combine(baseDir, "JoesScanner");

        try { Directory.CreateDirectory(fallback); } catch { }
        return fallback;
    }

    public static string GetDbPath(string fileName)
    {
        var dir = GetAppDataDirectorySafe();
        return Path.Combine(dir, fileName);
    }
}
