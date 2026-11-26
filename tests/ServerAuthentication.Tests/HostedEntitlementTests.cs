using System.Net;
using JoesScanner.Models;
using JoesScanner.Services;
using JoesScanner.ViewModels;

internal static partial class WordPressOwnershipTests
{
    private static readonly DateTimeOffset VerificationStart = DateTimeOffset.Parse("2026-11-24T12:00:00Z");

    internal static async Task GraceAcrossHostedFeatures()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "grace-account", "test-session");
        var clock = new EntitlementClock(VerificationStart);
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ => ++calls == 1
            ? VerifiedResponse(clock, 1)
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);

        Check((await service.EnsureSubscriptionAsync(default)).EffectiveTier == 1, "First verification must establish Standard.");
        var checkedAt = settings.ReadHostedSubscription().CheckedUtc;
        var account = RecordingPlaybackAccess.AccountKey(settings, Recorder);
        using var monitoring = HostedEntitlement.For(settings).Protect(default, clock);
        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        var outage = await service.EnsureSubscriptionForRecorderAsync(Recorder, default);
        Check(outage is { IsAllowed: true, ErrorCode: "offline_cached", EffectiveTier: 1, VerifiedOnlineTier: null } &&
            settings.ReadHostedSubscription().CheckedUtc == checkedAt, "A transient failure at 23:59 must retain the original Standard proof.");
        var history = await RecordingPlaybackAccess.VerifyHistoryAsync(settings, service, Recorder, default, clock);
        var recent = clock.GetUtcNow().AddMinutes(-1);
        Check(history.Tier == 1 && history.Allows(recent), "Hosted History must use the same Standard grant during the outage.");
        RecordingPlaybackAccess.RequireCachedEntitlement(settings, Recorder, account, recent, false, false, clock);
        Check(calls == 2 && !monitoring.Token.IsCancellationRequested, "Concurrent features must reuse the last outcome and remain authorized.");

        clock.Advance(TimeSpan.FromMinutes(2));
        Check(monitoring.Token.IsCancellationRequested, "A connected recorder must lose its grant even in the middle of an outage.");
        Check(!(await service.EnsureSubscriptionAsync(default)).IsAllowed && !history.IsCurrent(settings),
            "At 24:01 Live and an existing History grant must both be denied.");
        await AccessDeniedAsync(RecordingPlaybackAccess.VerifyHistoryAsync(settings, service, Recorder, default, clock));
        await AccessDeniedAsync(Task.Run(() => RecordingPlaybackAccess.RequireCachedEntitlement(
            settings, Recorder, account, recent, false, false, clock)));
    }

    internal static async Task AuthoritativeDenialsEndGrace()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.OK })
        {
            using var fixture = new SavedCredentialTests.Fixture();
            var settings = fixture.Load();
            SignIn(settings, "revoked-account", "test-session");
            var clock = new EntitlementClock(VerificationStart);
            var calls = 0;
            using var client = new HttpClient(new StubHandler(_ => ++calls switch
            {
                1 => VerifiedResponse(clock, 2),
                2 when status == HttpStatusCode.OK => JsonResponse(new { ok = true, subscription = new { active = false, tier_level = 2 } }),
                2 => new HttpResponseMessage(status),
                _ => throw new TaskCanceledException("Synthetic outage after authoritative denial.")
            }));
            using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
            Check((await service.EnsureSubscriptionAsync(default)).IsAllowed, "The account must start with a verified grant.");
            using var active = HostedEntitlement.For(settings).Protect(default, clock);
            clock.Advance(TimeSpan.FromMinutes(30));
            var denial = await service.EnsureSubscriptionAsync(default);
            Check(!denial.IsAllowed && denial.RequiresIntervention && active.Token.IsCancellationRequested &&
                settings.ReadHostedSubscription().CheckedUtc == null, "401, 403 and inactive replies must revoke proof and cancel protected work.");
            clock.Advance(TimeSpan.FromSeconds(15));
            Check(!(await service.EnsureSubscriptionAsync(default)).IsAllowed,
                "A timeout after confirmed revocation must never restore the revoked tier.");
        }

        using var unverifiedFixture = new SavedCredentialTests.Fixture();
        var unverified = unverifiedFixture.Load();
        SignIn(unverified, "unverified-account", "unverified-session");
        var unverifiedClock = new EntitlementClock(VerificationStart);
        using var unverifiedHttp = new HttpClient(new StubHandler(_ => new(HttpStatusCode.TooManyRequests)));
        using var unverifiedService = new SubscriptionService(unverified, new TelemetryStub(unverified), unverifiedHttp, unverifiedClock);
        Check(!(await unverifiedService.EnsureSubscriptionAsync(default)).IsAllowed &&
            HostedEntitlement.For(unverified).Current(unverifiedClock) == null, "A rate limit on a brand new account grants no access.");
        var independent = await RecordingPlaybackAccess.VerifyHistoryAsync(unverified, unverifiedService,
            "https://independent.test", default, unverifiedClock, independent: true);
        Check(independent.Allows(VerificationStart.AddDays(-20)), "Independent History must keep its access.");
    }

    internal static async Task EarliestVerifiedDeadline()
    {
        foreach (var credentialFirst in new[] { false, true })
        {
            using var fixture = new SavedCredentialTests.Fixture();
            var settings = fixture.Load();
            SignIn(settings, "deadline-account", "test-session");
            var clock = new EntitlementClock(VerificationStart);
            var early = VerificationStart.AddMinutes(40);
            var late = VerificationStart.AddHours(12);
            var calls = 0;
            using var client = new HttpClient(new StubHandler(_ => ++calls == 1
                ? VerifiedResponse(clock, 2, credentialFirst ? late : early, credentialFirst ? early : late)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
            Check((await service.EnsureSubscriptionAsync(default)).IsAllowed, "The initial verification must succeed.");
            var entitlement = HostedEntitlement.For(settings);
            using var work = entitlement.Protect(default, clock);
            clock.Advance(TimeSpan.FromMinutes(39));
            Check((await service.EnsureSubscriptionAsync(default)).IsAllowed &&
                !work.Token.IsCancellationRequested, "The account should remain usable until the earliest deadline.");
            clock.Advance(TimeSpan.FromMinutes(1));
            Check(work.Token.IsCancellationRequested && entitlement.Current(clock) == null &&
                !(await service.EnsureSubscriptionAsync(default)).IsAllowed, "The earliest verified expiry is exclusive even during an outage.");
        }

        using var expiredFixture = new SavedCredentialTests.Fixture();
        var expired = expiredFixture.Load();
        SignIn(expired, "already-expired", "test-session");
        var expiredClock = new EntitlementClock(VerificationStart);
        using var expiredHttp = new HttpClient(new StubHandler(_ => VerifiedResponse(expiredClock, 1, credential: VerificationStart)));
        using var expiredService = new SubscriptionService(expired, new TelemetryStub(expired), expiredHttp, expiredClock);
        Check(await expiredService.EnsureSubscriptionAsync(default) is { IsAllowed: false, ErrorCode: "expired" },
            "A successful HTTP response must not confer an already expired credential.");
    }

    internal static async Task SharedVerificationCadence()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "concurrent-account", "test-session");
        var clock = new EntitlementClock(VerificationStart);
        var started = Signal();
        var finish = Signal();
        var periodic = Signal();
        var publish = Signal();
        var calls = 0;
        using var client = new HttpClient(new AsyncStubHandler(async (_, token) =>
        {
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    started.TrySetResult();
                    await finish.Task.WaitAsync(token);
                    return VerifiedResponse(clock, 1);
                case 2:
                    periodic.TrySetResult();
                    await publish.Task.WaitAsync(token);
                    return VerifiedResponse(clock, 2);
                default:
                    throw new InvalidOperationException("Unexpected extra verification.");
            }
        }));
        using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
        using var caller = new CancellationTokenSource();
        var canceledStartup = service.EnsureSubscriptionAsync(caller.Token);
        await started.Task.WaitAsync(Timeout);
        var live = service.EnsureSubscriptionForRecorderAsync(Recorder, default);
        var history = RecordingPlaybackAccess.VerifyHistoryAsync(settings, service, Recorder, default, clock);
        caller.Cancel();
        try
        {
            await canceledStartup;
            throw new InvalidOperationException("The canceled startup waiter returned a grant.");
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
        Check(calls == 1, "Canceling one waiter may not restart the shared verification.");
        finish.SetResult();
        Check((await live.WaitAsync(Timeout)).EffectiveTier == 1 && (await history.WaitAsync(Timeout)).Tier == 1,
            "The other hosted clients must receive the shared verification.");

        using var monitoring = new CancellationTokenSource();
        var firstRecorder = service.MaintainHostedSubscriptionAsync(Recorder, monitoring.Token);
        var secondRecorder = service.MaintainHostedSubscriptionAsync(Recorder, monitoring.Token);
        clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));
        Check(calls == 1 && !periodic.Task.IsCompleted, "A success remains reusable until the next 30-minute check.");
        clock.Advance(TimeSpan.FromSeconds(1));
        await periodic.Task.WaitAsync(Timeout);
        var joinedHistory = RecordingPlaybackAccess.VerifyHistoryAsync(settings, service, Recorder, default, clock);
        var joinedForeground = ForegroundRecoveryEntitlement.TryVerifyAsync(settings, service, Recorder, clock, default);
        Check(calls == 2, "Both recorder timers, History and foreground recovery must join one due request.");
        publish.SetResult();
        Check((await joinedHistory.WaitAsync(Timeout)).Tier == 2 && (await joinedForeground.WaitAsync(Timeout))?.Tier == 2 &&
            calls == 2, "All waiters must observe the new verified tier.");
        await monitoring.CancelAsync();
        await Task.WhenAll(firstRecorder, secondRecorder).WaitAsync(Timeout);
        clock.Advance(TimeSpan.FromMinutes(30));
        Check(calls == 2, "Stopping the hosted monitoring owners must cancel their cadence.");
    }

    internal static Task RevocationCancelsProtectedWork() => RevocationCancelsProtectedWorkAsync(fromSettings: false);

    internal static Task SettingsRevocationCancelsProtectedWork() => RevocationCancelsProtectedWorkAsync(fromSettings: true);

    private static async Task RevocationCancelsProtectedWorkAsync(bool fromSettings)
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "playing-account", "test-session");
        var clock = new EntitlementClock(VerificationStart);
        var calls = 0;
        using var auth = new HttpClient(new StubHandler(_ => ++calls == 1
            ? VerifiedResponse(clock, 2) : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var subscription = new SubscriptionService(settings, new TelemetryStub(settings), auth, clock);
        Check((await subscription.EnsureSubscriptionAsync(default)).IsAllowed, "Hosted playback needs a verified starting grant.");
        var entitlement = HostedEntitlement.For(settings);
        var owners = new PlaybackOwnership();
        var live = owners.Live!;
        var entered = Signal();
        var mediaStarted = Signal();
        var independentStarted = Signal();
        using var testLifetime = new CancellationTokenSource(Timeout);
        var manager = new ServerConnectionManager(clock);
        var independent = new SavedScannerConnection(Guid.NewGuid(), "https://independent.test", "Independent", null, true);
        manager.Synchronize([independent], testLifetime.Token, async connection =>
        {
            manager.Report(connection, ServerConnectionStatus.Monitoring);
            independentStarted.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, connection.Token);
        });
        await independentStarted.Task.WaitAsync(Timeout);
        var independentConnection = manager.Snapshot().Single();

        using var playing = entitlement.Protect(testLifetime.Token, clock);
        using var preparing = entitlement.Protect(testLifetime.Token, clock, recordedAt: VerificationStart.AddDays(-2), history: true);
        var protectedAudio = owners.RunNativeAsync(live, playing.Token, async token =>
        {
            entered.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        });
        using var media = new HttpClient(new AsyncStubHandler(async (_, token) =>
        {
            mediaStarted.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled media reached the download result.");
        }));
        var root = Path.GetDirectoryName(fixture.DbPath)!;
        var cache = new PlaybackAudioCache(Path.Combine(root, "audio"), Path.Combine(root, "transfers"), 100,
            availableBytes: _ => 2L * 1024 * 1024 * 1024);
        var cacheKey = RecordingCacheKey.Create(Guid.NewGuid(), Recorder, "revoked-clip",
            RecordingPlaybackAccess.AccountKey(settings, Recorder), settings.AuthServerBaseUrl);
        var prepared = cache.AcquireAsync(cacheKey, (_, _) =>
        {
            preparing.Validate();
            return Task.CompletedTask;
        }, async (transfer, _) => await RecordingAudioRecovery.DownloadAsync(media, "https://media.test/controlled.wav",
            transfer, preparing.Token) ?? throw new InvalidOperationException("Missing synthetic audio."), testLifetime.Token);
        await Task.WhenAll(entered.Task, mediaStarted.Task).WaitAsync(Timeout);
        clock.Advance(TimeSpan.FromMinutes(30));
        if (fromSettings)
        {
            var editor = new SettingsViewModel(settings, subscription);
            Check(!await editor.PressValidateAsync() && editor.ServerValidationMessage == "Auth server responded with HTTP 403.",
                "The real Settings validation handler must publish the denial and show the error.");
            await AccessDeniedAsync(Task.Run(() => RecordingPlaybackAccess.RequireCachedEntitlement(settings, Recorder,
                RecordingPlaybackAccess.AccountKey(settings, Recorder), VerificationStart, false, false, clock)));
            await AccessDeniedAsync(RecordingPlaybackAccess.VerifyHistoryAsync(settings, subscription, Recorder, default, clock));
        }
        else
            Check((await subscription.EnsureSubscriptionAsync(default)).ErrorCode == "http_403", "The verifier must publish the denial.");
        await CanceledAsync(protectedAudio);
        await CanceledAsync(prepared);
        Check(cache.UsageBytes == 0, "The retired acquisition must not publish audio to the cache.");
        Check(!owners.CanPublishLivePlayback && ReferenceEquals(owners.Live, live) &&
            manager.IsCurrent(independentConnection) && !independentConnection.Token.IsCancellationRequested,
            "Revocation must drain protected audio without retiring the Live owner or independent connection.");

        var queue = new PlaybackCoordinator(settings);
        Check(queue.CanStartQueuePlayback("IndependentAfterRevocation", Recorder, true, true, true, false, false, false, 1),
            "A selected hosted recorder must not veto an eligible independent queue.");
        var nextClip = false;
        await owners.RunNativeAsync(live, testLifetime.Token, _ =>
        {
            nextClip = true;
            return Task.CompletedTask;
        });
        Check(nextClip, "Independent audio must acquire the existing Live owner.");
        manager.Retire(_ => true);
        await independentConnection.Worker.WaitAsync(Timeout);
    }

    internal static Task HostedTierChangesPreserveLive() => HostedTierChangesPreserveLiveAsync(fromSettings: false);

    internal static Task SettingsHostedTierChangesPreserveLive() => HostedTierChangesPreserveLiveAsync(fromSettings: true);

    private static async Task HostedTierChangesPreserveLiveAsync(bool fromSettings)
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "tier-account", "test-session");
        var clock = new EntitlementClock(VerificationStart);
        var tier = 1;
        using var client = new HttpClient(new StubHandler(_ => VerifiedResponse(clock, tier)));
        using var service = new SubscriptionService(settings, new TelemetryStub(settings), client, clock);
        var first = await RecordingPlaybackAccess.VerifyHistoryAsync(settings, service, Recorder, default, clock);
        var archived = VerificationStart.AddDays(-2);
        Check(first.Tier == 1 && !first.Allows(archived), "Standard must reject older audio.");
        var entitlement = HostedEntitlement.For(settings);
        using var live = entitlement.Protect(default, clock);
        using var standardSearch = entitlement.Protect(default, clock, expectedTier: 1);
        var accountVersion = settings.ReadAccountSession().AccountVersion;
        var editor = new SettingsViewModel(settings, service);
        tier = 2;
        clock.Advance(TimeSpan.FromMinutes(30));
        if (fromSettings)
            Check(await editor.PressValidateAsync(), "The Settings upgrade must publish a new verified tier.");
        var premium = await RecordingPlaybackAccess.VerifyHistoryAsync(settings, service, Recorder, default, clock);
        Check(premium is { Tier: 2 } && premium.Allows(archived) && standardSearch.Token.IsCancellationRequested &&
            !live.Token.IsCancellationRequested, "An upgrade must unlock History and retire the old search without touching live audio.");
        using var oldClip = entitlement.Protect(default, clock, recordedAt: archived, history: true);
        using var recentClip = entitlement.Protect(default, clock, recordedAt: VerificationStart, history: true);

        tier = 1;
        clock.Advance(TimeSpan.FromMinutes(30));
        if (fromSettings)
            Check(await editor.PressValidateAsync(), "The Settings downgrade must publish the restricted tier.");
        var downgraded = await RecordingPlaybackAccess.VerifyHistoryAsync(settings, service, Recorder, default, clock);
        Check(!downgraded.Allows(archived) && !premium.IsCurrent(settings) && oldClip.Token.IsCancellationRequested &&
            !recentClip.Token.IsCancellationRequested && !live.Token.IsCancellationRequested &&
            settings.ReadAccountSession().AccountVersion == accountVersion,
            "Downgrade must immediately fence old History work while permitted hosted audio continues.");
        settings.ClearServerCredentials(Recorder);
        Check(live.Token.IsCancellationRequested && recentClip.Token.IsCancellationRequested,
            "Sign-out must retire the remaining protected work.");
    }

    internal static async Task RevocationCancelsExportBody()
    {
        using var fixture = new SavedCredentialTests.Fixture();
        var settings = fixture.Load();
        SignIn(settings, "download-account", "test-session");
        var clock = new EntitlementClock(VerificationStart);
        var checks = 0;
        using var auth = new HttpClient(new StubHandler(_ => ++checks == 1
            ? VerifiedResponse(clock, 2) : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var service = new SubscriptionService(settings, new TelemetryStub(settings), auth, clock);
        var reading = Signal();
        using var media = new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PausedAudioBody(reading))
        }));
        var exports = new CallDownloadService(settings, service, media, clock);
        var call = new CallItem
        {
            AudioUrl = Recorder + "/revoked.wav",
            BackendId = "revoked-export",
            RecordingServer = Recorder,
            RecordingTimeUtc = VerificationStart,
            RecordingAccess = new(RecordingOrigin.History, true, RecordingPlaybackAccess.AccountKey(settings, Recorder))
        };
        var previousDirectory = FileSystem.CacheDirectory;
        var output = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.DbPath)!, "exports"));
        FileSystem.CacheDirectory = output.FullName;
        try
        {
            var sharedBefore = Microsoft.Maui.ApplicationModel.DataTransfer.Share.Default.Shared.Count;
            var download = exports.DownloadSingleAsync(call);
            await reading.Task.WaitAsync(Timeout);
            clock.Advance(TimeSpan.FromMinutes(30));
            Check(!(await service.EnsureSubscriptionAsync(default)).IsAllowed, "The new account decision must deny.");
            await download.WaitAsync(Timeout);
            Check(!output.EnumerateFiles().Any() &&
                Microsoft.Maui.ApplicationModel.DataTransfer.Share.Default.Shared.Count == sharedBefore,
                "Revocation after response headers must cancel the copy and discard the incomplete export.");
        }
        finally
        {
            FileSystem.CacheDirectory = previousDirectory;
        }
    }

    private sealed class PausedAudioBody(TaskCompletionSource reading) : MemoryStream
    {
        public override bool CanSeek => false;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => PauseAsync(token);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => new(PauseAsync(token));

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token) => PauseAsync(token);

        private async Task<int> PauseAsync(CancellationToken token)
        {
            reading.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return 0;
        }
    }

    private static HttpResponseMessage VerifiedResponse(EntitlementClock clock, int tier,
        DateTimeOffset? subscription = null, DateTimeOffset? credential = null) => JsonResponse(new
    {
        ok = true,
        session_token = "test-session",
        credential_expires_at = credential?.ToString("O"),
        subscription = new
        {
            active = true,
            tier_level = tier,
            expires_at = (subscription ?? clock.GetUtcNow().AddDays(3)).ToString("O"),
            period_end_at = clock.GetUtcNow().AddDays(4).ToString("O")
        }
    });

    private static async Task AccessDeniedAsync(Task task)
    {
        try { await task; }
        catch (RecordingAccessException) { return; }
        throw new InvalidOperationException("Protected work received access without a current grant.");
    }

    private static async Task CanceledAsync(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("The protected operation continued after authorization was revoked.");
    }

    private sealed class EntitlementClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ClockTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ClockTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            ClockTimer[] ready;
            lock (_gate)
            {
                _now += elapsed;
                ready = _timers.Where(timer => timer.DueAt <= _now).ToArray();
                foreach (var timer in ready)
                    timer.DueAt = null;
            }
            foreach (var timer in ready)
                timer.Fire();
        }

        private sealed class ClockTimer(EntitlementClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal DateTimeOffset? DueAt { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Check(period == global::System.Threading.Timeout.InfiniteTimeSpan, "This fixture uses one-shot timers.");
                lock (owner._gate)
                    DueAt = dueTime == global::System.Threading.Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }

            internal void Fire() => callback(state);

            public void Dispose()
            {
                lock (owner._gate)
                    owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
