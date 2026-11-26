using System.Text.Json;
using JoesScanner.Models;
using JoesScanner.Services;
using Microsoft.Data.Sqlite;

if (args is ["--fresh-live-session"])
{
    Environment.ExitCode = await FreshLiveSessionContractTests.RunAsync();
    return;
}

if (args.Length == 3 && args[0] == "--completion-crash-probe")
{
    await DurablePlaybackConcurrencyTests.RunCrashProbeAsync(args[1], args[2]);
    return;
}

if (args.Length == 3 && args[0] == "--settings-crash-probe")
{
    await PersistenceSafetyTests.RunCrashProbeAsync(args[1], args[2]);
    return;
}

await AndroidAudioTests.RunAsync();
await IosAudioTests.RunAsync();
await MonitoringLifecycleTests.RunAsync();
await PersistenceSafetyTests.RunAsync();
await DurableLiveQueueTests.RunAsync();
await DurableAccountIsolationTests.RunAsync();
await DurablePlaybackConcurrencyTests.RunAsync();
await DurableLiveTransportTests.RunAsync();
await LiveInterruptionTests.RunAsync();
await RecordingDatabaseMaintenanceTests.RunAsync();
await LiveHistoryHandoffTests.RunAsync();
await LivePlaybackPinTests.RunAsync();
await IntelligentLivePlaybackTests.RunAsync();
await VerifiedPlaybackLagTests.RunAsync();
await LiveAudioSourceTests.RunAsync();
await JumpVisualNavigationTests.RunAsync();
await AudioCacheTests.RunAsync();
await AudioPreloadTests.RunAsync();
await HistoryPlaybackDispatcherTests.RunAsync();

var defaults = CallCardLayout.Default;
var complete = new CallCardMetadata
{
    Timestamp = new DateTime(2026, 1, 1, 14, 32, 5),
    System = "County P25",
    Talkgroup = "Fire Dispatch",
    Site = "North Tower",
    Radio = "Engine 12",
    Receiver = "Receiver 1",
    DurationSeconds = 8.4,
    Frequency = "851.125",
    TalkgroupTag = "Fire Ops",
    RadioTag = "Apparatus",
    SystemType = "P25",
    CallType = "Group",
    VoiceType = "Digital",
    Lcn = "4"
};

Check(defaults.Fields.Count == 14, "Default options must include every field.");
Check(Fields(defaults, complete, CallCardSection.Header).SequenceEqual(
    [CallCardField.Time, CallCardField.System, CallCardField.Talkgroup]), "Wrong default header order.");
Check(Fields(defaults, complete, CallCardSection.Secondary).SequenceEqual(
    [CallCardField.Site, CallCardField.Radio]), "Wrong default secondary order.");
Console.WriteLine("PASS Default header, secondary details, and field catalog");

var edited = defaults.Move(CallCardField.Talkgroup, -1)
    .MoveToSection(CallCardField.Site, CallCardSection.Header)
    .Move(CallCardField.Site, -1)
    .SetEnabled(CallCardField.System, false)
    .SetEnabled(CallCardField.Receiver, true);
Check(Fields(edited, complete, CallCardSection.Header).SequenceEqual(
    [CallCardField.Time, CallCardField.Talkgroup, CallCardField.Site]), "Move, reorder, or visibility failed.");
Check(Fields(edited, complete, CallCardSection.Secondary).SequenceEqual(
    [CallCardField.Radio, CallCardField.Receiver]), "Receiver should follow Radio.");
Check(edited.Move(CallCardField.Time, -1) == edited, "Up should stop at the section boundary.");
Check(edited.MoveToSection(CallCardField.Time, CallCardSection.Header) == edited, "Same-section moves should be no-ops.");
Check(edited.GetOption(CallCardField.System).Section == CallCardSection.Header, "Hiding should keep the placement.");
Check(defaults.GetOption(CallCardField.System).Enabled, "Editing must not mutate the defaults.");
var hidden = defaults.Fields.Aggregate(defaults, (layout, option) => layout.SetEnabled(option.Field, false));
Check(!hidden.GetDisplayFields(complete, CallCardSection.Header).Any() &&
      !hidden.GetDisplayFields(complete, CallCardSection.Secondary).Any(), "All fields must be independently hideable.");
