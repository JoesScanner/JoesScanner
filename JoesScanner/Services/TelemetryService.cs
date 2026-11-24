using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using JoesScanner.Helpers;

namespace JoesScanner.Services
{
    public sealed class TelemetryService : ITelemetryService, IDisposable
    {
        private const string TableTelemetryQueue = "telemetry_queue";

        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

        private readonly string _dbPath;
        private readonly ISettingsService _settings;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _flushLock = new(1, 1);
        private readonly Channel<Func<CancellationToken, Task>> _dispatchQueue;
        private readonly CancellationTokenSource _dispatchCts = new();
        private readonly Task _dispatchWorker;
        private readonly object _serverStateLock = new();

        private Timer? _heartbeatTimer;
        private int _appStartInitialized;
        private string _lastStreamServerUrl = string.Empty;
        private bool _lastStreamServerIsHosted;
        private bool _disposed;

        public TelemetryService(ISettingsService settings, IDatabasePathProvider dbPathProvider)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _dbPath = (dbPathProvider ?? throw new ArgumentNullException(nameof(dbPathProvider))).DbPath;

            CleanupLegacyTelemetryQueueFile();

            _httpClient = WordPressSession.CreateClient(settings);
            _httpClient.Timeout = TimeSpan.FromSeconds(3);

            _dispatchQueue = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            _dispatchWorker = Task.Run(ProcessDispatchQueueAsync);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try { _heartbeatTimer?.Dispose(); } catch { }
            try { _dispatchQueue.Writer.TryComplete(); } catch { }
            try { _dispatchCts.Cancel(); } catch { }
            try { _dispatchWorker.GetAwaiter().GetResult(); } catch { }
            try { _dispatchCts.Dispose(); } catch { }
            try { _flushLock.Dispose(); } catch { }
            try { _httpClient.Dispose(); } catch { }
        }

        public void TrackAppStarted()
        {
            if (!ShouldSendTelemetry())
                return;

            if (Interlocked.Exchange(ref _appStartInitialized, 1) == 0)
                BeginNewAppStartSessionToken();

            EnqueueDispatch(async ct =>
            {
                await SendPingAsync(_settings.AuthSessionToken, "app_start", ct).ConfigureAwait(false);
                await SendAppEventAsync("app_started", null, ct).ConfigureAwait(false);
                await TryFlushQueueAsync(ct).ConfigureAwait(false);
            });
        }

        public void TrackAppStopping()
        {
            var session = _settings.ReadAccountSession();
            StopHeartbeat();

            if (!ShouldSendTelemetry())
                return;

            EnqueueDispatch(async ct =>
            {
                await SendAppEventAsync("app_stopping", null, ct).ConfigureAwait(false);
                await SendAppEventAsync("session_end", BuildSessionEndPayload(session.Token), ct, session.Version).ConfigureAwait(false);
                await TryFlushQueueAsync(ct).ConfigureAwait(false);
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
                    ["stream_server_url"] = RecordingStorageReference.ServerKey(streamServerUrl) ?? string.Empty,
                    ["stream_server_is_hosted"] = isHostedServer ? "true" : "false"
                }
            }, CancellationToken.None);

