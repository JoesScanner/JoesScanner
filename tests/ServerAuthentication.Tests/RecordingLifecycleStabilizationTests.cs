using JoesScanner.Models;
using JoesScanner.Services;

internal static partial class MobileRecoveryTests
{
    internal static async Task ReconfirmedFieldsAcrossReconstruction()
    {
        const string server = "https://custom.example";
        var recorder = Guid.NewGuid();
        var run = new DurableLiveQueue.Run(server, 1, null, ServerId: recorder);
        using var database = new SavedCredentialTests.Fixture();
        var queue = new DurableLiveQueue(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        await queue.OpenAsync(run, Now, token);
        object RowFor(string group) => new { DT_RowId = "reconfirmed", StartTimeUTC = Now.ToUnixTimeSeconds(),
            AudioFilename = "same.wav", TargetID = group, CallDuration = 3, CallText = "Dispatch" };
        using var socket = new ScriptSocket();
        socket.Send(RowFor("A"));
        var lookupStarted = NewArrivalSignal();
        var releaseLookup = NewArrivalSignal();
        using var socketHttp = new HttpClient(new StubHandler(_ => Reply()));
        using var stream = IngestionStream(Settings(server), socketHttp, _ => socket);
        await using var reader = stream.GetCallStreamAsync([], token, includeOrderingUpdates: true).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Expected the recorder handshake.");
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "reconfirmed", "The recording did not arrive.");
        var indexed = reader.Current.LiveState!;
        await queue.ObserveAsync(run, indexed.Id, Now, indexed.Read(), token, RecordingOrigin.Live);
        var restored = (await queue.NextAsync(run, 0, token))!.ToCallItem(server, recorder);
        using var slowHttp = new HttpClient(new AsyncStubHandler(async (_, ct) =>
        {
            lookupStarted.TrySetResult();
            await releaseLookup.Task.WaitAsync(ct);
            return Reply(RowFor("B"));
        }));
        using var lookup = CreateStream(Settings(server), new ScriptSubscription(false, null), slowHttp);
        var earlier = lookup.RefreshCallMetadataAsync([restored], token);
        await lookupStarted.Task.WaitAsync(token);
        socket.Send(RowFor("A"));
        socket.Send(Row("marker", Now, "other.wav"));
        Check(await reader.MoveNextAsync() && reader.Current is { BackendId: "reconfirmed", IsOrderingUpdate: true },
            "A later equal-value full row must carry its field ordering to the durable consumer.");
        var confirmed = reader.Current.LiveState!;
        var saved = await queue.ObserveWithPositionAsync(run, indexed.Id, null, confirmed.Read(), token, RecordingOrigin.Unknown, true);
        Check(!saved.Changed && !saved.PlaybackChanged, "A reconfirmation must persist ordering without waking playback.");
        releaseLookup.SetResult();
        await earlier;
        // The delayed detached copy can reach the journal before its visible projection merges the reconfirmation.
        await queue.ObserveAsync(run, indexed.Id, Now, restored.LiveState!.Read(), token, existingOnly: true);
        restored.LiveState.MergeRecorderState(confirmed);
        restored.ApplyLiveUpdate();
        var selected = FavoriteTalkgroupIdentity.From(confirmed.Read().Metadata)!;
        var favorite = new LiveAutomaticAudioFilter(FavoriteEdges: new Dictionary<(Guid, FavoriteTalkgroupIdentity), long>
            { [(recorder, selected)] = 0 });
        var ready = await queue.ReadReadyWindowAsync([run], _ => 0, 1, token, automatic: favorite);
        Check(restored.CardMetadata?.TalkgroupId == "A" && ready.Single().Row.Snapshot?.Metadata.TalkgroupId == "A",
            "An older lookup changed the reconstructed card or Favorites selection.");
    }

