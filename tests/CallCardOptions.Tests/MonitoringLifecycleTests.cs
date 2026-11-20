using JoesScanner.Services;

internal static class MonitoringLifecycleTests
{
    internal static async Task RunAsync()
    {
        await StopDuringInitializationAsync();
        await StopDuringNotificationPromptAsync();
        StopAtIosClipBoundary();
        await ReplaceAndroidServiceBeforeBackgroundingAsync();
        await RepeatedStopAndRestartAsync();
        await StopPreventsQueuedRecoveryAsync();
    }

    private static async Task StopDuringInitializationAsync()
    {
        var lifecycle = new MonitoringLifecycle();
        var old = Start(lifecycle);
        var initialization = new TaskCompletionSource();
        var activated = CompleteStartupAsync(lifecycle, old, initialization.Task);
        var stops = 0;
        await lifecycle.StopAsync(run =>
        {
            Check(run == old && old.Token.IsCancellationRequested, "The startup must already be canceled when Stop updates the UI.");
            stops++;
        }, _ => { }, _ => Task.CompletedTask);

        Check(stops == 1 && lifecycle.ExplicitlyStopped && lifecycle.Current == null, "Stop must record intent during startup.");
        Check(!initialization.Task.IsCompleted && !await activated, "Stop must detach startup from pending initialization.");
        var replacement = Start(lifecycle);
        initialization.SetResult();
        await lifecycle.AbortStartAsync(old, _ => throw new Exception("Retired startup still owns teardown."), _ => Task.CompletedTask);
        Check(lifecycle.Activate(replacement) && lifecycle.CanRecover(replacement), "Late initialization must leave the replacement intact.");
        await StopAsync(lifecycle);
        Console.WriteLine("PASS Stop cancels startup immediately and ignores delayed initialization from the old run");
    }

    private static async Task StopDuringNotificationPromptAsync()
    {
        var lifecycle = new MonitoringLifecycle();
        var prompt = new TaskCompletionSource();
        var old = Start(lifecycle);
        var oldStart = CompleteStartupAsync(lifecycle, old, prompt.Task);
        await StopAsync(lifecycle);

        Check(!prompt.Task.IsCompleted && !await oldStart, "Stop must complete independently of the OS prompt.");
        var replacement = Start(lifecycle);
        var newStart = CompleteStartupAsync(lifecycle, replacement, prompt.Task);
        Check(!newStart.IsCompleted, "A replacement may wait on the still-visible prompt.");
        prompt.SetResult();
        Check(await newStart && lifecycle.CanRecover(replacement) && !lifecycle.CanRecover(old),
            "The shared prompt result may activate only the replacement.");
        await StopAsync(lifecycle);
        Console.WriteLine("PASS Stop detaches an active notification permission prompt and restart can reuse it");
    }

    private static void StopAtIosClipBoundary()
    {
        var commands = new MonitoringTransportOwnership();
        commands.Publish(1);
        var command = commands.Capture()!;
        Check(commands.CanDispatch(command, 1, stopsMonitoring: false), "The original clip must accept transport commands.");
        commands.Clear();
        Check(commands.CanDispatch(command, 1, stopsMonitoring: true), "Stop must still reach its run during a silent gap.");
        Check(!commands.CanDispatch(command, 1, stopsMonitoring: false), "Clip commands must expire when the clip finishes.");
        commands.Publish(1);
        Check(commands.CanDispatch(command, 1, stopsMonitoring: true), "Stop must also work after the next clip publishes.");
        Check(!commands.CanDispatch(command, 1, stopsMonitoring: false), "The next clip must reject the previous clip's commands.");
        commands.Publish(2);
        Check(!commands.CanDispatch(command, 2, stopsMonitoring: true), "A late Stop must never stop a replacement run.");
        Check(!commands.CanDispatch(command, 0, stopsMonitoring: true), "A late Stop must not apply to an idle session.");
        Console.WriteLine("PASS iOS Stop survives a clip boundary while clip commands and old-run Stop expire");
    }

