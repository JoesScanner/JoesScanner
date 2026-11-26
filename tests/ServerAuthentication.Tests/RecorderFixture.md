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
