# WordPress Favorites integration

From the repository root, run this suite on a workstation with a local Linux-container Docker Engine, Docker Compose v2, Python 3.10+, and the .NET SDK in `global.json`.

```powershell
python tests/WordPressFavorites.Integration/run.py --check
python tests/WordPressFavorites.Integration/run.py
```

The runner creates a unique Compose project and mounts the repository plugin read-only. WordPress, the Apache authorization bridge, MariaDB, and a Caddy HTTPS proxy are real services. WordPress and MariaDB use an internal network. The proxy exposes only loopback ports 18443 for HTTPS and 18080 for the plaintext rejection test. Different ports can be selected with `--https-port` and `--http-port`. The Python and .NET clients trust the temporary CA for these requests. The runner cleans up its project and volumes on exit.

The schema test activates the plugin, removes the Favorites table from an otherwise populated test schema stamped `1.5.8`, and requests WordPress to trigger the `1.5.9` upgrade. It also exercises repair when `1.5.9` is already recorded. The fixture represents the old schema state using current source.

The authenticated tests use synthetic free WordPress accounts. They cover username and email, passwords containing quotes and backslashes, user isolation, 207 Favorites across multiple pages, concurrent revisions, retrying the same mutation, tombstones, invalid and oversized requests, lockout after 20 failed attempts, and storage and diagnostic log redaction. The legacy tests exercise WordPress login, the admin Settings page, retained SureCart options, and the legacy endpoint with SureCart absent or disabled. Active subscription and billing verification require a separate SureCart development tenant.

The client test runs `FavoritesService` and `FavoritesRepository` against that HTTPS endpoint using two temporary SQLite databases. It covers groups, changes on both devices, conflicts and resolution, stale deletes, account switching, Local Only, resuming sync, and an independent loopback recorder. The regular `ServerAuthentication.Tests` command uses mocked endpoint responses for its Favorites cases.

## Windows app smoke test

On a disposable Windows development profile configured to trust the fixture CA, run the suite with `--hold` to keep WordPress available. It prints the localhost URL and the temporary fixture path. With the test app stopped, set `auth_server_base_url` in the disposable app's `joesscanner.db` `app_settings` table to that HTTPS URL. Configure any recorder used for this check as a synthetic recorder.

1. Launch the Debug app in Normal Mode. Open Settings, then Favorites, and sign in with the synthetic Alpha account. Confirm the groups from the automated run appear.
2. Add a named group. Run the client integration command below, then confirm its group appears in the Windows account after the next sync.
3. Turn on Local Only Mode in the test app and add another group. Verify the device-only notice. Return to Normal Mode and verify the group synchronizes.

```powershell
dotnet run --project tests/ServerAuthentication.Tests/ServerAuthentication.Tests.csproj -- --wordpress-favorites "<temporary fixture path>"
```

Close the test app before ending the held run. The fixture uses its own TLS CA and keeps the machine trust store outside the automated procedure.
