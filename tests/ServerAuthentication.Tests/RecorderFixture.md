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

## Connection interruption and HTTP recovery

Use one fixture process per recorder. Type `link online` to enable recovery testing on that process. In this mode calljson serves the recorder's UTC date range inclusively, reports its UTC offset and filtered count, and logs the requested offset, length, draw, and applied fault.

| Command | Effect |
| --- | --- |
| `link http` | Return HTTP 503 on the listening endpoint until restored. |
| `link refuse` | Close the listening socket and release pending page holds. The console and recorder history stay active. |
| `link online` | Resume HTTP on the same loopback port with the same identities and history. |
| `page fail N` | Return 503 for nonempty-length call-list requests at offset N, including retries. |
| `page hold N` | Hold matching responses with their request-time snapshot. Other requests continue. |
| `page clear` | Clear the page rule and release held responses. Replacing a rule also releases previous holds. |
| `batch N` | Admit 1–500 consecutive numbered calls with missing audio at one recorder second, newest ID first on the wire. Use `attach N` by recording number when audio becomes available. |

On macOS, a request already held when the listener closes may return HTTP 200 with an empty body. To verify hard refusal, wait for the fixture to acknowledge `link refuse`, then open a fresh TCP connection to the same loopback address and port and check for `ECONNREFUSED`.

Continue using send, hold, revise, and attach while disconnected. Restore each recorder independently. A multi-page test can use a batch larger than the client's page size and fail the second offset. Keep the fault until the client requests that offset, then clear it and compare the retried IDs, timestamps, and durable dispositions. Fixture responses prove transmission only. Use application and native playback evidence to verify playback.

## Secure polling and WebSocket delivery

Run the existing numbered recorder and a secure listener in separate terminals, one pair per recorder. For example, start the north recorder on 19774. Its secure listener reads the same recorder's History, lookups, audio, and live rows.

```sh
dotnet run --project tests/ServerAuthentication.Tests -- --recorder-fixture 19774 north
dotnet run --project tests/SecureRecorderFixture -- 19784 19774 /path/to/leaf.pem /path/to/leaf.key
```

Use a disposable server-authentication certificate with an IP subject alternative name of 127.0.0.1. Keep its PEM private key outside the checkout. Add only the issuing root to a newly created disposable simulator with `xcrun simctl keychain <disposable-device-UUID> add-root-cert /path/to/root.pem`. In Local Only Mode, configure the app with `https://127.0.0.1:19784`. The listener binds IPv4 loopback, upgrades at `/Calls`, and obtains recorder data from the specified IPv4 loopback origin. Use distinct port pairs and stdin controls for a second recorder.

On Windows, pass the same leaf and key PEM files. The fixture loads a temporary current-user key for Schannel and releases it on normal shutdown. For an independent HTTPS check with Schannel curl, supply the disposable issuing CA to that invocation:

```powershell
curl.exe --noproxy "*" --cacert C:/path/to/root.pem https://127.0.0.1:19784/sitesjson
```

Serve the leaf's revocation information on loopback when the client requires it. For a WSS check, use the same trust and hostname policy for `wss://127.0.0.1:19784/Calls`. After `offline`, verify that a new upgrade returns 503 while an HTTPS poll still returns newly released rows. After `online`, connect again and compare a delivered row with that HTTPS poll.

On Windows, shut down the secure fixture by typing `quit` or closing stdin to send EOF. Wait for the process to exit. Forced termination can leave a usable disposable private key in the current user's key provider.

Keep the disposable leaf's public certificate until you verify cleanup. For any suspected residual key container, compare its public key with the public key in that certificate. Delete only a positively identified test container through its owning key provider, then verify its removal before discarding the certificate. Leave unrelated containers and any container whose identity is uncertain untouched.

Release or update numbered calls on the recorder console as before. Type one of the following on the secure console after its `upgrade` event.

| Command | Result |
| --- | --- |
| `row paired-P001` | Send one current recorder row. |
| `array paired-P001 paired-P002` | Send the rows as a JSON array in the given order. |
| `data paired-P001 paired-P002` | Send the rows in a JSON object's data array. |
| `incomplete` or `malformed` | Send a row missing its identity or an unfinished JSON object. |
| `offline` | Drop the active socket and return 503 to subsequent upgrades. HTTPS recorder requests continue. |
| `online` | Accept upgrades again. Observe the new connection ID before sending. |
| `quit` | Close the secure listener. The recorder console has its own quit command. |

A release enters recorder history immediately. A socket send reads a snapshot of that history through the origin's calljson endpoint. Origin page holds and failures can therefore affect the sender too. Use the secure listener's HTTPS records for application requests and its delivery events for completed socket sends. Confirm receipt and playback in the application. After testing, shut down and delete only the dedicated disposable simulator, then remove its disposable certificate and key files.
