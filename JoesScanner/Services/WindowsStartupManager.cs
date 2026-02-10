using Microsoft.Win32;
using System;
using System.IO;

namespace JoesScanner.Services
{
    internal static class WindowsStartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "JoesScanner";

        // Best effort: enable or disable Run on login for the current user.
        public static bool TrySetRunOnLogin(bool enabled)
        {
#if WINDOWS
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key is null)
                    return false;

                if (!enabled)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    return true;
                }

                var exePath = Environment.ProcessPath;

                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    return false;

                // Quote path in case of spaces.
                key.SetValue(ValueName, $"\"{exePath}\"");
                return true;
            }
            catch
            {
                return false;
            }
#else
            _ = enabled;
            return false;
#endif
        }

        public static bool IsRunOnLoginEnabled()
        {
#if WINDOWS
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var value = key?.GetValue(ValueName)?.ToString();
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }
    }
}
