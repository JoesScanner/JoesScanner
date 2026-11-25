# Playback behavior

This document and the playback integration tests define JoesScanner's playback compatibility contract.

## A. Live queue

- Assign a permanent SQLite arrival sequence when the app first admits a recording. Use that sequence for playback, transport, and Live boundaries. Recorder timestamps are used for display, History, and recording access checks.
- The automatic scheduler selects eligible pending calls for the active audio source using its grouping and Favorites settings. A new arrival wakes the queue and leaves active audio alone.
- The waiting indicator counts eligible ready recordings for the automatic audio source. Catch-up speed uses the active playback scope, including durable off-screen calls, and returns to normal speed at its edge.
- A pending recording with unavailable audio keeps its place. A relevant audio update can make it playable, and the user can Retry, Skip, or Jump to Live.

## B. Manual selection

- Prepare a Live card successfully before retiring the current playback request. Failed or superseded preparations leave the current clip and its traversal intact. The new selection can take the native owner once the old clip releases it.
- When monitoring is Running and Audio is On, continue in original client-arrival order within the originating view. Combined covers the active recorders, a recorder view stays within that recorder, and Favorites continues with Favorites across active recorders. A display-only switch preserves the captured scope. A change of the actual automatic audio source in Selected Only mode finishes the current native clip and retires the pass at the existing source boundary.
- A backward seek can replay eligible completed recordings captured in that view at selection time. Keep their original disposition and sequence. Previously visible completed Favorites stay available if their cards are evicted during the pass.
- Favorites membership is read when choosing each next recording. The current clip finishes. An added Favorite can join with pending audio ahead of the cursor. Removing a Favorite excludes it from subsequent choices, and a completed card outside the original capture stays outside the replay set.
- A Favorites pass selects each eligible arrival at most once. Missing audio can join if it is ready and still ahead of the cursor at the next selection. At the end, advance the durable floor through the last processed arrival and resume normal scheduling. For Favorite F1, ordinary O1, Favorite F2, and ordinary O2 in arrival order, tapping F1 plays F1 and F2 in the pass. O1 remains pending below the floor for explicit review, and normal scheduling can then play O2.
- An explicit tap can play a filtered recording. Subsequent automatic selections honor Mute and Disable. A tap while Stopped or with Audio Off plays one clip. A manual selection can drive the current run while the saved AutoPlay preference remains unchanged.
- For example, with arrivals A, B, C, D, E and B playing, tap D to hear D, E, and the next arrivals. From D, tap B to hear B, C, D, E when those newer calls are eligible for the pass.

## C. Live transport

- Previous and Next select adjacent cards in the currently visible projection, including Favorites. A prepared selection starts a new pass using that view.
- Jump to Live changes both the playback and visible position. It discards the older playback backlog at a reserved arrival boundary. Admissions after that reservation remain eligible. Authorized History still has access to older recordings.

### Live reading

- Visual Live-follow starts at the actual top of each list. Scrolling the first card partly out of view is enough to leave the edge. While reading, the display preserves a retained card and its viewport offset where native layout permits. Transcript growth or correction keeps the same recording card, and transcript-only updates leave playback alone.
- Combined, Favorites and each recorder panel retain their own reading position and **New Calls** count. Count first admissions while reading if the card is still in that projection. Metadata updates and Favorite edits can change displayed content without creating new admissions.
- **New Calls** scrolls just its own list to the newest card and clears its count, as does deliberately scrolling back to the edge. Monitoring, audio playback and the durable queue continue at their current positions.
- **Waiting** and the backlog warning reflect eligible automatic audio. **Jump to Live** still applies the audio and queue boundary described above. Stop retires deferred scroll work while retaining displayed cards, and a fresh listening session resets the reading positions and counts.

Run the native smoke test in an interactive Windows session with `dotnet run --project tests/WindowsLiveReading.Smoke -c Debug`. The unpackaged fixture uses synthetic cards and measures the real CollectionView anchor through arrivals and transcript resizing.

## D. Listening sessions

- A clean launch and explicit Stop followed by Play start fresh. A fresh listening session excludes prior playback backlog, retains recording identities and History access, and leaves unfinished HTTP reconciliation available.
- Audio Off continues monitoring and receiving call information. Turning audio on reserves a Live edge so older and muted-period arrivals do not autoplay. New arrivals after the reserved edge remain eligible.
- Explicit Stop retires the monitoring run and its playback, transport, and recovery requests. Delayed callbacks from that run cannot start playback or monitoring.

## E. AutoPlay

- AutoPlay is a saved preference for automatic startup. Enabling audio and opening or changing Settings leave it as configured.
- A user-driven listening run, manual Live selection, or an address alert can drive playback for the current run without changing AutoPlay. Address alerts use the same forward traversal as a card tap.
- A newly arrived call alone starts playback only when AutoPlay or the current run's playback intent authorizes it.

## F. History

- History displays results newest-first. A committed selection captures the selected card and the currently loaded newer cards. Playback visits that fixed sequence oldest to newest, skips unavailable audio, and stops naturally at its newest recording.
- When playback starts from one of the oldest 25 loaded results and the search has more results, prefetch one page of up to 100 older recordings. Audio preparation and playback proceed independently of that request. Prefetched cards join the displayed list with its existing scroll anchor and become available for explicit selection. They never enter the active playback sequence, even if their timestamps place them between already loaded cards.
- Load More, Previous at the older boundary, and prefetch share the same pending request and search snapshot. A temporary failure leaves the page available for explicit retry. Archive changes require a new search. Stop cancels pending paging and retains loaded results. A replacement search or exit from History retires its pending requests.
- History owns the audio player while active. Live resumes according to its existing listening intent after History releases ownership. History keeps its own controls and layout.
- Verify recording entitlement before playback, including replays and protected recovery results.

## G. Audio and filters

- Honor talkgroup Disable and Mute for automatic playback while retaining cards for explicit review.
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

## J. Backlog and display limits

The defaults warn at 80 eligible ready waiting calls and arm automatic Jump at 100. A manual traversal keeps the warning visible and suppresses automatic Jump while active. Explicit Jump and Stop can still retire the traversal. The shared 500-card display limit leaves durable playback records available.
