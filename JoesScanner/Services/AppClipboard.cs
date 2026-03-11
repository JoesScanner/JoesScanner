using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;

namespace JoesScanner.Services;

public static class AppClipboard
{
    public static async Task SetTextAsync(string text)
    {
        var value = text ?? string.Empty;
        Exception? lastError = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await Clipboard.Default.SetTextAsync(value);
                });

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

#if WINDOWS
            try
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dataPackage.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                    dataPackage.SetText(value);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                    Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                });

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            try
            {
                await SetWindowsClipboardTextWithWin32Async(value);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
#endif

            await Task.Delay(75);
        }

        throw new InvalidOperationException(lastError?.Message ?? "Clipboard copy failed.", lastError);
    }

#if WINDOWS
    private static Task SetWindowsClipboardTextWithWin32Async(string text)
    {
        var tcs = new TaskCompletionSource<object?>();

        var thread = new Thread(() =>
        {
            try
            {
                SetWindowsClipboardTextWithWin32(text);
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.IsBackground = true;
        thread.Name = "JoesScannerClipboardSTA";
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private static void SetWindowsClipboardTextWithWin32(string text)
    {
        const uint cfUnicodeText = 13;
        const uint gmemMoveable = 0x0002;

        var value = text ?? string.Empty;
        var bytes = (value.Length + 1) * 2;
        nint hGlobal = nint.Zero;
        var clipboardOpened = false;

        try
        {
            for (var i = 0; i < 10; i++)
            {
                if (OpenClipboard(nint.Zero))
                {
                    clipboardOpened = true;
                    break;
                }

                Thread.Sleep(25);
            }

            if (!clipboardOpened)
                throw new InvalidOperationException("Clipboard copy failed. Could not open the Windows clipboard.");

            if (!EmptyClipboard())
                throw new InvalidOperationException("Clipboard copy failed. Could not clear the Windows clipboard.");

            hGlobal = GlobalAlloc(gmemMoveable, (nuint)bytes);
            if (hGlobal == nint.Zero)
                throw new InvalidOperationException("Clipboard copy failed. Could not allocate clipboard memory.");

            var target = GlobalLock(hGlobal);
            if (target == nint.Zero)
                throw new InvalidOperationException("Clipboard copy failed. Could not lock clipboard memory.");

            try
            {
                Marshal.Copy(value.ToCharArray(), 0, target, value.Length);
                Marshal.WriteInt16(target, value.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(cfUnicodeText, hGlobal) == nint.Zero)
                throw new InvalidOperationException("Clipboard copy failed. Could not set clipboard data.");

            hGlobal = nint.Zero;
        }
        finally
        {
            if (clipboardOpened)
                CloseClipboard();

            if (hGlobal != nint.Zero)
                GlobalFree(hGlobal);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint hMem);
#endif
}