Console.WriteLine("PASS Field order, section moves, visibility, boundaries, and all-hidden layout");

var incomplete = new CallCardMetadata { System = " ", Talkgroup = "Dispatch", Radio = "  Unit 7  " };
Check(defaults.GetDisplayFields(incomplete, CallCardSection.Header).Select(entry => entry.Text).SequenceEqual(["Dispatch"]),
    "Missing time and system should leave only the talkgroup.");
Check(defaults.GetDisplayFields(incomplete, CallCardSection.Secondary).Select(entry => entry.Text).SequenceEqual(["Radio: Unit 7"]),
    "Missing site should leave only the trimmed radio.");
Check(!defaults.GetDisplayFields(new CallCardMetadata(), CallCardSection.Header).Any(), "Empty metadata should omit the whole section.");
Console.WriteLine("PASS Missing fields are omitted before joining rendered values");

// The live and history services both use ReadCardFields and ToCardMetadata.
var live = ReadRow("""
    {"SystemLabel":"County P25","SystemID":101,"TargetLabel":"Fire Dispatch","TargetID":102,
     "SiteLabel":"North Tower","SiteID":103,"SourceLabel":"Engine 12","SourceID":104,"VoiceReceiver":"Receiver 1",
     "CallDuration":8.4,"Frequency":851.125,"TargetTag":"Fire Ops","SourceTag":"Apparatus","SystemType":"P25",
     "CallType":"Group","CallAudioType":"Digital","LCN":4}
    """).ToCardMetadata(complete.Timestamp);
Check(live == complete with { SystemId = "101", TalkgroupId = "102", SiteId = "103" },
    "Live metadata must retain raw scheduler IDs alongside the labels and optional card fields.");

var history = ReadRow("""
    {"SystemID":101,"TargetID":102,"SiteID":103,"SourceID":104,"VoiceType":"Digital","CallDuration":"NaN"}
    """).ToCardMetadata(null, omitUnknownSite: true);
Check(history.System == "101" && history.Talkgroup == "102" && history.Site == "103" && history.Radio == "104",
    "History must fall back to the correct IDs.");
Check(history.Receiver == null && history.VoiceType == "Digital" && history.DurationSeconds == null && history.Timestamp == null,
    "Missing or invalid fields must not invent a receiver, time or duration.");
Check(ReadRow("""{"SiteLabel":"Unknown site","SystemLabel":"County","VoiceReceiver":"Receiver only"}""")
    .ToCardMetadata(null, omitUnknownSite: true) is { Site: null, System: "County", Radio: null, Receiver: "Receiver only" },
    "History must keep its unknown-site behavior without mixing System, Radio, and Receiver.");
Console.WriteLine("PASS Live labels and optional fields, History ID fallbacks, and missing metadata");

var metadataState = new LiveCallState("https://local.example", "metadata");
var call = new CallItem { LiveState = metadataState };
var notifications = 0;
call.PropertyChanged += (_, args) =>
{
    if (args.PropertyName == nameof(CallItem.CardMetadata))
        notifications++;
};
metadataState.Observe(ReadRow("""{"SystemID":101,"SourceID":104}""").ToLiveSnapshot(null, "Dispatch"), LiveCallState.NextVersion());
call.ApplyLiveUpdate();
Check(call.CardMetadata is { System: "101", Radio: "104" }, "A card should show initial ID fallbacks.");
var fullRow = ReadRow("""
    {"SystemLabel":"County P25","SystemID":101,"TargetLabel":"Fire Dispatch","SiteLabel":"North Tower",
     "SourceLabel":"Engine 12","SourceID":104,"VoiceReceiver":"Receiver 1","Frequency":851.125,
     "TargetTag":"Fire Ops","SourceTag":"Apparatus","SystemType":"P25","CallType":"Group","VoiceType":"Digital","LCN":4}
    """).ToLiveSnapshot(complete.Timestamp, "Dispatch");
