using System.Text.Json;
using JoesScanner.Models;
using JoesScanner.Services;

internal static class MonitoringDeviceTests
{
    private static readonly Guid First = Guid.Parse("df6fae5e-21cc-46af-957e-6ae9d856f546");
    private static readonly Guid Second = Guid.Parse("ff9968af-6ecd-4717-abd5-eb0c3bc139ff");
    private static readonly CallCardMetadata Metadata = new()
    {
        TalkgroupId = "101", SystemId = "75", SiteId = "2", Talkgroup = "Fixture", DurationSeconds = 2
    };
    private static readonly MonitoringDeviceOptions Enabled = new() { TransmissionNotifications = true };

    internal static Task ScopesAndFavorites()
    {
        var time = new Clock();
        var policy = new MonitoringNotificationPolicy(time);
        var recording = Recording(time, First);
        var defaults = MonitoringDeviceOptions.Decode("{}");
        Check(!defaults.TransmissionNotifications && !defaults.ConnectionNotifications && !defaults.ScreenAwake &&
            defaults.GroupByTalkgroup && defaults.ChargingOnly && !defaults.Audible,
            "Alerts and overnight display are opt-in, with grouped alerts, silent delivery and charging-only defaults.");
        Check(policy.Transmission(recording, defaults, true, false, true) == null &&
            policy.Transmission(recording, Enabled, false, false, true) == null &&
            policy.Transmission(recording, Enabled, true, true, true) == null,
            "Stopped monitoring and actively viewed Live must stay quiet.");
        Check(policy.Transmission(recording with { Talkgroup = null }, Enabled, true, false, true) != null,
            "All Calls must accept a received transmission with no identified talkgroup.");

        var selected = Enabled with { Scope = TransmissionScope.SelectedTalkgroups };
        selected = selected.ToggleTalkgroup(new(recording.Talkgroup!.Value, "Fixture recorder and talkgroup"));
        selected = MonitoringDeviceOptions.Decode(JsonSerializer.Serialize(selected));
        Check(policy.Transmission(recording, selected, true, false, true) != null &&
            policy.Transmission(Recording(time, Second), selected, true, false, true) == null &&
            policy.Transmission(Recording(time, First, Metadata with { SystemId = "90" }), selected, true, false, true) == null,
            "A saved talkgroup applies to its permanent recorder and matching radio system.");

        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        settings.LocalOnlyMode = true;
        var favorites = new FavoritesService(settings, new FavoritesRepository(fixture));
        favorites.SetProfiles([new(First, "https://fixture.test/recorder", "First", null, true)]);
        var card = new CallItem { ServerId = First, CardMetadata = Metadata };
        var onlyFavorites = Enabled with { Scope = TransmissionScope.Favorites };
        MonitoringAlert? MatchFavorite() => policy.Transmission(recording with { Favorite = favorites.Contains(card) },
            onlyFavorites, true, false, true);
        Check(MatchFavorite() == null, "Favorites mode initially excludes the unstarred talkgroup.");
        favorites.SetCall(favorites.OwnerKey, card, true);
        Check(MatchFavorite() != null, "Starring a Favorite immediately admits subsequent transmissions.");
        favorites.SetCall(favorites.OwnerKey, card, false);
        Check(MatchFavorite() == null, "Removing the Favorite stops subsequent alerts.");
        return Task.CompletedTask;
    }

    internal static Task FixedWindowsAndIsolation()
    {
        var time = new Clock();
        var policy = new MonitoringNotificationPolicy(time);
        var options = Enabled with { Audible = true };
        MonitoringAlert Send(Guid server, CallCardMetadata? metadata = null) =>
            policy.Transmission(Recording(time, server, metadata), options, true, false, true)!.Value;

        var first = Send(First);
        var b = Send(Second);
        var other = Send(First, Metadata with { TalkgroupId = "102" });
        Check(first.Id != b.Id && first.Id != other.Id && first.Audible && !first.Update,
            "Different recorders and talkgroups get separate alerts.");
        time.Advance(59);
        var update = Send(First, Metadata with { SiteId = "100" });
        Check(update.Id == first.Id && update.Count == 2 && update.Update,
            "The same canonical talkgroup updates the original alert across sites inside the fixed window.");
        time.Advance(1);
        var replacement = Send(First);
        Check(replacement.Id != first.Id && replacement.Count == 1 && !replacement.Update,
            "At 60 seconds a second alert starts a new window even after a recent update.");
        options = options with { GroupByTalkgroup = false };
        Check(Send(First).Id != Send(First).Id, "Ungrouped calls are delivered independently.");
        return Task.CompletedTask;
    }

