using System.Diagnostics;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class PersistenceSafetyTests
{
    private const string Server = "https://scanner.example/one";
    private const string Other = "https://scanner.example/two";
    private const string LegacyStopKey = "monitoring_explicitly_stopped";
    private static readonly MobileAudioPreferences Changed =
        new(MobileAudioMode.Exclusive, AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker);

    internal static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("JoesScanner-Persistence-");
        var previous = AppPaths.DatabaseDirectory;
        try
        {
            SetDirectory(root, "intent");
            await LockAndRestartAsync();
            SetDirectory(root, "failed-stop");
            await FailedStopAsync();
            SetDirectory(root, "crash-stop");
            await StopAndTerminateAsync();
            SetDirectory(root, "preferences");
            await AudioPreferencesAsync();
        }
        finally
        {
            AppPaths.DatabaseDirectory = previous;
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static void SetDirectory(DirectoryInfo root, string name) =>
        AppPaths.DatabaseDirectory = root.CreateSubdirectory(name).FullName;

    private static MonitoringIntentStore Store(Action<string>? report = null) => new(LegacyStopKey, report ?? (_ => { }));

    private static async Task LockAndRestartAsync()
    {
        var store = Store();
        AppStateStore.SetBool("last_connected_on_exit", true);
        AppStateStore.SetBool(LegacyStopKey, false);
        Check(!store.CanRestore(Server, null), "Legacy positive flags without a current permit must be ambiguous.");
        await store.SaveStartedAsync(store.BeginStart(), Server, "account-one", default);
        Check(Store().CanRestore(Server, "account-one") && !Store().CanRestore(Other, "account-one") &&
              !Store().CanRestore(Server, "account-two"), "Only the matching recorder and account may restore.");
        var permit = AppPaths.GetDbPath(MonitoringIntentStore.PermitFileName);
        var knownPermit = File.ReadAllText(permit);
        File.WriteAllText(permit, knownPermit[..12]);
        Check(!Store().CanRestore(Server, "account-one"), "An interrupted permit write must fail closed.");
        File.WriteAllText(permit, knownPermit);

        var lifecycle = new MonitoringLifecycle();
        var old = lifecycle.TryBeginStart()!;
        var oldRevision = store.BeginStart();
        Check(!Store().CanRestore(Server, "account-one"), "Startup consumes the previous permit before subscription verification.");
        var subscription = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verification = CompleteStartupAsync();
        using var connection = Open();
        using var writer = connection.BeginTransaction();
        Task? writeStop = null;
        await lifecycle.StopAsync(_ => writeStop = store.SaveStoppedAsync(Server, "account-one"),
            _ => { }, _ => Task.CompletedTask);
        Check(!await verification && !subscription.Task.IsCompleted && !Store().CanRestore(Server, "account-one"),
            "Stop must detach startup and immediately veto restore while SQLite is locked.");
        Check(Store().WasExplicitlyStopped(Server, "account-one", protectedServer: true) &&
              !Store().WasExplicitlyStopped(Server, "account-two", protectedServer: true),
            "A stopped journal scope must survive SQLite contention and stay account-specific.");

        var replacement = lifecycle.TryBeginStart()!;
        var revision = store.BeginStart();
        var reconnect = store.SaveStartedAsync(revision, Server, "account-one", replacement.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(1250));
        writer.Commit();
        await Task.WhenAll(writeStop!, reconnect).WaitAsync(TimeSpan.FromSeconds(10));
        subscription.SetResult();
        await store.SaveStartedAsync(oldRevision, Other, "account-two", default);
        Check(lifecycle.Activate(replacement) && Store().CanRestore(Server, "account-one") &&
              !Store().CanRestore(Other, "account-two"), "An immediate explicit Start must win over stale writes and callbacks.");
        Console.WriteLine("PASS Monitoring restart authorization, startup Stop, busy SQLite, and stale callback fencing");

        async Task<bool> CompleteStartupAsync()
        {
            try
            {
                await old.WaitForInitializationAsync(subscription.Task);
                await store.SaveStartedAsync(oldRevision, Server, "account-one", old.Token);
                return lifecycle.Activate(old);
            }
            catch (OperationCanceledException) when (old.Token.IsCancellationRequested) { return false; }
        }
    }

    private static async Task FailedStopAsync()
    {
        var issues = new List<string>();
        var store = Store(issues.Add);
        await store.SaveStartedAsync(store.BeginStart(), Server, null, default);
        var oldSnapshot = AppStateStore.GetString(MonitoringIntentStore.SnapshotKey);
        using (var connection = Open())
            Execute(connection, $"""
                CREATE TRIGGER refuse_stop BEFORE UPDATE ON app_settings
                WHEN NEW.setting_key = '{MonitoringIntentStore.SnapshotKey}'
                BEGIN SELECT RAISE(ABORT, 'test refuses the SQLite intent write'); END;
                """);
        await store.SaveStoppedAsync(Server, null);
        Check(issues.Count > 0 && AppStateStore.GetString(MonitoringIntentStore.SnapshotKey) == oldSnapshot &&
              !Store().CanRestore(Server, null) && Store().WasExplicitlyStopped(Server, null, protectedServer: false),
            "An aborted SQLite Stop transaction must report failure and leave restoration vetoed.");
        Console.WriteLine("PASS Failed SQLite Stop transaction leaves the previous connection unauthorized after reopening");
    }

    private static async Task StopAndTerminateAsync()
    {
        var store = Store();
        await store.SaveStartedAsync(store.BeginStart(), Server, null, default);
        var oldSnapshot = AppStateStore.GetString(MonitoringIntentStore.SnapshotKey);
        await TerminateAtAsync("stop");
        Check(AppStateStore.GetString(MonitoringIntentStore.SnapshotKey) == oldSnapshot &&
              !Store().CanRestore(Server, null) && Store().WasExplicitlyStopped(Server, null, protectedServer: false),
            "Hard termination after Stop with a blocked SQLite writer must not reauthorize the old run.");
        Console.WriteLine("PASS Abrupt process termination during Stop preserves the independent reconnect veto");
    }

    private static async Task AudioPreferencesAsync()
    {
        var initial = MobileAudioPreferences.Default;
        AppStateStore.SetString(MobileAudioPreferencesStore.LegacyMixKey, "1");
        AppStateStore.SetString(MobileAudioPreferencesStore.DisconnectKey, initial.OnDisconnect.ToString());
        var migrated = MobileAudioPreferencesStore.Read(isIos: true);
        Check(migrated == initial with { Mode = MobileAudioMode.MixNormally } &&
              MobileAudioPreferencesStore.Read(isIos: false) == migrated,
            "Atomic migration must distinguish legacy true and persist the complete selected snapshot.");
        MobileAudioPreferencesStore.Write(initial);

        using (var connection = Open())
        {
            Execute(connection, $"""
                CREATE TRIGGER reject_disconnection BEFORE UPDATE ON app_settings
                WHEN NEW.setting_key = '{MobileAudioPreferencesStore.DisconnectKey}'
                BEGIN SELECT RAISE(ABORT, 'test interrupts the second preference upsert'); END;
                """);
            try
            {
                MobileAudioPreferencesStore.Write(Changed);
                throw new InvalidOperationException("The injected audio failure must reach the caller.");
            }
            catch (SqliteException ex) when (ex.Message.Contains("test interrupts the second preference upsert")) { }
            Check(MobileAudioPreferencesStore.Read(isIos: true) == initial,
                "A failure between the two upserts must roll back the mode with the disconnection preference.");
            Execute(connection, "DROP TRIGGER reject_disconnection;");
        }

        await TerminateAtAsync("audio-before");
        Check(MobileAudioPreferencesStore.Read(isIos: true) == initial,
            "Termination during the audio transaction must keep the old complete snapshot.");
        await TerminateAtAsync("audio-after");
        Check(MobileAudioPreferencesStore.Read(isIos: true) == Changed,
            "Termination after commit must keep the new complete snapshot.");
        Console.WriteLine("PASS Atomic mobile preferences roll back a second-write failure and survive a process crash");

        var clip = new IosRecordingState();
        clip.Activated(Changed.Mode);
        MobileAudioPreferences[] permitted =
        [
            initial, Changed,
            new(MobileAudioMode.MixNormally, AudioDisconnectionBehavior.PausePlayback)
        ];
        var writing = Task.Run(() =>
        {
            for (var index = 0; index < 30; index++)
                MobileAudioPreferencesStore.Write(permitted[index % permitted.Length]);
        });
        for (var read = 0; read < 30; read++)
        {
            var saved = MobileAudioPreferencesStore.Read(isIos: true);
            Check(permitted.Contains(saved) && clip.ModeForActivation(saved.Mode) == Changed.Mode,
                "Readers must see a committed pair and an active or resumed clip must keep its original mode.");
        }
        await writing;
        var nextClip = new IosRecordingState();
        Check(nextClip.ModeForActivation(MobileAudioPreferencesStore.Read(isIos: true).Mode) == permitted[^1].Mode,
            "The next clip must see the latest saved snapshot.");
        Console.WriteLine("PASS Rapid audio changes retain a consistent snapshot and apply mode at the next clip boundary");
    }

    private static async Task TerminateAtAsync(string stage)
    {
        var launch = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        launch.ArgumentList.Add(typeof(PersistenceSafetyTests).Assembly.Location);
        launch.ArgumentList.Add("--settings-crash-probe");
        launch.ArgumentList.Add(AppPaths.DatabaseDirectory);
        launch.ArgumentList.Add(stage);
        using var child = Process.Start(launch) ?? throw new InvalidOperationException("Could not start the persistence crash probe.");
        try
        {
            Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == stage,
                "The child must reach its durable-write crash boundary.");
        }
        finally
        {
            if (!child.HasExited)
                child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    internal static async Task RunCrashProbeAsync(string directory, string stage)
    {
        AppPaths.DatabaseDirectory = directory;
        if (stage == "stop")
        {
            using var connection = Open();
            using var writer = connection.BeginTransaction();
            _ = Store().SaveStoppedAsync(Server, null);
            Console.WriteLine(stage);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        else if (stage == "audio-before")
        {
            AppStateStore.InTransaction(transaction =>
            {
                MobileAudioPreferencesStore.Write(transaction, Changed);
                Console.WriteLine(stage);
                Thread.Sleep(Timeout.Infinite);
                return true;
            }, durable: true);
        }
        else
        {
            MobileAudioPreferencesStore.Write(Changed);
            Console.WriteLine(stage);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = AppPaths.GetDbPath("joesscanner.db") }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }
}
