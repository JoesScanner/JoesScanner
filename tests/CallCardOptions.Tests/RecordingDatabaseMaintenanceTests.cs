using System.Text.Json;
using JoesScanner.Data;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static class RecordingDatabaseMaintenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);
    private const string Server = "https://scanner.example/one";
    private const string Other = "https://scanner.example/two";
    private const string Signed = "https://scanner.example/clip.wav?testLease=fake";

    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("JoesScanner.DbMaintenance-");
        try
        {
            await WriteAndRestoreAsync(new Paths(Path.Combine(directory.FullName, "writes.sqlite")));
            await VerifyMetadataOwnershipAsync(new Paths(Path.Combine(directory.FullName, "ownership.sqlite")));
            await InterruptedCleanupAsync(new Paths(Path.Combine(directory.FullName, "migration.sqlite")));
            await RetentionAsync(new Paths(Path.Combine(directory.FullName, "retention.sqlite")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task WriteAndRestoreAsync(Paths paths)
    {
        const string customPath = "Audio Archive/Channel @North/crew's%20call.mp3";
        var unsafeReferences = new[]
        {
            Signed,
            "https://test-user:test-pass@scanner.example/clip.wav",
            "//test-user:test-pass@scanner.example/clip.wav",
            "http://@scanner.example/clip.wav",
            "clip.wav%253FtestLease=fake",
            "clip.wav&#63;testLease=fake",
            "Authorization: Bearer fake",
            "Audio/Authorization %3D Bearer fake",
            "Bearer%20fake",
            "/Audio/Basic%20fake",
            "/Audio/clip.wav%0D%0AAuthorization%3A%20Basic%20fake",
            "https://scanner.example/path/test-user:test-pass@scanner.example/clip.wav"
        };
        Check(RecordingStorageReference.ForCache(customPath) == customPath &&
            RecordingStorageReference.ForJournal(Server + "/crew's%20call.mp3") != null &&
            RecordingStorageReference.ForCache("//cdn.example/audio/clip.wav") == "//cdn.example/audio/clip.wav",
            "Ordinary absolute, relative and network paths must remain usable.");
        Check(unsafeReferences.All(value => RecordingStorageReference.ForCache(value) == null &&
            RecordingStorageReference.ForJournal(value) == null),
            "Sensitive or ambiguous audio locations must stay out of both stores.");
        var diagnostic = DiagnosticRedaction.Sanitize(
            "Audio https://test-user:test-pass@scanner.example/private-path?testLease=fake //cdn.example/key-fragment?signature=fake " +
            """{"Authorization":"Bearer short","session_token":"short"}""");
        Check(diagnostic.Contains("scanner.example", StringComparison.Ordinal) &&
            !new[] { "test-user", "test-pass", "private-path", "testLease", "key-fragment", "signature", "short" }
                .Any(value => diagnostic.Contains(value, StringComparison.Ordinal)) &&
            DiagnosticRedaction.Sanitize(diagnostic) == diagnostic,
            "Diagnostic output must omit URL credentials and authorization headers and be safe to sanitize twice.");

        using var cache = new LocalCallsRepository(paths);
        var firstScope = new RecordingMetadataScope(null, "https://test-user:test-pass@scanner.example/one?discard=fake", false, null);
        var otherScope = new RecordingMetadataScope(null, Other, false, null);
        await cache.UpsertCallsAsync(firstScope,
            unsafeReferences.Select((value, i) => Cached("unsafe-" + i, value)).Append(Cached("same", customPath)).ToArray());
        await cache.UpsertCallsAsync(otherScope, [Cached("same", "/other/clip.wav")]);
        await using var db = await OpenAsync(paths);
        Check(Count(db, "SELECT count(*) FROM calls WHERE audio_filename IS NOT NULL") == 2 &&
            Count(db, "SELECT count(*) FROM calls WHERE server_key IN ($one, $two)", ("$one", firstScope.StorageKey), ("$two", otherScope.StorageKey))
                == unsafeReferences.Length + 2,
            "The real cache writer must omit credentials and isolate recorder base paths.");

        var queue = new DurableLiveQueue(paths);
        var run = new DurableLiveQueue.Run(Server, 11, null);
        await queue.OpenAsync(run, Now, default);
        var observation = Snapshot(Signed);
        await queue.ObserveAsync(run, "same", Now, observation, default, RecordingOrigin.Live);
        Check(observation.AudioUrl == Signed, "Persistence must preserve the transient URL for immediate playback.");
        var restarted = run with { Owner = 12 };
        var reopened = new DurableLiveQueue(paths);
        await reopened.OpenAsync(restarted, Now.AddMinutes(1), default);
        var recovered = await reopened.NextAsync(restarted, 0, default);
        Check(recovered is { Id: "same", Snapshot: { AudioUrl: null, Metadata.Talkgroup: "Dispatch" } } &&
            recovered.ToCallItem(Server) is { BackendId: "same", AudioUrl: "" },
            "Restart must retain a resolvable identity and metadata for a fresh by-ID acquisition.");
        var fresh = Snapshot(Server + "/clip.wav?testLease=fresh");
        Check(await reopened.ObserveAsync(restarted, "same", null, fresh, default, existingOnly: true) &&
            fresh.AudioUrl != null && await reopened.IsPendingAsync(restarted, "same", 0, default),
            "A rotated signed reference must advance its revision while its URL remains transient and the recording stays pending.");
        Console.WriteLine("PASS SQLite writers exclude signed and credential-bearing locations while preserving ordinary paths and restart identity");
    }

    private static async Task VerifyMetadataOwnershipAsync(Paths paths)
    {
        using var cache = new LocalCallsRepository(paths);
        await cache.InitializeAsync();
        var recorder = Guid.NewGuid();
        var alpha = new RecordingMetadataScope(recorder, Server, false, "alpha");
        var bravo = new RecordingMetadataScope(recorder, Server, false, "bravo");
        var other = new RecordingMetadataScope(recorder, Other, false, "alpha");
        var hosted = new RecordingMetadataScope(recorder, Server, true, "alpha");
        DbCallRecord Row(string group, string? text = null, string time = "100") => new()
        {
            ServerKey = Server, BackendId = "shared", StartTimeUtc = time,
            TargetLabel = group, Transcription = text, ReceivedAtUtc = Now, UpdatedAtUtc = Now
        };
        await cache.UpsertCallsAsync(alpha, [Row("Alpha", "Alpha transcript")]);
        await cache.UpsertCallsAsync(bravo, [Row("Bravo")]);
        await cache.UpsertCallsAsync(other, [Row("Other archive")]);
        await cache.UpsertCallsAsync(hosted, [Row("Hosted")]);
        await cache.UpsertCallsAsync(alpha, [Row("Reused ID", "Other time", "101")]);

        // Stop a real writer immediately before SQLite commits. Queue another owner's
        // write while the transaction is open, then retire (or cancel) the blocked writer.
        foreach (var retire in new[] { false, true })
        {
            using var reached = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var cancel = new CancellationTokenSource();
            var owner = new RecorderOperation();
            var blocked = new RecordingMetadataScope(recorder, Server, false, "alpha", (commit, token) =>
            {
                reached.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Timed out before SQLite commit.");
                owner.Apply(commit, token);
            });
            var pending = retire ? cache.MarkTranscriptionUpdateNotifiedAsync(blocked, "shared", cancel.Token)
                : cache.UpsertCallsAsync(blocked, [Row("Stale", "Late text")], cancel.Token);
            if (!reached.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("SQLite transaction did not reach commit.");
            var unrelated = cache.UpsertCallsAsync(other, [Row("Unrelated current")]);
            var obsolete = cache.UpsertCallsAsync(blocked, [Row("Queued stale")], cancel.Token);
            if (retire)
                owner.Retire();
            else
                cancel.Cancel();
            release.Set();
            foreach (var task in new[] { pending, obsolete })
            {
                try
                {
                    await task;
                    throw new InvalidOperationException("Obsolete metadata mutation was accepted.");
                }
                catch (Exception ex) when (ex is OperationCanceledException or RecordingAccessException) { }
            }
            await unrelated;
        }

        await using var db = await OpenAsync(paths);
        Check(Count(db, "SELECT count(*) FROM calls WHERE backend_id='shared'") == 4 &&
            Count(db, "SELECT count(*) FROM calls WHERE server_key=$key AND target_label='Alpha' AND transcription='Alpha transcript' AND transcription_update_notified_at_utc IS NULL", ("$key", alpha.StorageKey)) == 1 &&
            Count(db, "SELECT count(*) FROM calls WHERE server_key=$key AND target_label='Bravo' AND transcription IS NULL", ("$key", bravo.StorageKey)) == 1 &&
            Count(db, "SELECT count(*) FROM calls WHERE server_key=$key AND target_label='Unrelated current'", ("$key", other.StorageKey)) == 1 &&
            Count(db, "SELECT count(*) FROM calls WHERE server_key=$key AND target_label='Hosted'", ("$key", hosted.StorageKey)) == 1,
            "Each account and archive must retain only its own metadata, including across cancellation at commit.");
        Console.WriteLine("PASS Scoped SQLite metadata rejects retired and canceled commits, queued writes, and overlapping recording timestamps");
    }

    private static async Task InterruptedCleanupAsync(Paths paths)
    {
        await using var db = await OpenAsync(paths);
        var queue = new DurableLiveQueue(paths);
        var run = new DurableLiveQueue.Run(Server, 21, null);
        await queue.OpenAsync(run, Now, default);
        Execute(db, """
            INSERT INTO calls(server_key,backend_id,audio_filename,target_label,received_at_utc,updated_at_utc)
            VALUES ('https://test-user:test-pass@scanner.example/one?discard=fake','old',$url,'Legacy Dispatch',$now,$now);
            INSERT INTO live_queue_recordings (server,recording_id,recorded_utc,first_owner,payload,audio_ready,origin)
            VALUES ($server,'old',$ticks,$owner,$payload,1,1);
            INSERT INTO app_settings (setting_key,setting_value,updated_utc)
            VALUES ('auth_session_token','synthetic-process-token',$now),
                   ('telemetry_last_session_token','synthetic-process-token',$now),
                   ('kept.preference','keep',$now);
            INSERT INTO telemetry_queue(created_utc,event_type,json_payload) VALUES ($now,'connection_status',$event);
            CREATE TRIGGER abort_audio_cleanup BEFORE INSERT ON app_settings
                WHEN NEW.setting_key = 'db_recording_cleanup_v1_calls'
                BEGIN SELECT RAISE(ABORT, 'injected interruption'); END;
            """, ("$url", Signed), ("$now", Now.ToString("O")), ("$server", Server), ("$ticks", Now.UtcTicks),
            ("$owner", run.Owner), ("$payload", JsonSerializer.Serialize(Snapshot(Signed))), ("$event", """
                {"Data":{"stream_server_url":"https://test-user:test-pass@scanner.example/one?discard=fake",
                         "detail":"download failed https://scanner.example/clip.wav?testLease=fake"}}
                """));
        Check((await queue.NextAsync(run, 0, default))?.Snapshot?.AudioUrl == null,
            "Legacy URLs must be withheld from playback even before background cleanup runs.");
        using (var interrupted = new DbMaintenanceService(paths))
        {
            try
            {
                await interrupted.RunOnceAsync(Now, default);
                throw new InvalidOperationException("The interruption trigger was expected to abort this batch.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { }
        }
        Check(Count(db, "SELECT count(*) FROM calls WHERE audio_filename IS NOT NULL") == 1 &&
            Count(db, "SELECT count(*) FROM app_settings WHERE setting_key LIKE 'db_recording_cleanup_v1_%'") == 0,
            "A failed checkpoint must roll back the row changes and leave the migration retryable.");
        Execute(db, "DROP TRIGGER abort_audio_cleanup;");
        using var replacement = new DbMaintenanceService(paths);
        await replacement.RunOnceAsync(Now, default);
        await replacement.RunOnceAsync(Now, default);
        Check(Count(db, """
            SELECT count(*) FROM calls WHERE server_key = $server AND backend_id='old'
                AND audio_filename IS NULL AND target_label='Legacy Dispatch'
            """, ("$server", Server)) == 1 &&
            Count(db, """
            SELECT count(*) FROM live_queue_recordings WHERE recording_id='old' AND disposition=0
                AND audio_ready=0 AND json_extract(payload,'$.AudioUrl') IS NULL
                AND json_extract(payload,'$.Metadata.Talkgroup')='Dispatch'
            """) == 1 &&
            Count(db, "SELECT count(*) FROM app_settings WHERE setting_key IN ('auth_session_token','telemetry_last_session_token')") == 0 &&
            Count(db, "SELECT count(*) FROM app_settings WHERE setting_key='kept.preference' AND setting_value='keep'") == 1 &&
            Count(db, """
            SELECT count(*) FROM telemetry_queue
            WHERE json_extract(json_payload,'$.Data.stream_server_url')=$server
                AND json_extract(json_payload,'$.Data.detail')='download failed https://scanner.example'
            """, ("$server", Server)) == 1,
            "A restarted batch must scrub both stores and obsolete session tokens while preserving identity, state and settings.");
        Console.WriteLine("PASS Interrupted legacy sanitization rolls back atomically and can resume without changing unrelated metadata");
    }

    private static async Task RetentionAsync(Paths paths)
    {
        await using var db = await OpenAsync(paths);
        var queue = new DurableLiveQueue(paths);
        var run = new DurableLiveQueue.Run(Server, 31, null);
        var other = run with { Server = Other };
        var hosted = new DurableLiveQueue.Run("https://app.joesscanner.com", 31, 2, Protected: true, Account: "one");
        var otherAccount = hosted with { Account = "two" };
        foreach (var scope in new[] { run, other, hosted, otherAccount })
            await queue.OpenAsync(scope, Now, default);
        var old = Now.AddDays(-10);
        Execute(db, """
            WITH RECURSIVE numbers(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM numbers WHERE i<620)
            INSERT INTO live_queue_recordings(server, recording_id, recorded_utc, first_owner, payload, audio_ready, disposition, origin)
            SELECT $server, printf('%04d',i), $ticks+i,$owner,$data,
              CASE WHEN i<=420 THEN 1 ELSE 0 END, CASE WHEN i>420 THEN 0 WHEN i % 2=0 THEN 1 ELSE 2 END, 1 FROM numbers;
            WITH RECURSIVE numbers(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM numbers WHERE i<300)
            INSERT INTO calls(server_key,backend_id,received_at_utc,updated_at_utc)
            SELECT $server, printf('%04d',i),$old,$old FROM numbers;
            """, ("$server", Server), ("$ticks", old.UtcTicks), ("$owner", run.Owner),
            ("$data", JsonSerializer.Serialize(Snapshot(null))), ("$old", old.ToString("O")));
        await queue.ObserveAsync(other, "0001", old, Snapshot(Other + "/clip.wav"), default, RecordingOrigin.Live);
        await queue.ObserveAsync(hosted, "shared", old, Snapshot(Signed), default, RecordingOrigin.History);
        await queue.ObserveAsync(otherAccount, "other-account", old, Snapshot(null), default, RecordingOrigin.History);
        var head = (await queue.NextAsync(run, 0, default))!;
        Check(head.Id == "0421" && head.Snapshot?.AudioUrl == null, "The old pending head must await audio.");
        await queue.MarkPlayingAsync(run, head.Id, default);
        await queue.MarkAsync(run, head.Id, PlaybackOutcome.Failed, default);
        await queue.MarkAsync(run, head.Id, PlaybackOutcome.Canceled, default);

        using var maintenance = new DbMaintenanceService(paths);
        var more = await maintenance.RunOnceAsync(Now, default);
        Check(more && Count(db, "SELECT count(*) FROM calls") == 300 - DbMaintenanceService.BatchSize &&
            Count(db, "SELECT count(*) FROM live_queue_recordings WHERE disposition<>0 AND payload IS NULL") == DbMaintenanceService.BatchSize,
            "One maintenance pass must limit each table to a small batch.");
        const string ceilingQuery = """
            SELECT json_extract(setting_value,'$[1]') FROM app_settings
            WHERE setting_key='db_recording_cleanup_v1_live_queue_recordings'
            """;
        var ceiling = Count(db, ceilingQuery);
        var freshRun = run with { Server = "https://scanner.example/new" };
        await queue.OpenAsync(freshRun, Now, default);
        await queue.ObserveAsync(freshRun, "0001", Now, Snapshot(Signed), default, RecordingOrigin.Live);
        await Task.WhenAll(Task.Run(() => maintenance.RunOnceAsync(Now, default)),
            queue.CommitCompletionAsync(other, "0001"));
        while (await maintenance.RunOnceAsync(Now, default)) { }
        Check(Count(db, "SELECT max(rowid) FROM live_queue_recordings") > ceiling && Count(db, ceilingQuery) == ceiling &&
            (await queue.NextAsync(freshRun, 0, default))?.Id == "0001",
            "Ongoing sanitized ingestion must not extend the legacy scan or lose new pending recordings.");
        Check((await queue.NextAsync(run, 0, default))?.Id == "0421" &&
            Count(db, "SELECT count(*) FROM live_queue_recordings WHERE server=$server AND disposition=0", ("$server", Server)) == 200 &&
            Count(db, "SELECT count(*) FROM live_queue_servers WHERE server=$server AND resume_id='0421'", ("$server", Server)) == 1,
            "Maintenance must preserve active, interrupted and awaiting-audio records regardless of age.");
        Check(Count(db, "SELECT count(*) FROM live_queue_recordings WHERE disposition<>0 AND (payload IS NOT NULL OR audio_ready<>0)") == 0 &&
            Count(db, "SELECT count(*) FROM calls") == 0 && await queue.NextAsync(other, 0, default) == null,
            "Terminal payloads can be compacted while a recording on another server finishes.");
        for (var i = 0; i < 40; i++)
            Check(!await queue.ObserveAsync(run, "0001", old.AddDays(i), Snapshot(Server + "/clip.wav"), default, RecordingOrigin.Live),
                "Repeated recorder delivery must not resurrect or grow a terminal identity.");
        Check(Count(db, "SELECT count(*) FROM live_queue_recordings WHERE server=$server", ("$server", Server)) == 620,
            "Terminal and pending identities must all remain in the journal.");

        var floor = DurableLiveQueue.HistoricalFloor(true, 2, true, 1, Now);
        Check(await queue.NextAsync(hosted, floor, default) == null &&
            (await queue.NextAsync(hosted, 0, default))?.Id == "shared" &&
            (await queue.NextAsync(otherAccount, 0, default))?.Id == "other-account",
            "Maintenance must preserve subscription downgrade and account-specific authorization context.");
        await queue.StopAsync(run);
        Check(await queue.JumpToLiveAsync(run, 0, skipSelected: true, default) == null, "Stop must still fence transport.");
        var restarted = run with { Owner = run.Owner + 1 };
        var reopened = new DurableLiveQueue(paths);
        await reopened.OpenAsync(restarted, Now, default);
        Check((await reopened.JumpToLiveAsync(restarted, 0, skipSelected: true, default))?.Target.Id == "0620" &&
            await reopened.NextAsync(restarted, 0, default) == null,
            "Only an explicit Jump can skip the old pending backlog after maintenance.");
        Console.WriteLine("PASS Bounded retention compacts terminal rows without replay, preserving concurrent completion, unauthorized history, Stop and Jump");
    }

    private static DbCallRecord Cached(string id, string? url) =>
        new() { ServerKey = Server, BackendId = id, AudioFilename = url, ReceivedAtUtc = Now, TargetLabel = "Dispatch" };

    private static LiveCallSnapshot Snapshot(string? url) =>
        new(new CallCardMetadata { Talkgroup = "Dispatch" }, "Call", CallCardIdFallback.None, url);

    private static async Task<SqliteConnection> OpenAsync(Paths paths)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DbPath }.ToString());
        await db.OpenAsync();
        await DbBootstrapper.EnsureInitializedAsync(db);
        return db;
    }

    private static void Execute(SqliteConnection db, string sql, params (string, object)[] parameters)
    {
        using var command = BuildCommand(db, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection db, string sql, params (string, object)[] parameters)
    {
        using var command = BuildCommand(db, sql, parameters);
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteCommand BuildCommand(SqliteConnection db, string sql, (string, object)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition)
            throw new InvalidOperationException(failure);
    }

    private sealed record Paths(string DbPath) : IDatabasePathProvider;
}