    private static async Task ReplaceAndroidServiceBeforeBackgroundingAsync()
    {
        var lifecycle = new MonitoringLifecycle();
        var foreground = new AndroidForegroundOwnership();
        var old = Start(lifecycle);
        var oldService = new object();
        foreground.SetActivityVisible(true);
        foreground.Begin(old.Id);
        Check(foreground.TryRequest(), "The old run must request promotion while visible.");
        Check(foreground.Promote(old.Id, oldService) && lifecycle.Activate(old), "The old run should be active and ready.");

        var drain = new TaskCompletionSource();
        var stop = lifecycle.StopAsync(_ => { }, run =>
        {
            Check(foreground.End(run.Id) && !foreground.IsReady, "Stop must retire foreground ownership before audio has drained.");
        }, _ => drain.Task);
        var replacement = Start(lifecycle);
        foreground.Begin(replacement.Id);
        Check(!replacement.PreviousCleanup.IsCompleted, "The old run must still be draining.");
        Check(!foreground.IsReady && foreground.TryRequest(),
            "The new run must request its own promotion while the activity is still visible.");
        foreground.SetActivityVisible(false);
        Check(!foreground.TryRequest(), "Backgrounding must neither issue nor replace the pending request.");

        var newService = new object();
        foreground.RequestFailed(old.Id);
        foreground.SetActivityVisible(true);
        Check(!foreground.End(old.Id) && !foreground.TryRequest(),
            "A delayed failure or Stop from the old run must leave the replacement request pending.");
        foreground.SetActivityVisible(false);
        Check(!foreground.Promote(old.Id, oldService) && !foreground.IsReady, "A delayed old start must never confer readiness.");
        Check(foreground.Promote(replacement.Id, newService), "The request placed while visible can finish after backgrounding.");
        foreground.Destroy(old.Id, oldService);
        foreground.Destroy(replacement.Id, oldService);
        Check(foreground.IsReady, "A late OnDestroy from another run or instance must leave the replacement ready.");
        drain.SetResult();
        await stop;
        await replacement.PreviousCleanup;
        Check(lifecycle.Activate(replacement) && foreground.IsReady, "Completing old cleanup must preserve the replacement's readiness.");
        foreground.Destroy(replacement.Id, newService);
        Check(!foreground.IsReady && !foreground.TryRequest(), "Losing the current service revokes readiness.");
        foreground.SetActivityVisible(true);
        Check(foreground.TryRequest(), "Returning to the foreground can request promotion again.");
        await lifecycle.StopAsync(_ => { }, run => foreground.End(run.Id), _ => Task.CompletedTask);
        Console.WriteLine("PASS Rapid Android Stop and Start request fresh promotion before lock and ignore old service callbacks");
    }

    private static async Task RepeatedStopAndRestartAsync()
    {
        var lifecycle = new MonitoringLifecycle();
        var run = Start(lifecycle);
        lifecycle.Activate(run);
        var initialRevision = lifecycle.StopRevision;
        var stops = 0;
        var cleanups = 0;
        var drain = new TaskCompletionSource();
        var firstStop = lifecycle.StopAsync(_ => stops++, _ => { }, _ =>
        {
            cleanups++;
            return drain.Task;
        });
        var scheduledRestartRevision = lifecycle.StopRevision;
        var secondStop = lifecycle.StopAsync(_ => stops++, _ => { }, _ => throw new Exception("Cleanup repeated."));
        Check(stops == 1 && cleanups == 1 && !secondStop.IsCompleted, "Repeated Stop must join existing cleanup.");
        Check(lifecycle.StopRevision != scheduledRestartRevision, "A later user Stop must invalidate a scheduled restart.");
        Check(lifecycle.TryBeginStart(initialRevision) == null, "An auto start queued before Stop must be rejected.");
        drain.SetResult();
        await Task.WhenAll(firstStop, secondStop);
        var next = Start(lifecycle);
        Check(lifecycle.Activate(next) && lifecycle.CanRecover(next) && !lifecycle.ExplicitlyStopped, "A later explicit start must work normally.");
        await StopAsync(lifecycle);
        Console.WriteLine("PASS Repeated Stop shares teardown, vetoes scheduled restart, and allows later explicit start");
    }

    private static async Task StopPreventsQueuedRecoveryAsync()
    {
        var lifecycle = new MonitoringLifecycle();
        var old = Start(lifecycle);
        lifecycle.Activate(old);
        var playback = new LivePlaybackRequests();
        using var oldSelection = playback.Begin(manual: true);
        var trigger = new TaskCompletionSource();
        async Task<bool> QueuedRecoveryAsync()
        {
            await trigger.Task;
            return lifecycle.CanRecover(old);
        }

        Check(lifecycle.CanRecover(old), "Interruption recovery is allowed during the active run.");
        var recovery = QueuedRecoveryAsync();
        await lifecycle.StopAsync(_ => playback.RetireCurrent(), _ => { }, _ => Task.CompletedTask);
        Check(!oldSelection.IsCurrent && !oldSelection.CanContinue && oldSelection.Token.IsCancellationRequested,
            "Explicit Stop must invalidate the old manual selection before cancellation completes.");
        Check(!lifecycle.CanRecover(old), "Explicit Stop must disable recovery immediately.");
        var next = Start(lifecycle);
        lifecycle.Activate(next);
        using var newSelection = playback.Begin(manual: true);
        trigger.SetResult();
        Check(!await recovery && newSelection.CanContinue && lifecycle.CanRecover(next),
            "A recovery callback queued before Stop must not affect the new run.");
        await StopAsync(lifecycle);
        Console.WriteLine("PASS Explicit Stop invalidates playback and prevents delayed automatic queue recovery");
    }

    private static MonitoringLifecycle.Run Start(MonitoringLifecycle lifecycle) =>
        lifecycle.TryBeginStart() ?? throw new Exception("Expected to start monitoring.");

    private static Task StopAsync(MonitoringLifecycle lifecycle) =>
        lifecycle.StopAsync(_ => { }, _ => { }, _ => Task.CompletedTask);

    private static async Task<bool> CompleteStartupAsync(MonitoringLifecycle lifecycle, MonitoringLifecycle.Run run, Task initialization)
    {
        try
        {
            await run.WaitForInitializationAsync(initialization);
            run.Token.ThrowIfCancellationRequested();
            return lifecycle.Activate(run);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
