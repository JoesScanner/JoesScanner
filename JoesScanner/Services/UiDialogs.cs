using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace JoesScanner.Services
{
    // Centralized dialog helper.
    // RootPage hosts tab content as Views, so many ContentPages are not part of the active Window.
    // Calling DisplayAlert or DisplayActionSheet on those detached pages can fail.
    // Always present dialogs from the active visual root (Shell or window root page).
    internal static class UiDialogs
    {
        private static Page? GetHostPage()
        {
            try
            {
                if (Shell.Current is Page shellPage)
                    return shellPage;

                var app = Application.Current;
                var windows = app?.Windows;
                if (windows != null && windows.Count > 0 && windows[0].Page is Page windowPage)
                    return windowPage;

#pragma warning disable CS0618
                if (app?.MainPage is Page mainPage)
                    return mainPage;
#pragma warning restore CS0618
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

                await host.DisplayAlertAsync(title, message, cancel);
            });
        }

        public static Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var host = GetHostPage();
                if (host == null)
                    return false;

                return await host.DisplayAlertAsync(title, message, accept, cancel);
            });
        }

        public static Task<string?> ActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var host = GetHostPage();
                if (host == null)
                    return null;

                return await host.DisplayActionSheetAsync(title, cancel, destruction, buttons);
            });
        }

        public static Task<string?> PromptAsync(
            string title,
            string message,
            string accept,
            string cancel,
            string? initialValue = null,
            int maxLength = 64,
            Keyboard? keyboard = null)
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
                    keyboard: keyboard ?? Keyboard.Default,
                    initialValue: initialValue);
            });
        }
    }
}
