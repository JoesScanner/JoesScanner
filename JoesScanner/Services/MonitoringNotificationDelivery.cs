using System.Threading.Channels;

namespace JoesScanner.Services;

internal sealed class MonitoringNotificationDelivery(
    Func<Task<bool>> allowed, Func<MonitoringAlert, Task> show, Func<string, Task> remove)
{
    private sealed record Work(string Id, MonitoringAlert? Alert, Func<bool>? IsCurrent);
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(128) { SingleReader = true });

    internal void Post(MonitoringAlert alert, Func<bool> isCurrent) =>
        _queue.Writer.TryWrite(new(alert.Id, alert, isCurrent));

    internal void Remove(string id) => _ = _queue.Writer.WriteAsync(new(id, null, null)).AsTask();

    internal async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (work.Alert is { } alert)
                    await SendAsync(alert, work.IsCurrent!, allowed, show).ConfigureAwait(false);
                else
                    await remove(work.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Basic(() => $"Local monitoring notification could not be updated ({DiagnosticRedaction.Describe(ex)}).");
            }
        }
    }

    internal static async Task<bool> SendAsync(MonitoringAlert alert, Func<bool> isCurrent,
        Func<Task<bool>> allowed, Func<MonitoringAlert, Task> show)
    {
        if (!isCurrent() || !await allowed().ConfigureAwait(false) || !isCurrent())
            return false;

        await show(alert).ConfigureAwait(false);
        return true;
    }
}
