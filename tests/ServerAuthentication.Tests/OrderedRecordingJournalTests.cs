using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

internal static partial class MobileRecoveryTests
{
    internal static async Task OrderedJournalFields()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        const string server = "https://custom.example";
        const string id = "corrected";
        var recorder = Guid.NewGuid();
        var run = new DurableLiveQueue.Run(server, 1, null, ServerId: recorder);
        var queue = new DurableLiveQueue(fixture);
        await queue.OpenAsync(run, Now, default);
        var source = new LiveCallState(server, id);
        var original = new LiveCallSnapshot(new() { DurationSeconds = 2, TalkgroupId = "original" },
            "original", CallCardIdFallback.None, server + "/a.wav");
        source.Observe(original, LiveCallState.NextVersion());
        var early = source.Read();
        await queue.ObserveAsync(run, id, Now, early, default, RecordingOrigin.Live);
        var detached = new LiveCallState(server, id);
        detached.RestoreSnapshot(early);
        // The delayed snapshot knows one field the correction will omit.
        detached.Observe(new(new() { Site = "East" }, null, CallCardIdFallback.None), LiveCallState.NextVersion());
        source.Observe(original with { Metadata = new() { DurationSeconds = 7, TalkgroupId = "corrected" },
            Transcription = "corrected", AudioUrl = server + "/b.wav" }, LiveCallState.NextVersion());
        await queue.ObserveAsync(run, id, Now, source.Read(), default);
        var immediate = (await queue.NextAsync(run, 0, default))!.Snapshot!;
        Check(immediate.Metadata.DurationSeconds == 7 && immediate.Metadata.TalkgroupId == "corrected" &&
            immediate.AudioUrl == server + "/b.wav", "Journal did not retain the corrected duration, talkgroup ID and audio together.");
        await queue.ObserveAsync(run, id, Now, detached.Read(), default);
        var reconstructed = (await queue.NextAsync(run, 0, default))!.ToCallItem(server, recorder);
        Check(reconstructed.CardMetadata is { DurationSeconds: 7, TalkgroupId: "corrected", Site: "East" } &&
            reconstructed.Transcription == "corrected", "Delayed journal write must fill only independently newer fields.");
        source.MergeRecorderState(detached);
        Check(source.Read().Metadata == reconstructed.CardMetadata! with { Timestamp = null } &&
            source.Read().Transcription == "corrected", "Canonical and durable metadata must use the same field ordering.");

        // Change the process epoch on disk, as it would appear at the next launch.
        using (var db = new SqliteConnection($"Data Source={fixture.DbPath}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE live_queue_recordings SET payload=json_set(payload,'$.FieldOrder.Session',$old,'$.AudioOrder.Session',$old)";
            cmd.Parameters.AddWithValue("$old", Guid.NewGuid().ToString());
            cmd.ExecuteNonQuery();
        }
        queue = new DurableLiveQueue(fixture);
        var row = (await queue.NextAsync(run, 0, default))!;
        var favorite = FavoriteTalkgroupIdentity.From(row.Snapshot!.Metadata)!;
        var filter = new LiveAutomaticAudioFilter(FavoriteEdges: new Dictionary<(Guid, FavoriteTalkgroupIdentity), long> { [(recorder, favorite)] = 0 });
        Check(row.Snapshot.Metadata is { DurationSeconds: 7, TalkgroupId: "corrected" } &&
            (await queue.ReadReadyWindowAsync([run], _ => 0, 1, default, automatic: filter)).Single().Row.Id == id,
            "Corrected talkgroup must drive Favorites selection after durable reconstruction.");
        var next = new LiveCallState(server, id);
        next.Observe(new(new() { TalkgroupId = "new-session" }, null, CallCardIdFallback.None), 1);
        await queue.ObserveAsync(run, id, Now, next.Read(), default);
        Check((await queue.ReadReadyWindowAsync([run], _ => 0, 1, default, automatic: filter)).Length == 0 &&
            (await queue.NextAsync(run, 0, default))!.Snapshot!.Metadata.DurationSeconds == 7,
            "An earlier process cannot fence a correction from the next launch or erase omitted fields.");
    }

