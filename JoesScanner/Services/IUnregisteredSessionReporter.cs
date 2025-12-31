namespace JoesScanner.Services
{
    public interface IUnregisteredSessionReporter
    {
        void OnAppStarted();
        void OnAppStopping();

        System.Threading.Tasks.Task TryFlushQueueAsync(System.Threading.CancellationToken cancellationToken);
    }
}