Check(metadataState.Observe(fullRow, LiveCallState.NextVersion(), out var changedText) && changedText == null,
    "A full row with unchanged transcription must still publish its additional metadata.");
call.ApplyLiveUpdate();
Check(call.CardMetadata == complete with { DurationSeconds = null, SystemId = "101" } && notifications == 2,
    "Live cards must pick up all available fields and replace ID fallbacks with labels.");
Check(!metadataState.Observe(fullRow, LiveCallState.NextVersion()) &&
      !metadataState.Observe(ReadRow("""{"SystemID":101,"SourceID":104,"Frequency":" ","CallDuration":0}""")
          .ToLiveSnapshot(null, null), LiveCallState.NextVersion()),
    "Duplicates, missing fields and ID-only fallbacks must not alter the bound metadata.");
call.ApplyLiveUpdate();
Check(notifications == 2, "Repeated observations must not request another card layout.");
Console.WriteLine("PASS Late full rows populate mapped metadata without repeating UI updates");

var transcriptCall = new CallItem();
const string missingTranscript = "No transcription from server";
Check(transcriptCall.Transcription == string.Empty && transcriptCall.TranscriptionDisplay == missingTranscript,
    "The card should display the missing message without inserting it into the transcript.");
Check(CallItem.IsMissingTranscription("no transcription from server") && CallItem.IsMissingTranscription(" \t "),
    "Empty text and the server placeholder must both remain eligible for a late transcript.");
Check(!CallItem.IsMissingTranscription("Dispatch said no transcription from server was available."),
    "A real transcript mentioning the phrase must still be displayed.");

transcriptCall.Transcription = "no transcription from server";
Check(transcriptCall.TranscriptionDisplay == missingTranscript, "Use the same message for a server placeholder.");
var transcriptChanges = 0;
transcriptCall.PropertyChanged += (_, args) =>
{
    if (args.PropertyName == nameof(CallItem.TranscriptionDisplay))
        transcriptChanges++;
};
transcriptCall.Transcription = "  Dispatch clear  ";
Check(transcriptCall.TranscriptionDisplay == "Dispatch clear" && transcriptChanges == 1,
    "A late short transcript should replace the placeholder in the same binding.");
var longTranscript = string.Join(' ', Enumerable.Range(1, 500).Select(i => $"update-{i}"));
transcriptCall.Transcription = longTranscript;
Check(transcriptCall.TranscriptionDisplay == longTranscript && transcriptChanges == 2,
    "The same binding must publish the entire updated transcript.");
transcriptCall.Transcription = " ";
Check(transcriptCall.TranscriptionDisplay == missingTranscript && transcriptChanges == 3,
    "The missing message should return if text is cleared.");
Console.WriteLine("PASS Missing, short, and long transcriptions share the same updating card binding");

var legacyDuration = new CallItem { CallDurationSeconds = 8.4 };
Check(defaults.ShowDuration && defaults.GetCornerDuration(complete, compact: false) == legacyDuration.DurationDisplay &&
      defaults.GetCornerDuration(complete, compact: true) == legacyDuration.DurationMmSsDisplay,
    "The corner should default to on and use the existing Live and History formats.");
var durationInHeader = defaults.SetEnabled(CallCardField.Duration, true)
    .MoveToSection(CallCardField.Duration, CallCardSection.Header)
    .Move(CallCardField.Duration, -1);
Check(!Fields(durationInHeader, complete, CallCardSection.Header).Contains(CallCardField.Duration) &&
      durationInHeader.GetOption(CallCardField.Duration).Enabled,
    "The corner should suppress regular duration without losing its saved placement.");
var durationInMetadata = durationInHeader.SetShowDuration(false);
Check(durationInMetadata.GetCornerDuration(complete, compact: false) == null &&
      Fields(durationInMetadata, complete, CallCardSection.Header).SequenceEqual(
          [CallCardField.Time, CallCardField.System, CallCardField.Duration, CallCardField.Talkgroup]),
    "Turning the corner off must restore the field in its chosen order.");
Check(durationInMetadata.Move(CallCardField.Duration, 1).SetEnabled(CallCardField.Receiver, true).ShowDuration == false,
    "Editing metadata must preserve the corner preference.");