    internal static async Task EvictedSignedRotation()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        const string server = "https://custom.example";
        var run = new DurableLiveQueue.Run(server, 1, null);
        DurableLiveQueue OpenJournal() => new(fixture, new ProtectedCredentials(fixture.Vault).SignedReferenceKey());
        var queue = OpenJournal();
        await queue.OpenAsync(run, Now, default);
        string Signed() => server + "/media.wav?lease=" + Guid.NewGuid().ToString("N");
        var first = Signed();
        var second = Signed();
        const string id = "detached";
        LiveCallSnapshot Detached(string url)
        {
            var state = new LiveCallState(server, id);
            state.Observe(SignedSnapshot(url), LiveCallState.NextVersion());
            return state.Read();
        }
        var firstObservation = Detached(first);
        await queue.ObserveAsync(run, id, Now, firstObservation, default, RecordingOrigin.Live);
        var original = (await queue.ReadReadyWindowAsync([run], _ => 0, 1, default)).Single().Row;
        await queue.DeferAudioAsync(run, id, original.AudioRevision, default);
        queue = OpenJournal();
        await queue.ObserveAsync(run, id, Now, Detached(first), default, existingOnly: true);
        Check((await queue.ReadReadyWindowAsync([run], _ => 0, 1, default)).Length == 0,
            "A detached duplicate of a refused signed reference must remain deferred.");
        // A vault outage or an intervening key replacement cannot prove that a
        // recorder rotated its reference. A replacement key establishes a new baseline.
        fixture.Vault.FailRead = true;
        queue = OpenJournal();
        await queue.ObserveAsync(run, id, Now, Detached(first), default, existingOnly: true);
        fixture.Vault.FailRead = false;
        fixture.Vault.Values.Remove("joesscanner.v1.signed-audio-reference");
        queue = OpenJournal();
        await queue.ObserveAsync(run, id, Now, Detached(first), default, existingOnly: true);
        Check((await queue.ReadReadyWindowAsync([run], _ => 0, 1, default)).Length == 0,
            "Vault failure and key replacement must not rearm duplicate audio.");
        await queue.ObserveAsync(run, id, Now, Detached(second), default, existingOnly: true);
        var ready = await queue.ReadReadyWindowAsync([run], _ => 0, 1, default);
        Check(ready.Length == 1 && ready[0].Row.ArrivalSequence == original.ArrivalSequence &&
            ready[0].Row.AudioRevision > original.AudioRevision, "An evicted recording with a rotated signed reference must become ready.");
        await queue.DeferAudioAsync(run, id, ready[0].Row.AudioRevision, default);
        await queue.ObserveAsync(run, id, Now, firstObservation, default, existingOnly: true);
        await queue.ObserveAsync(run, id, Now, Detached(second), default, existingOnly: true);
        Check((await queue.ReadReadyWindowAsync([run], _ => 0, 1, default)).Length == 0,
            "Delayed and duplicate signed observations cannot repeatedly rearm a failed attempt.");
        using (var db = new SqliteConnection($"Data Source={fixture.DbPath}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM live_queue_recordings WHERE recording_id='detached'";
            var payload = (string)cmd.ExecuteScalar()!;
            Check(!payload.Contains("lease") && !payload.Contains(first.Split('=').Last()) &&
                !payload.Contains(second.Split('=').Last()), "Signed query material reached durable storage.");
        }
        await queue.ObserveAsync(run, id, Now, Detached(Signed()), default);
        await queue.CommitCompletionAsync(run, id);
        await queue.ObserveAsync(run, id, Now, Detached(Signed()), default);
        await queue.ObserveAsync(run, "skipped", Now, Detached(first), default, RecordingOrigin.Live);
        await queue.MarkAsync(run, "skipped", PlaybackOutcome.Skipped, default);
        await queue.ObserveAsync(run, "skipped", Now, Detached(second), default);
        Check((await queue.ReadReadyWindowAsync([run], _ => 0, 4, default)).Length == 0,
            "Completed and intentionally skipped rows must remain terminal after signed rotation.");
    }
}