            EnqueueDispatch(async ct =>
            {
                await SendPingAsync(_settings.AuthSessionToken, "stream_connect_attempt", ct).ConfigureAwait(false);
                await TryFlushQueueAsync(ct).ConfigureAwait(false);
            });
        }

        public void TrackConnectionStatusChanged(string status, string? detailMessage, string streamServerUrl)
        {
            if (!ShouldSendTelemetry())
                return;

            EnsureAppStartSessionInitialized();
            UpdateLastStreamServer(streamServerUrl, HostedServerRules.IsHostedStreamServerUrl(streamServerUrl));

            var data = new Dictionary<string, string>
            {
                ["connection_status"] = status ?? string.Empty,
                ["stream_server_url"] = RecordingStorageReference.ServerKey(streamServerUrl) ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(detailMessage))
                data["detail"] = DiagnosticRedaction.Sanitize(detailMessage);

            _ = EnqueueEventAsync(new TelemetryEvent
            {
                CreatedUtc = DateTime.UtcNow,
                EventType = "connection_status",
                Data = data
            }, CancellationToken.None);

            EnqueueDispatch(async ct =>
            {
                await SendPingAsync(_settings.AuthSessionToken, "connection_status", ct).ConfigureAwait(false);
                await TryFlushQueueAsync(ct).ConfigureAwait(false);
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
            UpdateLastStreamServer(streamServerUrl, HostedServerRules.IsHostedStreamServerUrl(streamServerUrl));

            StartHeartbeat(fireImmediately: true);

            EnqueueDispatch(async ct =>
            {
                await SendPingAsync(_settings.AuthSessionToken, "monitoring_start", ct).ConfigureAwait(false);
                await SendAppEventAsync("session_start", BuildSessionStartPayload("monitoring_start"), ct).ConfigureAwait(false);
                await SendAppEventAsync("monitoring_start", new
                {
                    stream_server_url = streamServerUrl ?? string.Empty
                }, ct).ConfigureAwait(false);
                await TryFlushQueueAsync(ct).ConfigureAwait(false);
            });
        }

        public void StopMonitoringHeartbeat(string reason)
        {
            var session = _settings.ReadAccountSession();

            EnqueueDispatch(async ct =>
            {
                await SendPingAsync(session.Token, "monitoring_stop", ct).ConfigureAwait(false);
            });

            StopHeartbeat();

            if (!ShouldSendTelemetry())
                return;

            EnqueueDispatch(async ct =>
            {
                await SendAppEventAsync("monitoring_stop", new
                {
                    reason = reason ?? string.Empty
                }, ct).ConfigureAwait(false);
                await SendAppEventAsync("session_end", BuildSessionEndPayload(session.Token), ct, session.Version).ConfigureAwait(false);
                await TryFlushQueueAsync(ct).ConfigureAwait(false);
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

        public async Task AdoptSessionTokenAsync(string newSessionToken, string reason, CancellationToken cancellationToken,
            long? expectedVersion = null)
        {
            if (expectedVersion.HasValue)
                Interlocked.Exchange(ref _appStartInitialized, 1);
            else
                EnsureAppStartSessionInitialized();

            newSessionToken = (newSessionToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newSessionToken))
                return;

            var (_, oldToken, version, _) = _settings.ReadAccountSession();
            if (expectedVersion.HasValue && expectedVersion.Value != version)
                return;
            if (string.Equals(oldToken, newSessionToken, StringComparison.Ordinal))
                return;

            if (!string.IsNullOrWhiteSpace(oldToken))
            {
                try
                {
                    await SendAppEventAsync("session_end", BuildSessionEndPayload(oldToken), cancellationToken, version).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (!_settings.TryReplaceAccountSession(version, newSessionToken))
                return;
            AppStateStore.SetString("telemetry_session_start_utc", DateTime.UtcNow.ToString("o"));

            if (!ShouldSendTelemetry())
                return;

            try
            {
                await SendPingAsync(newSessionToken, "session_start_" + (reason ?? string.Empty), cancellationToken).ConfigureAwait(false);
                await SendAppEventAsync("session_start", BuildSessionStartPayload(reason), cancellationToken, version + 1).ConfigureAwait(false);
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

                if (sentIds.Count > 0)
                    await DeleteQueueRowsAsync(sentIds, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _flushLock.Release();
            }
        }

        private async Task ProcessDispatchQueueAsync()
        {
            try
            {
                while (await _dispatchQueue.Reader.WaitToReadAsync(_dispatchCts.Token).ConfigureAwait(false))
                {
                    while (_dispatchQueue.Reader.TryRead(out var work))
                    {
                        try
                        {
                            await work(_dispatchCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private void EnqueueDispatch(Func<CancellationToken, Task> work)
        {
            if (_disposed)
                return;

            try
            {
                var cloud = PrivacyNetworkPolicy.For(_settings).CloudToken();
                _dispatchQueue.Writer.TryWrite(async stopping =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, cloud);
                    if (!linked.IsCancellationRequested)
                        await work(linked.Token).ConfigureAwait(false);
                });
            }
            catch
            {
            }
        }

        private void StartHeartbeat(bool fireImmediately)
        {
            _heartbeatTimer ??= new Timer(_ =>
            {
                EnqueueDispatch(async ct =>
                {
                    EnsureAppStartSessionInitialized();
                    await SendPingAsync(_settings.AuthSessionToken, "heartbeat", ct).ConfigureAwait(false);
                    await TryFlushQueueAsync(ct).ConfigureAwait(false);
                });
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
            var (_, token, version, _) = _settings.ReadAccountSession();
            if (token.Length == 0)
                _settings.TryReplaceAccountSession(version, Guid.NewGuid().ToString());
            AppStateStore.SetString("telemetry_session_start_utc", DateTime.UtcNow.ToString("o"));
        }

        private void EnsureAppStartSessionInitialized()
        {
            if (Volatile.Read(ref _appStartInitialized) != 0)
                return;

            if (Interlocked.Exchange(ref _appStartInitialized, 1) == 0)
                BeginNewAppStartSessionToken();
        }

        private void UpdateLastStreamServer(string? streamServerUrl, bool isHosted)
        {
            if (RecordingStorageReference.ServerKey(streamServerUrl) is not { } url)
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

        private bool ShouldSendTelemetry() => !_settings.LocalOnlyMode;

        private async Task<bool> TrySendEventAsync(TelemetryEvent ev, CancellationToken cancellationToken)
        {
            try
            {
                var session = _settings.ReadAccountSession();
                var endpoint = SavedConnectionUrl.AccountEndpoint(session.BaseUrl, "app-event");
                var payload = BuildAppEventPayload(ev.EventType, ev.Data, ev.CreatedUtc, session.Token);
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode == 404 || (int)response.StatusCode == 405)
                    return true;

                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task SendAppEventAsync(string eventType, object? payload, CancellationToken cancellationToken,
            long? requiredSessionVersion = null)
        {
            var session = _settings.ReadAccountSession();
            if (requiredSessionVersion.HasValue && requiredSessionVersion.Value != session.Version)
                return;
            var endpoint = SavedConnectionUrl.AccountEndpoint(session.BaseUrl, "app-event");
            var body = BuildAppEventPayload(eventType, payload, DateTime.UtcNow, session.Token);
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        }

        private async Task SendPingAsync(string? sessionToken, string reason, CancellationToken cancellationToken)
        {
            var token = (sessionToken ?? string.Empty).Trim();
            var session = _settings.ReadAccountSession();
            if (string.IsNullOrWhiteSpace(token) || token != session.Token)
                return;

            var endpoint = SavedConnectionUrl.AccountEndpoint(session.BaseUrl, "ping");
            var body = BuildPingPayload(token, reason);
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        }

        private object BuildPingPayload(string sessionToken, string reason)
        {
            var (serverUrl, isHosted) = GetLastStreamServer();
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                serverUrl = RecordingStorageReference.ServerKey(_settings.ServerUrl) ?? string.Empty;
                isHosted = HostedServerRules.IsHostedStreamServerUrl(serverUrl);
                if (!string.IsNullOrWhiteSpace(serverUrl))
                    UpdateLastStreamServer(serverUrl, isHosted);
            }

            var appVersion = AppInfo.Current.VersionString ?? string.Empty;
            var appBuild = AppInfo.Current.BuildString ?? string.Empty;
            var app = string.IsNullOrWhiteSpace(appBuild) ? appVersion : appVersion + "+" + appBuild;

            return new
            {
                session_token = sessionToken,
                device_id = _settings.DeviceInstallId,
                device_platform = DeviceInfo.Platform.ToString(),
                device_type = DeviceInfo.Idiom.ToString(),
                device_model = DeviceInfoHelper.CombineManufacturerAndModel(DeviceInfo.Manufacturer, DeviceInfo.Model),
                app_version = app,
                reason = reason ?? string.Empty,
                stream_server_url = serverUrl,
                stream_server_is_hosted = isHosted
            };
        }

        private object BuildAppEventPayload(string eventType, object? payload, DateTime createdUtc, string sessionToken)
        {
            return new
            {
                device_id = _settings.DeviceInstallId,
                session_token = sessionToken,
                device_platform = DeviceInfo.Platform.ToString(),
                device_type = DeviceInfo.Idiom.ToString(),
                device_model = DeviceInfoHelper.CombineManufacturerAndModel(DeviceInfo.Manufacturer, DeviceInfo.Model),
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

        private async Task EnqueueEventAsync(TelemetryEvent ev, CancellationToken cancellationToken)
        {
            if (!ShouldSendTelemetry())
                return;

            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                await DbBootstrapper.EnsureInitializedAsync(conn, cancellationToken).ConfigureAwait(false);

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
                        {
                            if (ev.Data is JsonElement { ValueKind: JsonValueKind.Object } fields)
                            {
                                // Legacy events can be sent before the startup maintenance sweep reaches them.
                                var cleaned = fields.EnumerateObject().ToDictionary(field => field.Name,
                                    field => (object?)field.Value);
                                if (fields.TryGetProperty("stream_server_url", out var server) && server.ValueKind == JsonValueKind.String)
                                    cleaned["stream_server_url"] = RecordingStorageReference.ServerKey(server.GetString()) ?? string.Empty;
                                if (fields.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                                    cleaned["detail"] = DiagnosticRedaction.Sanitize(detail.GetString());
                                ev.Data = cleaned;
                            }
                            list.Add(new TelemetryQueueRow(id, ev));
                        }
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
                var parameter = cmd.CreateParameter();
                parameter.ParameterName = "$id";
                cmd.Parameters.Add(parameter);

                foreach (var id in ids)
                {
                    parameter.Value = id;
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                tx.Commit();
            }
            catch
            {
            }
        }

        private static void CleanupLegacyTelemetryQueueFile()
        {
            try
            {
                var legacyPath = Path.Combine(AppPaths.GetAppDataDirectorySafe(), "telemetry-events.json");
                if (File.Exists(legacyPath))
                    File.Delete(legacyPath);
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