    internal static async Task DurableAdmissionAndRecovery()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var time = new Clock();
        var policy = new MonitoringNotificationPolicy(time);
        var durable = new DurableLiveQueue(fixture);
        var a = new DurableLiveQueue.Run("https://first.test", 1, null, ServerId: First);
        var b = new DurableLiveQueue.Run("https://second.test", 2, null, ServerId: Second);
        var snapshot = new LiveCallSnapshot(Metadata, "synthetic private transcript", CallCardIdFallback.None,
            "https://first.test/recording.wav");
        await durable.StartListeningAsync(a, time.GetUtcNow().AddMinutes(-1), default);
        await durable.StartListeningAsync(b, time.GetUtcNow().AddMinutes(-1), default);

        var first = await durable.ObserveWithPositionAsync(a, "shared-id", time.GetUtcNow(), snapshot, default, RecordingOrigin.Live, existingOnly: false);
        var duplicate = await durable.ObserveWithPositionAsync(a, "shared-id", time.GetUtcNow(),
            snapshot with { Transcription = "late update" }, default, RecordingOrigin.Live, existingOnly: true);
        var otherRecorder = await durable.ObserveWithPositionAsync(b, "shared-id", time.GetUtcNow(), snapshot, default, RecordingOrigin.Live, existingOnly: false);
        var baseline = await durable.ObserveWithPositionAsync(a, "old-baseline", time.GetUtcNow().AddSeconds(-20),
            snapshot, default, RecordingOrigin.History, existingOnly: false, historyBaseline: true);

        MonitoringAlert? Notice(Guid server, DurableLiveQueue.Observation observation, RecordingOrigin origin = RecordingOrigin.Live) =>
            policy.Transmission(Recording(time, server) with
            {
                FirstAdmission = observation.FirstAdmission,
                IsCurrentTransmission = !observation.IsRecoveryBacklog && origin == RecordingOrigin.Live
            }, Enabled, true, false, true);

        var alertA = Notice(First, first);
        var alertB = Notice(Second, otherRecorder);
        Check(alertA.HasValue && alertB.HasValue && alertA.Value.Id != alertB.Value.Id && Notice(First, duplicate) == null &&
            Notice(First, baseline, RecordingOrigin.History) == null,
            "Only the first durable Live admission alerts, and recovery and late metadata cannot send duplicates.");
        var incoming = Recording(time, First);
        Check(policy.Transmission(incoming with { RecordedAt = time.GetUtcNow().AddMinutes(-3) }, Enabled, true, false, true) == null &&
            policy.Transmission(incoming with { IsCurrentTransmission = false }, Enabled, true, false, true) == null &&
            policy.Transmission(incoming with { RecordedAt = null }, Enabled, true, false, true) == null,
            "Stale independent replays, History recovery and unknown timestamps do not alert.");
        Check(alertA is { } safe && !safe.Body.Contains("private transcript") && !safe.Body.Contains("https:"),
            "Only generic privacy-preserving text reaches the native notification.");

        Check((await durable.NextAsync(a, 0, default))?.Id == "shared-id" &&
            (await durable.NextAsync(b, 0, default))?.Id == "shared-id", "Alerts must not alter durable selection on either recorder.");
        await durable.CommitCompletionAsync(a, "shared-id");
        Check(await durable.NextAsync(a, 0, default) == null && (await durable.NextAsync(b, 0, default))?.Id == "shared-id",
            "Completion ownership remains independent of sends and their success.");