    internal static async Task ReconstructedSignedKeyFailure()
    {
        const string server = "https://custom.example";
        const string id = "refused";
        const string first = server + "/media.wav?lease=synthetic-first";
        const string rotated = server + "/media.wav?lease=synthetic-replacement";
        using var database = new SavedCredentialTests.Fixture();
        var scope = new DurableLiveQueue.Run(server, 3, null);
        DurableLiveQueue Open() => new(database, new ProtectedCredentials(database.Vault).SignedReferenceKey());
        var queue = Open();
        await queue.OpenAsync(scope, Now, default);
        await queue.ObserveAsync(scope, id, Now, SignedSnapshot(first), default, RecordingOrigin.Live);
        var original = (await queue.ReadReadyWindowAsync([scope], _ => 0, 1, default)).Single().Row;
        await queue.DeferAudioAsync(scope, id, original.AudioRevision, default);
        async Task Refresh(string file, bool shouldBecomeReady)
        {
            var card = (await queue.NextAsync(scope, 0, default))!.ToCallItem(server);
            Check(card.AudioUrl.Length == 0, "Reconstruction unexpectedly persisted a signed reference.");
            using var http = new HttpClient(new StubHandler(_ => Reply(Row(id, Now, file))));
            using var stream = CreateStream(Settings(server), new ScriptSubscription(false, null), http);
            await stream.RefreshCallMetadataAsync([card], default);
            await queue.ObserveWithPositionAsync(scope, id, card.RecordingTimeUtc, card.LiveState!.Read(), default,
                RecordingOrigin.Unknown, existingOnly: true);
            var ready = await queue.ReadReadyWindowAsync([scope], _ => 0, 1, default);
            Check(shouldBecomeReady == (ready.Length == 1), "Signed retry readiness requires a comparable rotation.");
        }
        database.Vault.FailRead = true;
        queue = Open();
        await Refresh(first, false);
        await Refresh(first, false);
        await Refresh(rotated, false);
        database.Vault.FailRead = false;
        database.Vault.Values.Remove("joesscanner.v1.signed-audio-reference");
        queue = Open();
        await Refresh(first, false);
        await Refresh(first, false);
        await Refresh(rotated, true);
    }

    internal static async Task SameUrlAfterUnmatchedLookup()
    {
        const string server = "https://custom.example";
        const string url = server + "/same.wav";
        var state = new LiveCallState(server, "recording");
        state.Observe(SignedSnapshot(url), LiveCallState.NextVersion());
        var card = new CallItem { BackendId = state.Id, RecordingServer = server, LiveState = state, RecordingTimeUtc = Now };
        card.ApplyLiveUpdate();
        using var http = new HttpClient(new AsyncStubHandler(async (request, ct) =>
        {
            var (_, _, size) = await ReadLookupRequest(request, ct);
            if (size != 0)
            {
                state.Observe(SignedSnapshot(null) with { Availability = RecordingAudioAvailability.Missing }, LiveCallState.NextVersion());
                state.Observe(SignedSnapshot(url), LiveCallState.NextVersion());
            }
            return LookupReply(0, null);
        }));
        using var stream = CreateStream(Settings(server), new ScriptSubscription(false, null), http);
        var refresh = await stream.RefreshRecordingAudioAsync(card, server, Now, default);
        Check(refresh == url && state.Read().AudioUrl == url, "An unmatched page discarded the later same-URL recovery.");
    }