foreach (var invalid in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity, double.MaxValue })
{
    var invalidCall = complete with { DurationSeconds = invalid };
    Check(defaults.GetCornerDuration(invalidCall, compact: false) == null &&
          defaults.GetCornerDuration(invalidCall, compact: true) == null &&
          !Fields(durationInMetadata, invalidCall, CallCardSection.Header).Contains(CallCardField.Duration),
        "An invalid duration should never take space in the card.");
}
Console.WriteLine("PASS Default corner, both duration formats, invalid values, and duplicate suppression");

var pendingState = new LiveCallState("https://local.example", "pending");
var pendingCard = new CallItem { LiveState = pendingState, BackendId = pendingState.Id };
var newerCard = new CallItem { BackendId = "newer" };
var olderCard = new CallItem { BackendId = "older", IsHistory = true };
var visible = new List<CallItem> { olderCard };
var pending = new List<CallItem> { newerCard, pendingCard };
var pendingGate = new object();
pendingState.Observe(new(new(), missingTranscript, CallCardIdFallback.None), LiveCallState.NextVersion());
Check(pendingState.Observe(new(new() { DurationSeconds = 8.4 }, "Dispatch clear", CallCardIdFallback.None),
          LiveCallState.NextVersion(), out var lateTranscript) && lateTranscript == "Dispatch clear",
    "A combined update must carry both the first transcription and duration.");
var target = pendingState.FindCard(visible, pending, pendingGate);
Check(ReferenceEquals(target, pendingCard) && target!.ApplyLiveUpdate() && pendingCard.Transcription == "Dispatch clear" &&
      pendingCard.CardMetadata?.DurationSeconds == 8.4 && pendingCard.CallDurationSeconds == 8.4,
    "The combined update must apply to the pending card in place.");
Check(pending.SequenceEqual([newerCard, pendingCard]) && visible.SequenceEqual([olderCard]),
    "Applying an update must leave queue membership and order unchanged.");

pending.RemoveAt(1);
visible.Insert(0, pendingCard);
pendingState.Observe(new(new() { DurationSeconds = 9.6 }, null, CallCardIdFallback.None), LiveCallState.NextVersion());
target = pendingState.FindCard(visible, pending, pendingGate);
Check(ReferenceEquals(target, pendingCard) && !target!.ApplyLiveUpdate() && pendingCard.CardMetadata?.DurationSeconds == 9.6 &&
      pendingCard.Transcription == "Dispatch clear" && visible.SequenceEqual([pendingCard, olderCard]) && pending.SequenceEqual([newerCard]),
    "After promotion, the same call must receive updates in the visible queue without duplicates.");
pendingCard.IsHistory = true;
Check(ReferenceEquals(pendingState.FindCard(visible, pending, pendingGate), pendingCard),
    "Replaying an older Live card must not remove it from update routing.");
Console.WriteLine("PASS Combined updates reach pending cards and follow their promotion without reordering");

var index = new LiveCallIndex();
var completeTranscript = new LiveCallState("https://local.example", "notice");
completeTranscript.Observe(new(new(), "Existing transcript", CallCardIdFallback.None, "https://local.example/call.mp3"), LiveCallState.NextVersion());
Check(index.MarkSeen(completeTranscript.Id), "The first full row should be new.");
index.Track(completeTranscript);
var now = DateTimeOffset.UtcNow.AddSeconds(3);
Check(index.BeginCatchup(now) == null, "Missing duration alone must not trigger polling without a notification.");
index.QueueNotification(completeTranscript, now);
index.QueueNotification(completeTranscript, now);
var scheduled = index.BeginCatchup(now);
Check(scheduled != null && scheduled.SequenceEqual([completeTranscript]),
    "An ID-only notification must schedule one lookup even when transcription is complete.");
index.QueueNotification(completeTranscript, now.AddSeconds(3));
Check(index.BeginCatchup(now.AddSeconds(3)) == null, "Repeated notifications must not duplicate an in-flight lookup.");
completeTranscript.Observe(new(new() { DurationSeconds = 8.4 }, null, CallCardIdFallback.None), LiveCallState.NextVersion());
completeTranscript.CompleteNotification();
index.QueueNotification(completeTranscript, now.AddSeconds(4));
Check(index.BeginCatchup(now.AddSeconds(4)) == null && completeTranscript.Read().Metadata.DurationSeconds == 8.4,
    "A completed lookup must supply duration and stop unnecessary requests.");
