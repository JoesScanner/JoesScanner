# JoesScanner App

JoesScanner is a cross-platform application for listening to live radio calls from the Joe’s Scanner service or any compatible Trunking Recorder server. It provides clean UI presentation, reliable audio streaming, and automatic call queue handling. The app is built for continuous, long-term monitoring on Windows, Android, and iOS.

---

## What the App Does

- Streams radio calls in real time  
- Displays upcoming calls in a queue  
- Allows you to jump directly to live activity  
- Provides playback speed control  
- Shows connection status at a glance  
- Connects to Joe’s Scanner or your own server  
- Automatically saves your preferences  
- Includes a built-in log viewer for troubleshooting  

---

## Using With Joe’s Scanner

Subscribers can connect directly to the Joe’s Scanner service.

1. Open the Settings page  
2. Enter the server URL  
3. Provide your username and password  
4. Test and save your connection  

Once authenticated, calls will begin loading and playing automatically.

---

## Using Your Own Trunking Recorder Server

The app can also connect to self-hosted Trunking Recorder (TR) servers.

You will need:

- Your server URL  
- A username  
- A password (if your TR server uses Basic Auth)  

Enter these on the Settings screen and use “Test Connection.” When the connection succeeds, the app will begin displaying your call list and streaming audio.

---

## Supported Platforms

- Windows  
- Android  
- iOS 
- macOS

The application runs on desktops, laptops, tablets, and mobile devices.

---

## Basic Setup

1. Install the app for your platform  
2. Open the Settings page  
3. Enter your server URL  
4. (Optional) Enter username and password  
5. Select “Verify”  
6. Once verified, return to the main screen  

When configured correctly, call activity will appear when you press play.

---

## Features

### Call Queue  
Incoming calls are queued and played in sequence, with the ability to jump ahead to live activity.

### Audio Controls  
- Play the most recent call  
- Jump to live  
- Adjust playback speed for clearing backlogs  

### Connection Status  
A badge indicates whether the app is connected, connecting, or offline.

### Logging  
The log viewer helps diagnose server or connection issues. Logs can be exported for support.

### Local recording storage

The Live playback journal keeps pending recordings and their metadata regardless of age. This includes recordings that are playing, interrupted, failed, or waiting for audio. Completed and intentionally skipped recordings keep a compact identity record with their recorder, order, disposition, and authorization context. The recorder can still return old IDs during recovery, so the journal keeps these identities indefinitely to prevent replay. Journal size grows with distinct recordings and any unresolved backlog.

The separate calls cache keeps up to 500 rows per recorder on write. Background maintenance expires cache rows after 24 hours, generic lookup cache entries after 14 days, and queued telemetry after seven days. Maintenance starts after launch and processes up to 128 rows per table in each batch. It checks again after 30 seconds while cleanup is pending and every six hours otherwise. SQLite can reuse freed space for future writes.

The recording stores retain ordinary audio paths. Signed or credential-bearing locations stay in memory, and playback can request a fresh location by recording ID after restart. Startup maintenance removes legacy credential-bearing references in resumable batches. Old backups, exported logs, and filesystem copies can still contain earlier data.

---

### Saved connection credentials

JoesScanner stores saved custom-server credentials, the active WordPress login, and the what3words API key in platform-protected storage. Windows uses the current user's protected storage, Android uses Keystore-backed encrypted preferences, and iOS uses the device's Keychain. The WordPress session token and API-issued firewall credentials are kept in memory for the current session.

On upgrade, the app verifies protected writes before cleaning up legacy SQLite settings. If protected storage or the database is unavailable, the affected rows are kept so migration can retry. SQLite manages its own journal and WAL files throughout this process.

### Backups and diagnostic exports

- Android cloud backup and device transfer exclude the encrypted credential preferences, the local SQLite database and its WAL, SHM and rollback journal, the associated reconnect permit, historical logs, and the old telemetry queue. The encrypted preferences require the original device's Keystore. Other eligible app files and preferences can still be backed up. An in-place upgrade keeps local data. A restore or transfer using the new rules requires reconfiguring database-backed settings and saved connections. Local call history, pending playback, and filters in the database stay with the original installation.
- On iOS, device-local Keychain credentials remain accessible while locked after the first unlock following a restart. They do not transfer to a different device. The database and logs remain in the existing app data location and can be included in app data backups. A future exclusion can use a dedicated directory marked excluded before SQLite opens it. A move would need a verified SQLite-aware upgrade to preserve the database and keep its sidecars together.
- Windows keeps the existing MSIX identity and database location for in-place upgrades. Credentials are protected for the current Windows user. Disk or profile backups can still contain the database and historical logs. For a future managed backup exclusion, packaged Windows provides a non-backed-up local cache folder. Relocating existing data there would require a tested upgrade path.
- New diagnostic ZIPs sanitize the snapshot and copied text logs. They leave the source logs intact. Existing backups, previously exported ZIPs, and earlier copies of SQLite sidecars can still contain secrets. Protect or remove those copies and rotate credentials if they were shared.

## Troubleshooting

If calls are not displaying or audio does not play:

- Confirm the server URL is correct  
- Verify username and password  
- Use the “Test Connection” button  
- Check the log viewer for errors  
- Ensure your network can reach the server  
- For TR servers, confirm that the Call History API is enabled and reachable  

---

## Downloads

The latest builds for windows are available under GitHub Releases:  
https://github.com/JoesScanner/JoesScanner/releases
Or on the Windows, Android, and iOS stores.
Search "Joes Scanner"

---

## License

The JoesScanner app is provided under a dual license:

- Free for personal, non-commercial use  
- Commercial use requires a paid license  

See the LICENSE file for full terms.

---

## Support

Joe’s Scanner subscribers:  
https://www.joesscanner.com

Commercial licensing and technical support:  
support@joesscanner.com
