using JoesScanner.Models;
using JoesScanner.Services;

internal static partial class MobileRecoveryTests
{
    internal static async Task SameUrlAfterLookup()
    {
        const string id = "returned";
        const string server = "https://recorder.example";
        const string audio = server + "/clip.wav";
        var state = new LiveCallState(server, id);
        state.Observe(SignedSnapshot(audio), LiveCallState.NextVersion());
        var card = new CallItem { BackendId = id, RecordingServer = server, LiveState = state };
        card.ApplyLiveUpdate();
        var before = state.Read();
        using var http = new HttpClient(new AsyncStubHandler(async (request, token) =>
        {
            var (_, _, length) = await ReadLookupRequest(request, token);
            return length == 0 ? LookupReply(0, null) : LookupReply(1, null, Row(id, Now, null));
        }));
        using var stream = CreateStream(Settings(server), new ScriptSubscription(false, null), http);
        var result = await stream.RefreshRecordingAudioAsync(card, server, Now, default);
        Check(result == null && state.Read().EffectiveAvailability == RecordingAudioAvailability.Missing,
            "Expected the exact lookup to confirm loss.");
        state.Observe(SignedSnapshot(audio), LiveCallState.NextVersion());
        Check(card.ApplyRefreshedAudioUrl(result, before, default) == audio && !card.HasDebugInfo,
            "Losing and recovering the same URL must preserve the download.");
    }

    internal static async Task FullRowAudioChanges()
    {
        const string id = "full-row";
        const string server = "https://recorder.example";
        const string audio = server + "/clip.wav";
        var state = new LiveCallState(server, id);
        state.Observe(SignedSnapshot(audio) with { Metadata = new() { Timestamp = Now.LocalDateTime } }, LiveCallState.NextVersion());
        var card = new CallItem { BackendId = id, LiveState = state, RecordingServer = server, RecordingTimeUtc = Now };
        object row = Row(id, Now, null);
        using var http = new HttpClient(new StubHandler(_ => Reply(row)));
        using var stream = CreateStream(Settings(server), new ScriptSubscription(false, null), http);
        async Task Refresh()
        {
            await stream.RefreshCallMetadataAsync([card], default);
            card.ApplyLiveUpdate();
        }
        await Refresh();
        Check(card.AudioUrl.Length == 0 && card.HasDebugInfo, "An ordinary full recorder row must clear a disappeared file.");
        row = Row(id, Now, audio);
        await Refresh();
        Check(card.AudioUrl == audio && !card.HasDebugInfo, "A later full row must restore availability.");
        foreach (object partial in new object[] {
            new { DT_RowId = id, StartTimeUTC = Now.ToUnixTimeSeconds(), CallText = "metadata" },
            new { DT_RowId = id, StartTimeUTC = Now.ToUnixTimeSeconds(), AudioFilename = 3 },
            new { DT_RowId = id, AudioFilename = (string?)null },
            new { DT_RowId = id, StartTimeUTC = Now.ToUnixTimeSeconds(), Update = true, AudioFilename = (string?)null }
        })
        {
            row = partial;
            await Refresh();
            Check(card.AudioUrl == audio && !card.HasDebugInfo, "A partial or malformed row cannot prove disappearance.");
        }
    }

    internal static Task AddressAlertOwnership()
    {
        var recorder = Guid.NewGuid();
        CallItem Card(string account, string endpoint = RecorderA, int seconds = 0) => new()
        {
            BackendId = "shared", ServerId = recorder, RecordingServer = endpoint, RecordingTimeUtc = Now.AddSeconds(seconds),
            RecordingAccess = new(RecordingOrigin.Live, false, account)
        };
        var old = Card("A");
        var replacement = Card("B");
        var own = Card("A");
        Check(RecordingIdentity.Find([replacement], old) == null &&
            RecordingIdentity.Find([Card("A", RecorderA + "/other")], old) == null &&
            RecordingIdentity.Find([Card("A", seconds: 1)], old) == null &&
            RecordingIdentity.Find([replacement, own], old) == own &&
            RecordingIdentity.Find([old], old) == old, "Creation, retention, dismissal and playback must select only the owning recording.");
        return Task.CompletedTask;
    }
}
