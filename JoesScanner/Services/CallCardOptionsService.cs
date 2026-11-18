using System.ComponentModel;
using JoesScanner.Models;

namespace JoesScanner.Services;

public sealed class CallCardOptionsService : INotifyPropertyChanged
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public CallCardOptionsService()
    {
        Ready = LoadAsync();
    }

    public CallCardLayout Layout { get; private set; } = CallCardLayout.Default;
    internal Task Ready { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task LoadAsync()
    {
        try
        {
            var layout = await Task.Run(CallCardLayoutStore.Read).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() => Publish(layout));
        }
        catch (Exception ex)
        {
            AppLog.Add(() => $"Call card options: could not read saved layout ({ex.GetType().Name}).");
        }
    }

    public async Task ApplyAsync(CallCardLayout layout)
    {
        await Ready.ConfigureAwait(false);
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => CallCardLayoutStore.Write(layout)).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() => Publish(layout));
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void Publish(CallCardLayout layout)
    {
        if (Layout.HasSameOptions(layout))
            return;

        Layout = layout;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Layout)));
    }
}
