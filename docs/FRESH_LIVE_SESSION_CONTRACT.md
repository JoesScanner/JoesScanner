# Fresh Live listening sessions

Phase 1 contract for the existing single-recorder app. It covers blueprint §3–4, §8, and acceptance scenarios SES-01–06, MAN-04, MOB-04, and UI-05. Later work can apply the same session boundaries across multiple connections.

## Independent state

| State | Contract |
| --- | --- |
| Connection intent | At process launch, the saved AutoPlay preference authorizes automatic connection to an eligible configured recorder. At runtime, user Play authorizes connection and user Stop withdraws it for that process. User Play and Stop leave the saved preference intact. |
| Listening session | Stopped has no active scanner transport or retry work. Starting can be an internal transition with a cancellable generation. Running has a current generation and may wait quietly for new calls. Connection failure and silence are separate from the user's Stop intent. |
| Automatic audio | A Running session can continuously play eligible new Live calls when Audio is enabled and Live holds playback ownership. The automatic queue can wait for work without stopping monitoring. Audio Off keeps monitoring and card ingestion active. |
| Playback ownership | One native owner at a time. Live automatic playback and explicit Live selections share this owner. History takes exclusive ownership while open. An explicit stopped selection can acquire ownership for a single clip. |
| Visible Live cards | The display starts empty on a new process, retains review cards and transcriptions on Stop, and clears them when a new listening session begins. Queue retirement and playback ownership changes do not erase cards. |

## Transitions

| Trigger | Listening and connection | Audio and cards |
| --- | --- | --- |
| Fresh process, AutoPlay Off | Remain Stopped. Zero automatic Live recorder connections, including on the first Live page appearance. | Empty Live list. No queued or cached recording starts automatically. |
| Fresh process, AutoPlay On, eligible recorder | Begin one new listening generation at the current Live edge. A persisted reconnect permit, previous Stop, or saved playback position has no authority over this launch decision. | Empty Live list at startup. Only eligible arrivals from the new session can start automatic audio. |
| On-screen Play while Stopped | Start a fresh Running session, including with AutoPlay Off. System Play from Stopped has the same start intent. | Clear prior-session Live cards and enable continuous queue traversal for eligible arrivals when Audio is enabled. A test should admit and finish at least two recordings without another Play or tap. |
| Explicit Stop | Retire the generation immediately and cancel its startup, connection, retries, and preloads. Late completion of cleanup does not affect a replacement run. Repeated Stop does not create another teardown. | Cancel Live audio and clear pending automatic traversal. Keep the visible cards and their text for review. |
| Stop then Play | Admit a new generation and set its Live edge before exposing automatic playback. | Clear prior Live cards once as the new session starts. Earlier queued calls, late updates, and old playback positions stay out of automatic traversal. |
| Process restart | Apply the launch rule for the saved AutoPlay setting, including after a crash or while an old clip was active. Keep saved settings, server configuration, and credentials. | Start with an empty Live list and fresh playback boundary. Retained recording identities or transport recovery checkpoints do not authorize old playback. |
| Tap a visible call while Stopped | Remain Stopped. An explicit recording acquisition may fetch that clip if authorized. It does not start scanner listening or retries. | Play just the chosen recording and end without advancing. The tap leaves AutoPlay and the global Audio setting intact. |
| Open History and return | Opening and closing History preserve the live generation if it remains Running. A Stopped session stays Stopped, including if Stop occurred while History was open. | History owns audio exclusively while open. On return to a Running session, fence playback at the current Live edge and exclude pending calls through that boundary, including the interrupted Live clip and calls observed during History. Keep the Live review cards. |

## Generation and Live edge

Every asynchronous startup, transport, queue, and playback result belongs to the generation or playback lease that started it. Cancellation releases work where possible. A result that arrives after Stop or ownership loss must also pass a current-generation or lease check before it can publish. An old event may not change visible cards, start audio, advance the current queue, or alter the replacement's recovery checkpoint. A native end confirmed before retirement may finish persisting its original disposition without advancing the new session. A native end first reported after cancellation cannot confirm completion.

The Live edge uses the client's admission boundary. It does not depend on the recorder's wall-clock ordering. A first poll can establish a baseline, including an empty snapshot. A recovered call or duplicate notification from before the boundary may be retained for reconciliation or History while remaining ineligible for automatic Live playback. The History return boundary changes queue eligibility within the same listening generation.

## Phase 1 regression baseline

The Phase 1 cases use the existing console harness, fake audio, controlled callback delivery, and isolated SQLite files. Run the dedicated contract mode with the repository's existing test project.

```
dotnet run --project tests/CallCardOptions.Tests/CallCardOptions.Tests.csproj --no-restore -- --fresh-live-session
```

The contract mode runs every case and exits 1 if any assertion fails. The normal runner still exercises the existing suite, including older History expectations that need revision when its return policy changes.