Console.WriteLine("PASS ID-only WebSocket notices coalesce and resolve missing duration with an existing transcript");

var queued = new LiveCallState("https://local.example", "retry");
queued.Observe(new(new(), "Existing transcript", CallCardIdFallback.None, "https://local.example/call.mp3"), LiveCallState.NextVersion());
index.Track(queued);
index.QueueNotification(queued, now);
Check(index.BeginCatchup(now)?.Single() == queued, "A missing duration should trigger a lookup for this notice.");
queued.CompleteNotification();
index.QueueNotification(queued, now.AddSeconds(1));
Check(index.BeginCatchup(now.AddSeconds(1)) == null, "Repeated notices should respect the lookup cooldown.");
index.QueueNotification(queued, now.AddSeconds(2));
queued.Observe(new(new() { DurationSeconds = 7 }, null, CallCardIdFallback.None), LiveCallState.NextVersion());
Check(index.BeginCatchup(now.AddSeconds(2)) == null, "A full row that resolves duration must cancel a queued lookup.");
Console.WriteLine("PASS Notification retries are bounded and a full row can make a queued lookup unnecessary");

var ordered = new LiveCallState("https://local.example", "ordered");
ordered.Observe(new(new() { DurationSeconds = 4, System = "Initial system" }, null, CallCardIdFallback.None),
    LiveCallState.NextVersion());
var oldVersion = LiveCallState.NextVersion();
var newVersion = LiveCallState.NextVersion();
var oldResponse = new TaskCompletionSource<LiveCallSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
var newResponse = new TaskCompletionSource<LiveCallSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
var oldRequest = ApplyResponseAsync(oldResponse.Task, oldVersion);
var newRequest = ApplyResponseAsync(newResponse.Task, newVersion);
async Task ApplyResponseAsync(Task<LiveCallSnapshot> response, long version) => ordered.Observe(await response, version);

newResponse.SetResult(new(new() { DurationSeconds = 9, System = "Updated system" }, "Newest transcript", CallCardIdFallback.None));
await newRequest;
ordered.Observe(new(new() { DurationSeconds = 11, Site = "Live site" }, "Live transcript", CallCardIdFallback.None),
    LiveCallState.NextVersion());
oldResponse.SetResult(new(new() { DurationSeconds = 6, System = "Stale", Site = "Stale", RadioTag = "New tag" },
    "Stale transcript", CallCardIdFallback.None));
await oldRequest;

var orderedResult = ordered.Read();
Check(orderedResult.Metadata is { DurationSeconds: 11, System: "Updated system", Site: "Live site", RadioTag: "New tag" } &&
      orderedResult.Transcription == "Live transcript",
    "An older in-flight response may fill an untouched gap but must preserve newer HTTP and stream observations.");
var confirmedOld = LiveCallState.NextVersion();
Check(!ordered.Observe(new(new() { DurationSeconds = 11 }, null, CallCardIdFallback.None), LiveCallState.NextVersion()) &&
      !ordered.Observe(new(new() { DurationSeconds = 10 }, null, CallCardIdFallback.None), confirmedOld),
    "An unchanged newer full row must still protect its fields from older responses without refreshing the UI.");
Console.WriteLine("PASS Out-of-order catch-up responses preserve newer metadata and transcription per field");

var collidingState = new LiveCallState("https://other.example", pendingState.Id);
var otherServer = new CallItem { LiveState = collidingState, BackendId = pendingState.Id };
var historyCard = new CallItem { IsHistory = true, BackendId = pendingState.Id, CardMetadata = history };
var reconnectIndex = new LiveCallIndex();
reconnectIndex.Seed([historyCard, otherServer, pendingCard], pendingState.ServerUrl);
Check(ReferenceEquals(reconnectIndex.Find(pendingCard.BackendId), pendingState) && !reconnectIndex.MarkSeen(pendingCard.BackendId),
    "Reconnecting to the same server should retain the existing Live card identity.");
