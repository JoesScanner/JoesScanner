# Playback behavior

This document and the playback integration tests define JoesScanner's playback compatibility contract.

## A. Live queue

- Assign a permanent SQLite arrival sequence when the app first admits a recording. Use that sequence for playback, transport, and Live boundaries. Recorder timestamps are used for display, History, and recording access checks.
- Automatic playback starts with the oldest eligible pending call. Keep the playing card stable while incoming recordings accumulate. A new arrival wakes the queue and leaves active audio alone.
- Keep the waiting indicator and catch-up speed relative to the listening position. The waiting indicator counts playable cards represented in the Live view and its pending card buffer. SQLite can retain additional recordings beyond those buffers. Catch-up speed uses durable pending arrivals, including off-screen calls, and returns to normal speed at the Live edge.
- A pending recording with unavailable audio keeps its place. A relevant audio update can make it playable, and the user can Retry, Skip, or Jump to Live.

## B. Manual selection

- A Live card tap cancels the old playback request immediately, including preparation and native playback. Prepare the latest selected call as soon as possible. Wait for the previous native player to release its resources before starting the replacement. A confirmed completion write from the old request must not block preparation or the new native start.
- Continue forward from the selected arrival. A forward seek past pending calls makes those calls ineligible for automatic playback from the new position. Their recordings remain available through authorized History and an explicit backward seek. A canceled call retains its pending disposition unless its native end was already confirmed.
- A backward seek can deliberately replay eligible, newer completed calls that were visible when the user selected the anchor. Merge those replays with pending calls in arrival order. The selected card may also be replayed directly. Preserve terminal dispositions and the original arrival sequence. At the end of the pass, wait for later arrivals.
- An explicit tap can play muted audio. Disabled or hidden calls remain hidden and cannot take playback. An explicit tap can drive playback for this listening session even when AutoPlay is off. It leaves the saved preference unchanged.
- For example, with arrivals A, B, C, D, E and B playing, tap D to hear D, E, and the next arrivals. From D, tap B to hear B, C, D, E when those newer calls are eligible for the pass.

## C. Live transport

- Previous moves toward older arrivals and Next moves toward newer arrivals. Both can find recordings outside the visible card window, skip unavailable or filtered transport candidates, and change the playback position when a target is selected.
- Jump to Live changes both the playback and visible position. It discards the older playback backlog at a reserved arrival boundary. Admissions after that reservation remain eligible. Authorized History still has access to older recordings.

## D. Listening sessions

- A clean launch and explicit Stop followed by Play start fresh. A fresh listening session excludes prior playback backlog, retains recording identities and History access, and leaves unfinished HTTP reconciliation available.
- Audio Off continues monitoring and receiving call information. Turning audio on reserves a Live edge so older and muted-period arrivals do not autoplay. New arrivals after the reserved edge remain eligible.
- Explicit Stop retires the monitoring run and its playback, transport, and recovery requests. Delayed callbacks from that run cannot start playback or monitoring.

## E. AutoPlay

- AutoPlay is a saved preference for automatic startup. Enabling audio and opening or changing Settings leave it as configured.
- A user-driven listening run, manual Live selection, or an address alert can drive playback for the current run without changing AutoPlay. Address alerts use the same forward traversal as a card tap.
- A newly arrived call alone starts playback only when AutoPlay or the current run's playback intent authorizes it.

## F. History

- A selected History result starts playback at that result and continues through the applicable result list. Previous and Next operate on the History result set.
- History owns the audio player while active. Live resumes according to its existing listening intent after History releases ownership. History keeps its own controls and layout.
- Verify recording entitlement before playback, including replays and protected recovery results.

## G. Audio and filters

- Honor talkgroup Disable and Mute for automatic playback. Allow explicit playback of muted calls where supported. Keep hidden recordings out of the display and replay set.
- Preserve the speed controls, including backlog catch-up and normal speed at Live.
- Preserve Mix normally, Mix and duck, and Exclusive on mobile. Brief ducking is limited to three seconds. Release native player resources, session or focus leases, and temporary playback files at the appropriate boundary.
- Honor the configured Pause or Continue behavior on wired or Bluetooth route disconnection. A route configured to pause requires explicit resume.

## H. Interruptions

- A short platform interruption can resume the existing listening position. At the configured threshold, return to Live. The default is five minutes. Immediately, Never, presets, and custom durations of 1–1,440 minutes are supported. The threshold is inclusive.
- Silence by itself does not start interruption recovery. Explicit Stop and a fresh listening session override interruption recovery. An automatic jump preserves work admitted after its reserved boundary.

## I. SQLite and recovery

- Maintain SQLite v5 arrival order, deduplicate by server/account/recording ID, and keep account and entitlement checks in every eligible playback path.
- A fresh-session boundary decides what may autoplay. The automatic playback floor records where pending playback is allowed to start after seeks and audio changes. The in-memory manual traversal records the current replay position and the visible replay set. Native playback ownership and pending/completed/skipped dispositions remain independent.
- A fresh session or jump can suppress old recovery results from autoplay while durable HTTP reconciliation retains its own window and checkpoint. Keep that window across retries and app restarts. SQLite-only pending recordings can still receive late metadata and audio updates.
- Preserve the disposition of migrated queues. Legacy rows without arrival data have a deterministic migration order. Their original local arrival order cannot be reconstructed.

## J. Historical backlog limits

Earlier requirements described a warning at about 80 calls behind and forced return to Live at 100 calls, with older recordings available in History. The repository's older card-anchor implementation bounded the in-memory list. The current 100-card caps limit the visible list and pending card buffer. The durable queue retains records beyond those caps.

The current implementation has no 80-call warning or count-triggered forced jump at 100. Reinstating that rule would require deciding whether the threshold counts all durable pending recordings, eligible ones, or just cards, and would make valid pending calls ineligible for automatic playback. The limits above document the current behavior. Explicit Jump to Live and configured interruption recovery remain the ways to skip a backlog.