| Requirement | Evidence available in Phase 1 |
| --- | --- |
| AutoPlay Off and On at process launch | Source trace below. The harness lacks the MAUI window and first page appearance paths. Test both launch states with a fake transport at that boundary in Phase 2. |
| Actual on-screen Play with AutoPlay Off | Source trace below. The harness can drive the linked queue directly. The XAML-bound command and its actual startup path require a production entry-point seam. |
| Stop, card review, and stopped one-shot playback | The new Stop case joins the linked lifecycle, playback attempt, and durable queue. It asserts cancellation, pending eligibility, retained cards, and no automatic continuation. The fixture supplies the view model's Stop wiring. |
| Stop then Play | Existing fresh-run tests and the new delayed-read case verify queue generation and old audio exclusion. The fixture clears its own cards, so these tests do not establish that the actual on-screen Play clears the production collection. |
| Process restart | The existing server authentication runner exercises fresh socket and HTTP baselines, old active and waiting clips, inherited recovery, and first eligible new observations. Actual app launch routing is a separate integration check. |
| Stale events, audio completions, and transport responses | The new delayed-read and retired-callback cases exercise the linked playback code, cancellation and completion markers, and durable write fences after replacement. |
| History return | New cases exercise the real linked History close and Live lease-change paths while Stopped and Running. The Running case requires only History audio and the first post-return Live arrival to play. |

[Contract test cases](../tests/CallCardOptions.Tests/FreshLiveSessionContractTests.cs) · [Existing fresh transport tests](../tests/ServerAuthentication.Tests/FreshListeningTests.cs)

## Production entry-point gaps recorded in Phase 1

| Entry point | Current behavior to address |
| --- | --- |
| Startup | `App.CreateWindow` schedules `StartMonitoringWithAutoplayAsync` for AutoPlay On. That method delegates to `RestoreMonitoringAsync`, which requires `MonitoringIntentStore.CanRestore`. `MainPage.OnAppearing` separately calls `TryAutoReconnectAsync` without an AutoPlay guard. Route first launch through a single fresh-start decision. Gate page or foreground recovery to the current process and generation. [App.xaml.cs](../JoesScanner/App.xaml.cs#L133) · [MainPage.xaml.cs](../JoesScanner/Views/MainPage.xaml.cs#L973) · [MainViewModel.cs](../JoesScanner/ViewModels/MainViewModel.cs#L3222) · [MonitoringIntentStore.cs](../JoesScanner/Services/MonitoringIntentStore.cs#L20) |
| On-screen Play | The `MediaPlayStop` button binds `ToggleConnectionCommand`, which calls `ToggleConnectionAsync`. That method takes the default `StartMonitoringAsync` path with `driveQueue: false`. `RequestQueueForLiveArrival` requires AutoPlay or run-level playback intent. `Start()` and `SystemPlayAsync` already request `driveQueue: true`. Route user Play through a shared explicit start intent. [MainPage.xaml](../JoesScanner/Views/MainPage.xaml#L91) · [Command binding](../JoesScanner/ViewModels/MainViewModel.cs#L318) · [Toggle handler](../JoesScanner/ViewModels/MainViewModel.cs#L3563) · [Queue arrival gate](../JoesScanner/ViewModels/MainViewModel.cs#L3311) · [System Play](../JoesScanner/ViewModels/MainViewModel.cs#L493) |
| Stop and next Play | `ToggleConnectionAsync` and `StopCommand` call `StopMonitoringAsync`, `StopMonitoringCoreAsync`, and `RecordMonitoringStop`. The latter already preserves visible cards. `StartMonitoringCoreAsync` clears the pending buffer and opens fresh durable playback while retaining old cards. Clear Live cards at accepted fresh start. Also gate `RefreshStaleCallMetadataAsync` to an active run so page appearance after Stop cannot poll old cards. [Stop](../JoesScanner/ViewModels/MainViewModel.cs#L2216) · [Fresh queue initialization](../JoesScanner/ViewModels/MainViewModel.cs#L1775) · [Metadata refresh](../JoesScanner/ViewModels/MainViewModel.cs#L3327) |
| History close | `HistoryViewModel.OnPageClosedAsync` releases the shared lease. `MainViewModel.OnPlaybackOwnershipChanged` can immediately request the old Live queue. Reserve a fresh playback floor before the first queue request on return. Keep the same Running generation or leave it Stopped. [History close](../JoesScanner/ViewModels/HistoryViewModel.PlaybackOwnership.cs#L73) · [Live lease change](../JoesScanner/ViewModels/MainViewModel.PlaybackOwnership.cs#L59) |

Phase 2 should wire those existing entry points for one recorder. Keep the listening lifecycle, durable reconciliation, and native ownership protections. Test launch and button behavior through the smallest entry-point seams that exercise the real intent and card collection. Update the older History replay expectations alongside the return-policy change.

## Phase 2 regression coverage

The same contract runner now links the production session entry points for launch, visible Play/Stop, system Play, fresh card clearing, and passive metadata refresh. It uses fake recorder and audio I/O with the real listening lifecycle, durable queue, and playback ownership code. The History cases cover returning while Stopped or Running, interrupted Live audio, admissions immediately after return, and late HTTP recovery below the playback edge. The console tests exercise the command handlers and collection changes. Windows packaging is a separate build check.

[Session entry points](../JoesScanner/ViewModels/MainViewModel.ListeningSession.cs) · [Playback ownership](../JoesScanner/ViewModels/MainViewModel.PlaybackOwnership.cs) · [Contract tests](../tests/CallCardOptions.Tests/FreshLiveSessionContractTests.cs)