Check(pendingState.FindCard([otherServer, historyCard], [], pendingGate) == null && historyCard.CardMetadata == history,
    "A historical card or another server's card must never receive an update solely because its IDs match.");
const string untrackedId = "older response row";
Check(reconnectIndex.Find(untrackedId) == null && reconnectIndex.MarkSeen(untrackedId),
    "Looking up an untracked historical row for catch-up must not suppress a future new Live row.");
Console.WriteLine("PASS Reconnect deduplication, historical row isolation, and server identity");

var older = CallCardLayout.Deserialize("1;Receiver,Header,true;Removed,Header,true;Receiver,Secondary,false;Time,?,true");
Check(older.GetOption(CallCardField.Receiver) == new CallCardFieldOption(CallCardField.Receiver, CallCardSection.Header, true) &&
      older.GetOption(CallCardField.Time) == defaults.GetOption(CallCardField.Time) && older.Fields.Count == 14 && older.ShowDuration,
    "Old entries must keep valid choices and fill missing fields with defaults.");
Check(CallCardLayout.Deserialize("2;Receiver,Header,true") == defaults && CallCardLayout.Deserialize("broken") == defaults,
    "Unknown versions and malformed preferences must fall back safely.");
Console.WriteLine("PASS Old, malformed, duplicate and unknown preference entries");

var database = Directory.CreateTempSubdirectory("JoesScanner.CallCardOptionsTests-");
AppPaths.DatabaseDirectory = database.FullName;
try
{
    AppStateStore.SetString("unrelated_preference", "unchanged");
    Check(CallCardLayoutStore.Read().Fields.SequenceEqual(defaults.Fields), "A missing SQLite row must use defaults.");
    CallCardLayoutStore.Write(edited);
    Check(CallCardLayoutStore.Read().Fields.SequenceEqual(edited.Fields), "SQLite must retain order, placement, and visibility.");
    CallCardLayoutStore.Write(hidden);
    Check(CallCardLayoutStore.Read().Fields.SequenceEqual(hidden.Fields), "A deliberately empty layout must survive restart.");
    CallCardLayoutStore.Write(defaults);
    Check(CallCardLayoutStore.Read().Fields.SequenceEqual(defaults.Fields), "Reset must persist the defaults.");
    Check(AppStateStore.GetString("unrelated_preference") == "unchanged", "Saving a layout must preserve unrelated preferences.");
    AppStateStore.SetString(CallCardLayoutStore.Key, "broken data");
    Check(CallCardLayoutStore.Read().Fields.SequenceEqual(defaults.Fields), "Corrupt SQLite values must use defaults.");
    Console.WriteLine("PASS Layout persistence, recovery, and reset in SQLite");

    CallCardLayoutStore.Write(edited);
    var options = new CallCardOptionsService();
    await options.Ready;
    Check(options.Layout.Fields.SequenceEqual(edited.Fields), "Startup must load the saved layout.");
    var updates = 0;
    options.PropertyChanged += (_, args) =>
    {
        if (args.PropertyName == nameof(CallCardOptionsService.Layout))
            updates++;
    };
    await options.ApplyAsync(defaults);
    Check(updates == 1 && options.Layout.Fields.SequenceEqual(defaults.Fields) &&
          CallCardLayoutStore.Read().Fields.SequenceEqual(defaults.Fields),
        "Apply must publish the saved layout to existing bindings.");
    Console.WriteLine("PASS Shared layout loads from SQLite and notifies existing bindings after Apply");

    await options.ApplyAsync(defaults.SetShowDuration(false));
    Check(updates == 2 && !options.Layout.ShowDuration && !CallCardLayoutStore.Read().ShowDuration,
        "Changing only the duration toggle must persist and notify already-bound cards.");
    await options.ApplyAsync(defaults);
    Check(updates == 3 && CallCardLayoutStore.Read().ShowDuration, "Reset should restore the enabled duration corner.");
    Console.WriteLine("PASS Duration-only Apply and Reset persist and notify");
}
finally
{
    SqliteConnection.ClearAllPools();
    foreach (var name in new[] { "joesscanner.db", "joesscanner.db-wal", "joesscanner.db-shm" })
        File.Delete(Path.Combine(database.FullName, name));
    database.Delete();
}