        var muted = await durable.ObserveWithPositionAsync(a, "muted", time.GetUtcNow(), snapshot, default,
            RecordingOrigin.Live, existingOnly: false, skipAutomatic: true);
        Check(Notice(First, muted) != null && await durable.NextAsync(a, 0, default) == null,
            "Notification eligibility is independent of the playback filter and does not undo it.");
    }

    internal static Task RedFailureEpisodes()
    {
        var policy = new MonitoringNotificationPolicy(new Clock());
        var options = new MonitoringDeviceOptions { ConnectionNotifications = true, Audible = true };
        Check(policy.FailureStatus(First, 1, ServerConnectionStatus.Reconnecting, options, out _) == null &&
            policy.FailureStatus(First, 1, ServerConnectionStatus.Unavailable, new(), out _) == null,
            "Yellow is visual only and connection alerts default off.");
        var first = policy.FailureStatus(First, 1, ServerConnectionStatus.Unavailable, options, out _)!.Value;
        var auth = policy.FailureStatus(Second, 2, ServerConnectionStatus.SetupRequired, options, out _)!.Value;
        Check(first.Kind == MonitoringAlertKind.Unavailable && auth.Kind == MonitoringAlertKind.SignInRequired &&
            first.Id != auth.Id && policy.FailureStatus(First, 1, ServerConnectionStatus.Unavailable, options, out _) == null,
            "Other recorders and confirmed authentication errors have independent failure episodes.");
        Check(policy.EndFailure(First, 1) == first.Id &&
            policy.FailureStatus(First, 1, ServerConnectionStatus.Unavailable, options, out _) is { } later && later.Id != first.Id,
            "Recovery resets the episode so a later failure can alert again.");
        var next = policy.FailureStatus(Second, 3, ServerConnectionStatus.Unavailable, options, out var retired);
        Check(next?.Id != auth.Id && retired == auth.Id && policy.EndFailure(Second, 2) == null &&
            policy.EndFailure(Second, 3) == next?.Id,
            "A replacement connection clears the old alert and an obsolete retirement cannot clear the new alert.");
        Check(policy.EndFailures().Length == 1 && policy.EndFailures().Length == 0, "Stopping clears unresolved episodes.");
        return Task.CompletedTask;
    }

    internal static async Task PermissionAndPrivacy()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        settings.LocalOnlyMode = true;
        var privacy = PrivacyNetworkPolicy.For(settings);
        var time = new Clock();
        var policy = new MonitoringNotificationPolicy(time);
        var incoming = Recording(time, First);
        var notice = policy.Transmission(incoming, Enabled, true, false, privacy.Allows(new Uri("https://independent.fixture")));
        Check(notice != null && !privacy.AllowsCloud &&
            policy.Transmission(incoming, Enabled, true, false, privacy.Allows(new Uri("https://app.joesscanner.com"))) == null,
            "Local Only permits private notifications from independent live connections and keeps Joe-managed connections blocked.");

        var shown = 0;
        Task Show(MonitoringAlert notice) { shown++; return Task.CompletedTask; }
        var alert = notice!.Value;
        Check(!await MonitoringNotificationDelivery.SendAsync(alert, () => true, () => Task.FromResult(false), Show) && shown == 0,
            "An OS denial suppresses delivery.");
        var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = true;
        var waiting = MonitoringNotificationDelivery.SendAsync(alert, () => current, () => permission.Task, Show);
        current = false;
        permission.SetResult(true);
        Check(!await waiting && shown == 0, "A late permission result must not deliver from a retired session or view.");
        Check(await MonitoringNotificationDelivery.SendAsync(alert, () => true, () => Task.FromResult(true), Show) && shown == 1,
            "A current local event can be delivered with permission.");
    }

    internal static Task DisplayTransitions()
    {
        var policy = new ScreenAwakeController();
        var device = new Display { Brightness = 0.64 };
        var options = new MonitoringDeviceOptions { ScreenAwake = true, LowLight = true, Brightness = 20 };
        var monitoring = true;
        void Update(bool foreground, bool powered) => policy.Update(options, monitoring, foreground, powered, device.Acquire);

        Update(true, false);
        Check(!device.Awake && device.Brightness == 0.64, "Charging-only mode must wait for external power.");
        Update(true, true);
        Check(device.Awake && device.Brightness == 0.20 && device.Acquired == 1, "Acquiring the display applies low light.");
        options = options with { Brightness = 30 };
        Update(true, true);
        Check(device.Brightness == 0.30 && device.Acquired == 1, "Changing brightness must retain the first restoration point.");
        Update(true, false);
        Check(!device.Awake && device.Brightness == 0.64 && monitoring, "Unplug restores the screen without ending monitoring.");
        Update(true, true);
        Update(false, true);
        Check(!device.Awake && device.Brightness == 0.64, "Deactivation or lock restores the original display state.");

        device.Awake = true;
        device.Brightness = 0.9;
        options = options with { ChargingOnly = false };
        Update(true, false);
        options = options with { LowLight = false };
        Update(true, false);
        Check(device.Awake && device.Brightness == 0.9, "Turning off low light restores brightness while awake mode is active.");
        options = options with { LowLight = true };
        Update(true, false);
        monitoring = false;
        Update(true, false);
        Check(device.Awake && device.Brightness == 0.9, "Stop restores the device’s original true idle policy and brightness.");
        device.Awake = false;
        monitoring = true;
        Update(true, false);
        options = options with { ScreenAwake = false };
        Update(true, false);
        Check(!device.Awake && device.Brightness == 0.9, "Disabling the feature releases the screen and restores brightness.");
        return Task.CompletedTask;
    }

    private static NotificationRecording Recording(Clock time, Guid server, CallCardMetadata? metadata = null) =>
        new(server, TalkgroupKey.From(server, metadata ?? Metadata), false, time.GetUtcNow(), 2, true, true);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Clock : TimeProvider
    {
        private TimeSpan _elapsed;
        private static readonly DateTimeOffset Start = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Start + _elapsed;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(int seconds) => _elapsed += TimeSpan.FromSeconds(seconds);
    }

    private sealed class Display
    {
        internal double Brightness;
        internal bool Awake;
        internal int Acquired;

        internal IMonitoringScreenLease Acquire()
        {
            Acquired++;
            var lease = new Lease(this, Awake);
            Awake = true;
            return lease;
        }

        private sealed class Lease(Display device, bool previousIdlePolicy) : IMonitoringScreenLease
        {
            public double Brightness { get => device.Brightness; set => device.Brightness = value; }
            public void Dispose() => device.Awake = previousIdlePolicy;
        }
    }
}
