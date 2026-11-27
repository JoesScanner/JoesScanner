# Local recorder playback fixture

Run the test console from the repository root with a free TCP port. Leave the terminal open. The JSON lines on stdout include UTC timestamps. To keep a log on macOS, use a new log filename each time.

```sh
dotnet run --project tests/ServerAuthentication.Tests -- --recorder-fixture 19873 | tee recorder-session.jsonl
```

The ready event gives the IPv4 loopback URL and a new run ID. The fixture starts empty. From an iOS simulator on the same Mac, enable Local Only Mode, leave other recorders disabled, and save the URL as a custom server with blank credentials. Start Live monitoring and wait for a successful empty poll before releasing calls.

Type each command in the fixture terminal and press Return.

| Command | Result |
| --- | --- |
| a | Publish call A with an eight-second 440 Hz WAV. |
| b | Publish call B with a preliminary transcript and no audio URL. |
| update-b | Revise B's transcript and attach its eight-second 880 Hz WAV. Its ID and recording time stay the same. |
| quit | Stop serving and exit. Closing stdin also stops the fixture. |

The first three commands are accepted once each, in order. Between commands the state stays fixed. Restart the fixture for a fresh scenario. Stop monitoring and remove the temporary custom server after testing.

The recorder accepts POST /calljson with the client's DataTables draw, start and length fields as JSON or URL-encoded form data. Responses include the draw, counts and newest-first rows. A released audio URL accepts GET. Requests carrying an Authorization header are refused. There is no outbound client, account setup or remote control endpoint.

Use the request, response, poll, release and audio_download records to correlate activity. A download records bytes written by the server. Verify native playback and completion on the client, and capture output separately when acoustic evidence is required.

Running the test console without the fixture arguments runs the usual regression suite.

## Queue scenarios

Start a new fixture process for each independent scenario. Use either the Phase 1 controls above or the numbered queue controls below in a run. All controls are local stdin commands.

| Command | Effect |
| --- | --- |
| `send 4500` | Publish one 4.5-second call with audio. |
| `burst 1800 2300 2700` | Publish the three durations together in one atomic snapshot. |
| `hold 5000` | Publish one call with no audio URL. Leave it missing or attach its audio later. |
| `attach 4` | Attach the previously specified audio to numbered call 4 and revise its transcript. |
| `revise 2` | Publish a transcript revision for call 2, retaining its identity, timestamp, and audio. |

Durations are integer milliseconds from 250 to 60000. A burst accepts up to 64 durations. Numbers start at 1 in input order. IDs include the run ID. Polls return newest first. Recording timestamps advance by at least one second per number so bursts have explicit display order. A rapid burst can place a synthetic recording timestamp ahead of wall time. The UTC log event records the actual release time. Compare playback with the client's durable admission sequence. WAVs have 8000 mono samples per second and a numbered tone (300 + 23 × (number modulo 127) Hz). Poll metadata rounds duration up to whole seconds.

## Paired recorder scenarios

Launch two independent processes with distinct unused ports, for example `--recorder-fixture 19874 north` and `--recorder-fixture 19875 south`. Use a fresh Local Only app test container with only those two loopback endpoints. Each process takes the numbered queue commands on its own stdin. Both start at `paired-P001` on each launch. Odd calls use dispatch (101) and even calls use tactical (102). Labels identify the recorder, and south tones are 300 Hz higher. GET /receiversjson, /sitesjson and /talkgroupsjson return each recorder's synthetic catalog.

To establish a specific cross-recorder arrival order, release a call on one endpoint, wait for its admission in the client's durable queue, then release on the other. For distinct displayed timestamps, also wait for the next wall-clock second after the previous synthetic capture timestamp. Each process logs actual UTC release and HTTP events independently.

## History scenarios

Start a north or south fixture on loopback. Use `archive seed <unix-seconds>` once to set the newest recording time inside the search window. Both recorders supply 510 stable IDs H001–H510, one second apart, newest first. A site or receiver filter matches all 510. Dispatch (101) and tactical (102) each match 255. Date bounds are inclusive at the start and exclusive at the end. Live poll responses stay separate.

North H001 is 20 seconds and South H001 is three minutes. H002–H004 are 2.4, 3.1 and 4.2 seconds. H226–H250 last 8700 + row number milliseconds, and other recordings last 1300 + row number milliseconds. Control the archive from its own stdin:

| Command | Effect |
| --- | --- |
| `archive duplicates on` / `off` | Insert or remove a second H250 immediately after the first. Counts and offsets include both raw rows. |
| `archive missing 2` / `archive ready 2` | Remove or restore H002's audio URL and WAV. Search again to refresh an existing result. |
| `archive delay 9000` | Delay the next History response by 0–12000 ms. Other requests can finish first. |
| `archive fail` | Return HTTP 503 for the next History request only. |
| `archive empty on` / `off` | Return zero filtered matches or resume normal filtering. |

Change duplicate or empty mode before a new search to keep each paged result stable. Correlate UTC history_request, history_response and history_audio events with the native player trace.
