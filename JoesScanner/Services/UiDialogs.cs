using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace JoesScanner.Services
{
    // Centralized dialog helper.
    // RootPage hosts tab content as Views, so many ContentPages are not part of the active Window.
    // Calling DisplayAlert / DisplayActionSheet on those detached pages can fail.
    // Always present dialogs from the active visual root (Shell or MainPage).
    internal static class UiDialogs
    {
        private static Page? GetHostPage()
        {
            try
            {
                if (Shell.Current is Page shellPage)
                    return shellPage;

                if (Application.Current?.MainPage is Page mainPage)
                    return mainPage;
            }
            catch
            {
            }

            return null;
        }

        public static Task AlertAsync(string title, string message, string cancel)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var host = GetHostPage();
                if (host == null)
                    return;

                await host.DisplayAlert(title, message, cancel);
            });
        }

        public static Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var host = GetHostPage();
                if (host == null)
                    return false;

                return await host.DisplayAlert(title, message, accept, cancel);
            });
        }

        public static Task<string?> ActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var host = GetHostPage();
                if (host == null)
                    return null;

                return await host.DisplayActionSheet(title, cancel, destruction, buttons);
            });
        }

        public static Task<string?> PromptAsync(
            string title,
            string message,
            string accept,
            string cancel,
            string? initialValue = null,
            int maxLength = 64)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var host = GetHostPage();
                if (host == null)
                    return null;

                return await host.DisplayPromptAsync(
                    title: title,
                    message: message,
                    accept: accept,
                    cancel: cancel,
                    placeholder: null,
                    maxLength: maxLength,
                    keyboard: Keyboard.Default,
                    initialValue: initialValue);
            });
        }
    }
}
