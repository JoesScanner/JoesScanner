using System;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;

namespace JoesScanner.Services;

public static class AppClipboard
{
    public static async Task SetTextAsync(string text)
    {
        // Always attempt the cross platform API first.
        try
        {
            await Clipboard.Default.SetTextAsync(text ?? string.Empty);
            return;
        }
        catch
        {
        }

#if WINDOWS
        try
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(text ?? string.Empty);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            return;
        }
        catch
        {
        }
#endif

        // If we are here, all attempts failed.
        throw new InvalidOperationException("Clipboard copy failed.");
    }
}
