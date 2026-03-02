using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services
{
    public sealed class TelemetryService : ITelemetryService, IDisposable
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

        // Default servers are provided by the app and always have telemetry on.
        // This list can be extended as additional built-in servers are added.
        private static readonly string[] ProvidedDefaultServerUrls =
        {
            "https://app.joesscanner.com"
        };

        private readonly ISettingsService _settings;
        private readonly HttpClient _httpClient;

        private readonly SemaphoreSlim _flushLock = new(1, 1);
        private Timer? _heartbeatTimer;

        private int _appStartInitialized;

        private readonly object _serverStateLock = new();
        private string _lastStreamServerUrl = string.Empty;
        private bool _lastStreamServerIsHosted;

        public TelemetryService(ISettingsService settings, IDatabasePathProvider dbPathProvider)
        {
            _dbPath = (dbPathProvider ?? throw new ArgumentNullException(nameof(dbPathProvider))).DbPath;
            CleanupLegacyTelemetryQueueFile();

            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
        }

        public void Dispose()
        {
            try { _heartbeatTimer?.Dispose(); } catch { }
            try { _flushLock.Dispose(); } catch { }
            try { _httpClient.Dispose(); } catch { }
        }

        private void UpdateLastStreamServer(string? streamServerUrl, bool isHosted)
        {
            var url = (streamServerUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
                return;

            lock (_serverStateLock)
            {
                _lastStreamServerUrl = url;
                _lastStreamServerIsHosted = isHosted;
            }
        }

        private (string Url, bool IsHosted) GetLastStreamServer()
        {
            lock (_serverStateLock)
            {
                return (_lastStreamServerUrl, _lastStreamServerIsHosted);
            }
        }

        private static bool IsHostedStreamServerUrl(string? streamServerUrl)
        {
            var url = (streamServerUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            return string.Equals(uri.Host, "app.joesscanner.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProvidedDefaultServerUrl(string? streamServerUrl)
        {
            var url = (streamServerUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            foreach (var candidate in ProvidedDefaultServerUrls)
            {
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri))
                    continue;

                if (string.Equals(uri.Host, candidateUri.Host, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool ShouldSendTelemetry()
        {
            var selectedServer = _settings.ServerUrl;
            if (IsProvidedDefaultServerUrl(selectedServer))
                return true;

            return _settings.TelemetryEnabled;
        }

        public void TrackAppStarted()
        {
            if (!ShouldSendTelemetry())
                return;

            // Make the session token change synchronous so no other telemetry path
            // can send a ping with an old token during startup.
            if (Interlocked.Exchange(ref _appStartInitialized, 1) == 0)
            {
                BeginNewAppStartSessionToken();
            }

            // Do not start the heartbeat on app start. Heartbeats must be driven by
            // monitoring state so background playback does not create multi-hour sessions.
            _ = Task.Run(async () =>
            {
                try
                {
                    // Register the app-start session token with the server early so other features
                    // (like Communications preload) can use the token before monitoring begins.
                    await SendPingAsync(_settings.AuthSessionToken, "app_start", CancellationToken.None).ConfigureAwait(false);

                    await SendAppEventAsync("app_started", null, CancellationToken.None).ConfigureAwait(false);
                    await TryFlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }

        public void TrackAppStopping()
        {
            var token = _settings.AuthSessionToken;
            StopHeartbeat();

            if (!ShouldSendTelemetry())
                return;

            // Session end is inferred by absence of heartbeat pings.
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAppEventAsync("app_stopping", null, CancellationToken.None).ConfigureAwait(false);
                    await SendAppEventAsync("session_end", BuildSessionEndPayload(token), CancellationToken.None).ConfigureAwait(false);
                    await TryFlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }

        public void TrackConnectionAttempt(string streamServerUrl, bool isHostedServer)
        {
            if (!ShouldSendTelemetry())
                return;

            EnsureAppStartSessionInitialized();

            UpdateLastStreamServer(streamServerUrl, isHostedServer);

            _ = EnqueueEventAsync(new TelemetryEvent
            {
                CreatedUtc = DateTime.UtcNow,
                EventType = "stream_connect_attempt",
                Data = new Dictionary<string, string>
                {
                    ["stream_server_url"] = streamServerUrl ?? string.Empty,
                    ["stream_server_is_hosted"] = isHostedServer ? "true" : "false"
                }
            }, CancellationToken.None);

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendPingAsync(_settings.AuthSessionToken, "stream_connect_attempt", CancellationToken.None).ConfigureAwait(false);
                    await TryFlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }

        public void TrackConnectionStatusChanged(string status, string? detailMessage, string streamServerUrl)
        {
            if (!ShouldSendTelemetry())
                return;

            EnsureAppStartSessionInitialized();

            UpdateLastStreamServer(streamServerUrl, IsHostedStreamServerUrl(streamServerUrl));

            var data = new Dictionary<string, string>
            {
                ["connection_status"] = status ?? string.Empty,
                ["stream_server_url"] = streamServerUrl ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(detailMessage))
                data["detail"] = detailMessage;

            _ = EnqueueEventAsync(new TelemetryEvent
            {
                CreatedUtc = DateTime.UtcNow,
                EventType = "connection_status",
                Data = data
            }, CancellationToken.None);

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendPingAsync(_settings.AuthSessionToken, "connection_status", CancellationToken.None).ConfigureAwait(false);
                    await TryFlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }


        public void StartMonitoringHeartbeat(string streamServerUrl)
        {
            if (!ShouldSendTelemetry())
            {
                StopHeartbeat();
                return;
            }

            EnsureAppStartSessionInitialized();

            UpdateLastStreamServer(streamServerUrl, IsHostedStreamServerUrl(streamServerUrl));

            // Use the app launch session token for monitoring so opening the app and connecting creates a single session row.

            // Immediately send at least one ping so short runs do not
            // create 0 second or 1 second sessions on the server.
            StartHeartbeat(fireImmediately: true);

            _ = Task.Run(async () =>
            {
                try
                {
                    // Record the start promptly (separate from the heartbeat cadence).
                    await SendPingAsync(_settings.AuthSessionToken, "monitoring_start", CancellationToken.None).ConfigureAwait(false);
                    await SendAppEventAsync("session_start", BuildSessionStartPayload("monitoring_start"), CancellationToken.None).ConfigureAwait(false);
                    await SendAppEventAsync("monitoring_start", new
                    {
                        stream_server_url = streamServerUrl ?? string.Empty
                    }, CancellationToken.None).ConfigureAwait(false);

                    await TryFlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }

        public void StopMonitoringHeartbeat(string reason)
        {
            // Capture the token now so the stop ping always lands on the session that just ended.
            var token = _settings.AuthSessionToken;

            // Send a final ping so the server updates last_seen_utc even if the monitoring run is
            // shorter than the heartbeat interval.
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendPingAsync(token, "monitoring_stop", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });

            StopHeartbeat();

            if (!ShouldSendTelemetry())
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAppEventAsync("monitoring_stop", new
                    {
                        reason = reason ?? string.Empty
                    }, CancellationToken.None).ConfigureAwait(false);

                    await SendAppEventAsync("session_end", BuildSessionEndPayload(token), CancellationToken.None).ConfigureAwait(false);
                    await TryFlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }


        public async Task ResetSessionAsync(string reason, CancellationToken cancellationToken)
        {
            if (!ShouldSendTelemetry())
                return;

            EnsureAppStartSessionInitialized();

            var newToken = Guid.NewGuid().ToString();
            await AdoptSessionTokenAsync(newToken, reason ?? "reset", cancellationToken).ConfigureAwait(false);
        }

        public async Task AdoptSessionTokenAsync(string newSessionToken, string reason, CancellationToken cancellationToken)
        {
            EnsureAppStartSessionInitialized();

            newSessionToken = (newSessionToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newSessionToken))
                return;

            var oldToken = (_settings.AuthSessionToken ?? string.Empty).Trim();
            if (string.Equals(oldToken, newSessionToken, StringComparison.Ordinal))
                return;

            if (!string.IsNullOrWhiteSpace(oldToken))
            {
                // Do not ping the old token. Lack of pings is treated as session end.
                try
                {
                    await SendAppEventAsync("session_end", BuildSessionEndPayload(oldToken), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _settings.AuthSessionToken = newSessionToken;

            AppStateStore.SetString("telemetry_last_session_token", newSessionToken);
            AppStateStore.SetString("telemetry_session_start_utc", DateTime.UtcNow.ToString("o"));

            if (!ShouldSendTelemetry())
                return;

            try
            {
                await SendPingAsync(newSessionToken, "session_start_" + (reason ?? string.Empty), cancellationToken).ConfigureAwait(false);
                await SendAppEventAsync("session_start", BuildSessionStartPayload(reason), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        public async Task TryFlushQueueAsync(CancellationToken cancellationToken)
        {
            if (!ShouldSendTelemetry())
            {
                await ClearQueueAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await TrimQueueAsync(cancellationToken).ConfigureAwait(false);

                var queue = await LoadQueueAsync(cancellationToken).ConfigureAwait(false);
                if (queue.Count == 0)
                    return;


                var sentIds = new List<long>();

                foreach (var row in queue)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var ok = await TrySendEventAsync(row.Event, cancellationToken).ConfigureAwait(false);
                    if (!ok)
                        break;

                    sentIds.Add(row.Id);
                }

                if (sentIds.Count <= 0)
                    return;

                await DeleteQueueRowsAsync(sentIds, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _flushLock.Release();
            }
        }

        private void StartHeartbeat(bool fireImmediately)
        {
            _heartbeatTimer ??= new Timer(async _ =>
            {
                try
                {
                    EnsureAppStartSessionInitialized();
                    await SendPingAsync(_settings.AuthSessionToken, "heartbeat", CancellationToken.None).ConfigureAwait(false);
                    await TryFlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }, null, fireImmediately ? TimeSpan.Zero : HeartbeatInterval, HeartbeatInterval);
        }

        private void StopHeartbeat()
        {
            var timer = Interlocked.Exchange(ref _heartbeatTimer, null);
            if (timer == null)
                return;

            try
            {
                timer.Dispose();
            }
            catch
            {
            }
        }

        private void BeginNewAppStartSessionToken()
        {
            var newToken = Guid.NewGuid().ToString();

            _settings.AuthSessionToken = newToken;

            AppStateStore.SetString("telemetry_last_session_token", newToken);
            AppStateStore.SetString("telemetry_session_start_utc", DateTime.UtcNow.ToString("o"));
        }


        private void BeginNewMonitoringSessionToken()
        {
            var newToken = Guid.NewGuid().ToString();

            _settings.AuthSessionToken = newToken;

            AppStateStore.SetString("telemetry_last_session_token", newToken);
            AppStateStore.SetString("telemetry_session_start_utc", DateTime.UtcNow.ToString("o"));
        }


        private void EnsureAppStartSessionInitialized()
        {
            if (Volatile.Read(ref _appStartInitialized) != 0)
                return;

            if (Interlocked.Exchange(ref _appStartInitialized, 1) == 0)
            {
                BeginNewAppStartSessionToken();
            }
        }

        private async Task<bool> TrySendEventAsync(TelemetryEvent ev, CancellationToken cancellationToken)
        {
            try
            {
                var endpoint = BuildAuthServerUri("/wp-json/joes-scanner/v1/app-event");

                var payload = BuildAppEventPayload(ev.EventType, ev.Data, ev.CreatedUtc);
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode == 404 || (int)response.StatusCode == 405)
                    return true;

                if (!response.IsSuccessStatusCode)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task SendAppEventAsync(string eventType, object? payload, CancellationToken cancellationToken)
        {
            var endpoint = BuildAuthServerUri("/wp-json/joes-scanner/v1/app-event");

            var body = BuildAppEventPayload(eventType, payload, DateTime.UtcNow);

            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

            // Best effort.
        }

        private async Task SendPingAsync(string? sessionToken, string reason, CancellationToken cancellationToken)
        {
            var token = (sessionToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token))
                return;

            var endpoint = BuildAuthServerUri("/wp-json/joes-scanner/v1/ping");

            var body = BuildPingPayload(token, reason);
            var json = JsonSerializer.Serialize(body);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

            // Best effort.
        }

        private object BuildPingPayload(string sessionToken, string reason)
        {
            var (serverUrl, isHosted) = GetLastStreamServer();
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                serverUrl = (_settings.ServerUrl ?? string.Empty).Trim();
                isHosted = IsHostedStreamServerUrl(serverUrl);
                if (!string.IsNullOrWhiteSpace(serverUrl))
                {
                    UpdateLastStreamServer(serverUrl, isHosted);
                }
            }

            var appVersion = AppInfo.Current.VersionString ?? string.Empty;
            var appBuild = AppInfo.Current.BuildString ?? string.Empty;
            var app = string.IsNullOrWhiteSpace(appBuild) ? appVersion : (appVersion + "+" + appBuild);

            return new
            {
                session_token = sessionToken,
                device_id = _settings.DeviceInstallId,

                device_platform = DeviceInfo.Platform.ToString(),
                device_type = DeviceInfo.Idiom.ToString(),
                device_model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model),

                app_version = app,
                reason = reason ?? string.Empty,
                stream_server_url = serverUrl,
                stream_server_is_hosted = isHosted
            };
        }

        private object BuildAppEventPayload(string eventType, object? payload, DateTime createdUtc)
        {
            return new
            {
                device_id = _settings.DeviceInstallId,
                session_token = _settings.AuthSessionToken,

                device_platform = DeviceInfo.Platform.ToString(),
                device_type = DeviceInfo.Idiom.ToString(),
                device_model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model),

                app_version = AppInfo.Current.VersionString ?? string.Empty,
                app_build = AppInfo.Current.BuildString ?? string.Empty,

                event_type = eventType ?? string.Empty,
                event_time_utc = createdUtc.ToString("o"),

                payload = payload
            };
        }

        private object BuildSessionStartPayload(string? reason)
        {
            return new
            {
                reason = reason ?? string.Empty,
                started_utc = AppStateStore.GetString("telemetry_session_start_utc", DateTime.UtcNow.ToString("o"))
            };
        }

        private object BuildSessionEndPayload(string sessionToken)
        {
            var started = AppStateStore.GetString("telemetry_session_start_utc", string.Empty);
            return new
            {
                session_token = sessionToken,
                started_utc = started
            };
        }

        private Uri BuildAuthServerUri(string path)
        {
            var baseUrl = (_settings.AuthServerBaseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "https://joesscanner.com";

            baseUrl = baseUrl.TrimEnd('/');

            path = (path ?? string.Empty).Trim();
            if (!path.StartsWith("/", StringComparison.Ordinal))
                path = "/" + path;

            return new Uri(baseUrl + path);
        }

        private static string CombineDeviceModel(string manufacturer, string model)
        {
            manufacturer = (manufacturer ?? string.Empty).Trim();
            model = (model ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(manufacturer))
                return model;

            if (string.IsNullOrWhiteSpace(model))
                return manufacturer;

            if (model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
                return model;

            return manufacturer + " " + model;
        }

        
        private const string TableTelemetryQueue = "telemetry_queue";
                private readonly string _dbPath;
private async Task EnqueueEventAsync(TelemetryEvent ev, CancellationToken cancellationToken)
        {
            if (!ShouldSendTelemetry())
                return;

            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                await DbBootstrapper.EnsureInitializedAsync(conn, cancellationToken).ConfigureAwait(false);

                // Best-effort retention trim at insert time.
                var cutoff = DateTime.UtcNow - MaxAge;
                using (var trim = conn.CreateCommand())
                {
                    trim.CommandText = $"DELETE FROM {TableTelemetryQueue} WHERE created_utc < $cutoff;";
                    trim.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
                    await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var json = JsonSerializer.Serialize(ev);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
INSERT INTO {TableTelemetryQueue} (created_utc, event_type, json_payload)
VALUES ($created, $type, $json);";
                cmd.Parameters.AddWithValue("$created", ev.CreatedUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$type", ev.EventType ?? string.Empty);
                cmd.Parameters.AddWithValue("$json", json);

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task ClearQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                await DbBootstrapper.EnsureInitializedAsync(conn, cancellationToken).ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {TableTelemetryQueue};";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        
        private static void CleanupLegacyTelemetryQueueFile()
        {
            try
            {
                var legacyPath = Path.Combine(FileSystem.AppDataDirectory, "telemetry-events.json");
                if (File.Exists(legacyPath))
                    File.Delete(legacyPath);
            }
            catch
            {
            }
        }

private async Task<List<TelemetryQueueRow>> LoadQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                await DbBootstrapper.EnsureInitializedAsync(conn, cancellationToken).ConfigureAwait(false);

                var list = new List<TelemetryQueueRow>();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
SELECT id, json_payload
FROM {TableTelemetryQueue}
ORDER BY created_utc ASC, id ASC;";

                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = reader.GetInt64(0);
                    var json = reader.GetString(1);

                    try
                    {
                        var ev = JsonSerializer.Deserialize<TelemetryEvent>(json);
                        if (ev != null)
                            list.Add(new TelemetryQueueRow(id, ev));
                    }
                    catch
                    {
                    }
                }

                return list;
            }
            catch
            {
                return new List<TelemetryQueueRow>();
            }
        }

        private async Task TrimQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                await DbBootstrapper.EnsureInitializedAsync(conn, cancellationToken).ConfigureAwait(false);

                var cutoff = DateTime.UtcNow - MaxAge;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {TableTelemetryQueue} WHERE created_utc < $cutoff;";
                cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task DeleteQueueRowsAsync(List<long> ids, CancellationToken cancellationToken)
        {
            if (ids == null || ids.Count == 0)
                return;

            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                await DbBootstrapper.EnsureInitializedAsync(conn, cancellationToken).ConfigureAwait(false);

                using var tx = conn.BeginTransaction();

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;

                cmd.CommandText = $"DELETE FROM {TableTelemetryQueue} WHERE id = $id;";
                var p = cmd.CreateParameter();
                p.ParameterName = "$id";
                cmd.Parameters.Add(p);

                foreach (var id in ids)
                {
                    p.Value = id;
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                tx.Commit();
            }
            catch
            {
            }
        }

        private sealed record TelemetryQueueRow(long Id, TelemetryEvent Event);

        private sealed class TelemetryEvent
        {
            public DateTime CreatedUtc { get; set; }
            public string EventType { get; set; } = string.Empty;
            public object? Data { get; set; }
        }
    }
}
