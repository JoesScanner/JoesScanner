using System;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace JoesScanner.Helpers
{
    public static class ServiceHelper
    {
        public static T GetService<T>() where T : class =>
            Current.GetService<T>() ?? throw new InvalidOperationException($"Service {typeof(T)} not found.");

        public static IServiceProvider Current =>
            Application.Current?.Handler?.MauiContext?.Services
            ?? throw new InvalidOperationException("MauiContext is not available.");
    }
}
