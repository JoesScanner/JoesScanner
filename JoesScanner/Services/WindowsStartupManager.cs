using Microsoft.Win32;
using System;
using System.IO;
using System.Threading.Tasks;

namespace JoesScanner.Services
{
    internal static class WindowsStartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "JoesScanner";

#if WINDOWS
        private const string StartupTaskId = "JoesScannerStartupTask";
#endif

        // Best effort: enable or disable Run on login for the current user.
        public static async Task<bool> TrySetRunOnLoginAsync(bool enabled)
        {
#if WINDOWS
            try
            {
                AppLog.Add($"Startup: SetRunOnLogin requested. enabled={enabled}");

                // Store-installed (MSIX) apps cannot rely on writing HKCU\\...\\Run.
                // When the app has package identity, use StartupTask (requires Package.appxmanifest extension).
                if (HasPackageIdentity())
                {
                    AppLog.Add("Startup: App has package identity. Using StartupTask.");
                    return await TrySetStartupTaskAsync(enabled);
                }

                AppLog.Add("Startup: App is unpackaged. Using HKCU Run key.");
                return TrySetRunKey(enabled);
            }
            catch (Exception ex)
            {
                AppLog.Add($"Startup: SetRunOnLogin failed. {ex.GetType().Name}: {ex.Message}");
                return false;
            }
#else
            _ = enabled;
            return false;
#endif
        }

        public static async Task<bool> IsRunOnLoginEnabledAsync()
        {
#if WINDOWS
            try
            {
                if (HasPackageIdentity())
                {
                    var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                    AppLog.Add($"Startup: StartupTask current state={startupTask.State}");
                    return startupTask.State == Windows.ApplicationModel.StartupTaskState.Enabled;
                }

                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var value = key?.GetValue(ValueName)?.ToString();
                AppLog.Add($"Startup: Run key present={!string.IsNullOrWhiteSpace(value)}");
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                AppLog.Add($"Startup: IsRunOnLoginEnabled check failed. {ex.GetType().Name}: {ex.Message}");
                return false;
            }
#else
            return false;
#endif
        }

#if WINDOWS
        private static bool TrySetRunKey(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                AppLog.Add("Startup: Failed to open HKCU Run key.");
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Add("Startup: Removed HKCU Run value.");
                return true;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                AppLog.Add($"Startup: Cannot set HKCU Run value. Invalid exePath='{exePath}'.");
                return false;
            }

            // Quote path in case of spaces.
            key.SetValue(ValueName, $"\"{exePath}\"");
            AppLog.Add($"Startup: Set HKCU Run value to '{exePath}'.");
            return true;
        }

        private static bool HasPackageIdentity()
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> TrySetStartupTaskAsync(bool enabled)
        {
            try
            {
                var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                AppLog.Add($"Startup: StartupTask '{StartupTaskId}' initial state={startupTask.State}");

                if (!enabled)
                {
                    startupTask.Disable();
                    AppLog.Add("Startup: StartupTask disable requested.");
                    return true;
                }

                var state = await startupTask.RequestEnableAsync();
                AppLog.Add($"Startup: StartupTask enable request result state={state}");

                if (state != Windows.ApplicationModel.StartupTaskState.Enabled)
                {
                    AppLog.Add("Startup: StartupTask was not enabled. This is usually because Windows disabled it by user choice, policy, or startup impact settings. Check Windows Settings, Apps, Startup.");
                }
                return state == Windows.ApplicationModel.StartupTaskState.Enabled;
            }
            catch (Exception ex)
            {
                AppLog.Add($"Startup: StartupTask operation failed. {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
#endif
    }
}