foreach (var (ios, legacy, expected) in new (bool, string?, MobileAudioMode)[]
{
    (false, null, MobileAudioMode.MixAndDuck),
    (true, null, MobileAudioMode.MixAndDuck),
    (false, "true", MobileAudioMode.MixAndDuck),
    (true, "true", MobileAudioMode.MixNormally),
    (false, "false", MobileAudioMode.Exclusive),
    (true, "false", MobileAudioMode.Exclusive),
    (true, "invalid", MobileAudioMode.MixAndDuck)
})
{
    var mobileDb = Directory.CreateTempSubdirectory("JoesScanner.MobileAudioTests-");
    AppPaths.DatabaseDirectory = mobileDb.FullName;
    try
    {
        if (legacy != null)
            AppStateStore.SetString(MobileAudioPreferencesStore.LegacyMixKey, legacy);
        var initial = MobileAudioPreferencesStore.Read(ios);
        Check(initial == new MobileAudioPreferences(expected, AudioDisconnectionBehavior.PausePlayback),
            $"Wrong mobile audio defaults or migration for iOS={ios}, legacy={legacy}.");
        Check(AppStateStore.GetString(MobileAudioPreferencesStore.LegacyMixKey) == (legacy ?? string.Empty),
            "Migration must preserve the legacy value used by current playback.");
        var oppositeLegacy = expected == MobileAudioMode.Exclusive ? "true" : "false";
        AppStateStore.SetString(MobileAudioPreferencesStore.LegacyMixKey, oppositeLegacy);
        Check(MobileAudioPreferencesStore.Read(ios) == initial, "Migration must run only once.");

        var routeChange = initial with { OnDisconnect = AudioDisconnectionBehavior.ContinueThroughPhoneSpeaker };
        MobileAudioPreferencesStore.Write(routeChange);
        Check(MobileAudioPreferencesStore.Read(ios) == routeChange, "Changing route behavior must preserve the mode.");
        foreach (var mode in Enum.GetValues<MobileAudioMode>())
        {
            var changedMode = routeChange with { Mode = mode };
            MobileAudioPreferencesStore.Write(changedMode);
            Check(MobileAudioPreferencesStore.Read(ios) == changedMode, "Every mode must persist independently of the route choice.");
        }
        Check(AppStateStore.GetString(MobileAudioPreferencesStore.LegacyMixKey) == oppositeLegacy,
            "Saving the new preferences must preserve the legacy playback setting.");
        var custom = routeChange with { InterruptionRecovery = new(InterruptionRecoveryOption.Custom, 19) };
        MobileAudioPreferencesStore.Write(custom);
        Check(MobileAudioPreferencesStore.Read(ios) == custom, "A custom threshold must survive rereading SQLite.");
        AppStateStore.SetString(MobileAudioPreferencesStore.CustomRecoveryKey, "1441");
        Check(MobileAudioPreferencesStore.Read(ios).InterruptionRecovery == new InterruptionRecoveryPreference(InterruptionRecoveryOption.Custom, 5),
            "An out-of-range stored threshold must fall back to five minutes.");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        foreach (var name in new[] { "joesscanner.db", "joesscanner.db-wal", "joesscanner.db-shm" })
            File.Delete(Path.Combine(mobileDb.FullName, name));
        mobileDb.Delete();
    }
}
Console.WriteLine("PASS Mobile audio defaults, one-time platform migration, and independent SQLite setting changes");

static TrunkingRecorderCallFields ReadRow(string json)
{
    using var document = JsonDocument.Parse(json);
    var fields = new TrunkingRecorderCallFields();
    fields.ReadCardFields(document.RootElement, (row, name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null);
    return fields;
}

static IEnumerable<CallCardField> Fields(CallCardLayout layout, CallCardMetadata metadata, CallCardSection section) =>
    layout.GetDisplayFields(metadata, section).Select(field => field.Field);

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