    internal static async Task UtcRecordingIdentity()
    {
        const string server = "https://custom.example";
        const string id = "shared";
        const string url = server + "/original.wav";
        var time = DateTimeOffset.Parse("2026-11-01T01:30:00-06:00");
        var later = DateTimeOffset.Parse("2026-11-01T01:30:00-07:00");
        var state = new LiveCallState(server, id);
        state.Observe(SignedSnapshot(url), LiveCallState.NextVersion());
        var undated = new CallItem { BackendId = id, RecordingServer = server, LiveState = state };
        foreach (var sparse in new object[] { new { DT_RowId = id, AudioFilename = (string?)null }, Row(id, later, null) })
        {
            using var sparseHttp = new HttpClient(new StubHandler(_ => LookupReply(1, null, sparse)));
            using var lookup = CreateStream(Settings(server), new ScriptSubscription(false, null), sparseHttp);
            Check(await lookup.RefreshRecordingAudioAsync(undated, server, null, default) == null && state.Read().AudioUrl == url,
                "An ID-only lookup cannot establish disappearance for an undated recording.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var socket = new ScriptSocket();
        object Stamped(DateTimeOffset stamp, string? audio) =>
            new { DT_RowId = id, StartTimeUTC = stamp.ToString("O"), AudioFilename = audio, CallText = "Dispatch" };
        socket.Send(Stamped(time, url));
        socket.Send(Stamped(later, null));
        socket.Send(Stamped(later, "/wrong-file.wav"));
        socket.Send(Stamped(later, null));
        socket.Send(Row("marker", Now, "other.wav"));
        socket.Send(Stamped(time.ToUniversalTime(), null));
        using var http = new HttpClient(new StubHandler(_ => Reply()));
        using var stream = IngestionStream(Settings(server), http, _ => socket);
        await using var reader = stream.GetCallStreamAsync([], token, includeOrderingUpdates: true).GetAsyncEnumerator(token);
        Check(await reader.MoveNextAsync() && reader.Current.Talkgroup == "HEARTBEAT", "Expected a connection.");
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == id, "Expected the original recording.");
        var indexed = reader.Current.LiveState!;
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == "marker" && indexed.Read().AudioUrl == url &&
            indexed.Read().RecordingUnixSeconds == time.ToUnixTimeSeconds(), "A reused ID at another instant mutated the recording.");
        Check(await reader.MoveNextAsync() && reader.Current.BackendId == id &&
            indexed.Read().EffectiveAvailability == RecordingAudioAvailability.Missing,
            "The equivalent UTC instant must prove disappearance in either half of a daylight-saving fold.");
        using var database = new SavedCredentialTests.Fixture();
        var queue = new DurableLiveQueue(database);
        var run = new DurableLiveQueue.Run(server, 5, null);
        await queue.OpenAsync(run, Now, token);
        await queue.ObserveAsync(run, id, time, SignedSnapshot(url), token, RecordingOrigin.Live);
        var unrelated = new LiveCallState(server, id);
        unrelated.Observe(SignedSnapshot(null) with { Availability = RecordingAudioAvailability.Missing,
            RecordingUnixSeconds = later.ToUnixTimeSeconds() }, LiveCallState.NextVersion());
        var conflict = await queue.ObserveWithPositionAsync(run, id, later, unrelated.Read(), token, RecordingOrigin.Live, false);
        Check(conflict.ConflictingRecordingTime && conflict.ArrivalSequence == null &&
            (await queue.NextAsync(run, 0, token))!.Snapshot?.AudioUrl == url,
            "A conflicting pending identity cannot be admitted to the UI or change the journal.");
        var index = new LiveCallIndex();
        index.MarkSeen(id);
        index.Track(unrelated);
        index.ConfirmAdmission(id, conflict.ArrivalSequence.HasValue);
        Check(index.Find(id) == null && index.NeedsAdmission(id), "A rejected stream identity must allow a later correct admission.");
        await queue.CommitCompletionAsync(run, id);
        conflict = await queue.ObserveWithPositionAsync(run, id, later, unrelated.Read(), token, RecordingOrigin.Unknown, true);
        var matching = await queue.ObserveWithPositionAsync(run, id, time, indexed.Read(), token, RecordingOrigin.Unknown, true);
        Check(conflict.ConflictingRecordingTime && conflict.ArrivalSequence == null &&
            !matching.ConflictingRecordingTime && matching.ArrivalSequence.HasValue && !matching.PlaybackChanged,
            "A terminal row must fence a reused ID while retaining the matching card for intentional replay.");
    }
}
