using System.ComponentModel;
using System.Runtime.CompilerServices;

public abstract class BindableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class MainThread
{
    private static readonly AsyncLocal<Action<Action>?> Posted = new();
    internal static Action<Action>? PostForTest { get => Posted.Value; set => Posted.Value = value; }
    public static bool IsMainThread => true;
    public static void BeginInvokeOnMainThread(Action action)
    {
        if (PostForTest is { } post)
            post(action);
        else
            action();
    }
    public static Task InvokeOnMainThreadAsync(Func<Task> action) => action();
    public static Task<T> InvokeOnMainThreadAsync<T>(Func<T> action) => Task.FromResult(action());

    public static Task InvokeOnMainThreadAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

namespace JoesScanner.Services
{
    internal static class AppLog
    {
        public static void Basic(Func<string> message) { }
        public static void Basic(string message) { }
        public static void Add(Func<string> message) { }
        public static void DebugWriteLine(Func<string> message) { }
        public static void DebugWriteLine(string message) { }
    }

    internal static class AppPaths
    {
        internal static string DatabaseDirectory { get; set; } = string.Empty;
        internal static string GetDbPath(string fileName) => Path.Combine(DatabaseDirectory, fileName);
    }
}
