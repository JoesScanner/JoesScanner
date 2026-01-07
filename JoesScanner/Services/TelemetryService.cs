using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace JoesScanner.Services
{
    public sealed class TelemetryService : ITelemetryService, IDisposable
    {
        private const string QueueFileName = "telemetry-events.json";
        private const string LastSessionTokenKey = "TelemetryLastSessionToken";
        private const string SessionStartUtcKey = "TelemetrySessionStartUtc";

        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

        private readonly ISettingsService _settings;
        private readonly HttpClient _httpClient;

        private readonly SemaphoreSlim _flushLock = new(1, 1);
        private Timer? _heartbeatTimer;

        private int _appStartInitialized;

        public TelemetryService(ISettingsService settings)
        {
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

        public void TrackAppStarted()
        {
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
            StopHeartbeat();

            // Session end is inferred by absence of heartbeat pings.
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAppEventAsync("app_stopping", null, CancellationToken.None).ConfigureAwait(false);
                    await SendAppEventAsync("session_end", BuildSessionEndPayload(_settings.AuthSessionToken), CancellationToken.None).ConfigureAwait(false);
                    await TryFlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }

        public void TrackConnectionAttempt(string streamServerUrl, bool isHostedServer)
        {
            EnsureAppStartSessionInitialized();

            EnqueueEvent(new TelemetryEvent
            {
                CreatedUtc = DateTime.UtcNow,
                EventType = "stream_connect_attempt",
                Data = new Dictionary<string, string>
                {
                    ["stream_server_url"] = streamServerUrl ?? string.Empty,
                    ["stream_server_is_hosted"] = isHostedServer ? "true" : "false"
                }
            });

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
            EnsureAppStartSessionInitialized();

            var data = new Dictionary<string, string>
            {
                ["connection_status"] = status ?? string.Empty,
                ["stream_server_url"] = streamServerUrl ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(detailMessage))
                data["detail"] = detailMessage;

            EnqueueEvent(new TelemetryEvent
            {
                CreatedUtc = DateTime.UtcNow,
                EventType = "connection_status",
                Data = data
            });

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
            EnsureAppStartSessionInitialized();

            // Treat each monitoring run as its own session on the server.
            BeginNewMonitoringSessionToken();

            StartHeartbeat();

            _ = Task.Run(async () =>
            {
                try
                {
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
            StopHeartbeat();

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAppEventAsync("monitoring_stop", new
                    {
                        reason = reason ?? string.Empty
                    }, CancellationToken.None).ConfigureAwait(false);

                    await SendAppEventAsync("session_end", BuildSessionEndPayload(_settings.AuthSessionToken), CancellationToken.None).ConfigureAwait(false);
                    await TryFlushQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            });
        }


        public async Task ResetSessionAsync(string reason, CancellationToken cancellationToken)
        {
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

            Preferences.Set(LastSessionTokenKey, newSessionToken);
            Preferences.Set(SessionStartUtcKey, DateTime.UtcNow.ToString("o"));

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
            await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var queue = LoadQueue();
                if (queue.Count == 0)
                    return;

                var cutoff = DateTime.UtcNow - MaxAge;
                queue = queue.Where(x => x.CreatedUtc >= cutoff).ToList();
                SaveQueue(queue);

                if (queue.Count == 0)
                    return;

                var sentCount = 0;

                foreach (var item in queue)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var ok = await TrySendEventAsync(item, cancellationToken).ConfigureAwait(false);
                    if (!ok)
                        break;

                    sentCount++;
                }

                if (sentCount <= 0)
                    return;

                queue.RemoveRange(0, sentCount);
                SaveQueue(queue);
            }
            finally
            {
                _flushLock.Release();
            }
        }

        private void StartHeartbeat()
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
            }, null, HeartbeatInterval, HeartbeatInterval);
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

            Preferences.Set(LastSessionTokenKey, newToken);
            Preferences.Set(SessionStartUtcKey, DateTime.UtcNow.ToString("o"));
        }


        private void BeginNewMonitoringSessionToken()
        {
            var newToken = Guid.NewGuid().ToString();

            _settings.AuthSessionToken = newToken;

            Preferences.Set(LastSessionTokenKey, newToken);
            Preferences.Set(SessionStartUtcKey, DateTime.UtcNow.ToString("o"));
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
                reason = reason ?? string.Empty
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
                started_utc = Preferences.Get(SessionStartUtcKey, DateTime.UtcNow.ToString("o"))
            };
        }

        private object BuildSessionEndPayload(string sessionToken)
        {
            var started = Preferences.Get(SessionStartUtcKey, string.Empty);
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

        private string GetQueuePath()
        {
            return Path.Combine(FileSystem.AppDataDirectory, QueueFileName);
        }

        private List<TelemetryEvent> LoadQueue()
        {
            var path = GetQueuePath();
            if (!File.Exists(path))
                return new List<TelemetryEvent>();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<TelemetryEvent>>(json) ?? new List<TelemetryEvent>();
            }
            catch
            {
                return new List<TelemetryEvent>();
            }
        }

        private void SaveQueue(List<TelemetryEvent> items)
        {
            try
            {
                var path = GetQueuePath();
                var json = JsonSerializer.Serialize(items);
                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }

        private void EnqueueEvent(TelemetryEvent ev)
        {
            try
            {
                var queue = LoadQueue();

                var cutoff = DateTime.UtcNow - MaxAge;
                queue = queue.Where(x => x.CreatedUtc >= cutoff).ToList();

                queue.Add(ev);
                SaveQueue(queue);
            }
            catch
            {
            }
        }

        private sealed class TelemetryEvent
        {
            public DateTime CreatedUtc { get; set; }
            public string EventType { get; set; } = string.Empty;
            public object? Data { get; set; }
        }
    }
}
