using System.Text;
using System.Text.Json;

namespace JoesScanner.Services
{
    public sealed class UnregisteredSessionReporter : IUnregisteredSessionReporter
    {
        private const string QueueFileName = "unregistered-session-events.json";
        private const string ActiveSessionIdKey = "UnregisteredActiveSessionId";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        private readonly ISettingsService _settings;
        private readonly HttpClient _httpClient;

        public UnregisteredSessionReporter(ISettingsService settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
        }

        public void OnAppStarted()
        {
            if (HasAuthCredentialsConfigured())
                return;

            var nowUtc = DateTime.UtcNow;

            // If we already have an active session id, do not create another start.
            var existingSessionId = Preferences.Get(ActiveSessionIdKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(existingSessionId))
                return;

            var sessionId = Guid.NewGuid().ToString();

            Preferences.Set(ActiveSessionIdKey, sessionId);

            EnqueueEvent(new SessionEvent
            {
                CreatedUtc = nowUtc,
                SessionId = sessionId,
                Kind = "session_start",
                StartedUtc = nowUtc
            });
        }

        public void OnAppStopping()
        {
            if (HasAuthCredentialsConfigured())
                return;

            var nowUtc = DateTime.UtcNow;

            var sessionId = Preferences.Get(ActiveSessionIdKey, string.Empty);
            if (string.IsNullOrWhiteSpace(sessionId))
                return;

            // Clear first so a crash during write does not block future sessions.
            Preferences.Set(ActiveSessionIdKey, string.Empty);

            EnqueueEvent(new SessionEvent
            {
                CreatedUtc = nowUtc,
                SessionId = sessionId,
                Kind = "session_end",
                EndedUtc = nowUtc
            });
        }

        public async Task TryFlushQueueAsync(CancellationToken cancellationToken)
        {
            if (HasAuthCredentialsConfigured())
                return;

            try
            {
                var queue = LoadQueue();
                if (queue.Count == 0)
                    return;

                // Drop old events first.
                var cutoff = DateTime.UtcNow - MaxAge;
                queue = queue.Where(x => x.CreatedUtc >= cutoff).ToList();
                SaveQueue(queue);

                if (queue.Count == 0)
                    return;

                // Attempt to send in order. Stop on first failure.
                var sentCount = 0;
                foreach (var item in queue)
                {
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
            catch
            {
                // Never allow telemetry queue work to crash the app.
            }
        }

        private bool HasAuthCredentialsConfigured()
        {
            return !string.IsNullOrWhiteSpace(_settings.BasicAuthUsername) &&
                   !string.IsNullOrWhiteSpace(_settings.BasicAuthPassword);
        }

        private async Task<bool> TrySendEventAsync(SessionEvent ev, CancellationToken cancellationToken)
        {
            try
            {
                var endpoint = BuildAuthServerUri("/wp-json/joes-scanner/v1/app-event");

                var payload = BuildPayload(ev);
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

                // If endpoint is not found or method not allowed, stop retrying for this session.
                if ((int)response.StatusCode == 404 || (int)response.StatusCode == 405)
                    return false;

                // For other HTTP failures, treat as transient and retry later.
                if (!response.IsSuccessStatusCode)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private object BuildPayload(SessionEvent ev)
        {
            var appVersion = AppInfo.Current.VersionString ?? string.Empty;
            var appBuild = AppInfo.Current.BuildString ?? string.Empty;

            var platform = DeviceInfo.Platform.ToString();
            var type = DeviceInfo.Idiom.ToString();
            var model = CombineDeviceModel(DeviceInfo.Manufacturer, DeviceInfo.Model);
            var osVersion = DeviceInfo.VersionString ?? string.Empty;

            return new
            {
                // No username or password. Server should classify as Unregistered.
                device_id = _settings.DeviceInstallId,
                session_token = _settings.AuthSessionToken,

                device_platform = platform,
                device_type = type,
                device_model = model,
                os_version = osVersion,

                app_version = appVersion,
                app_build = appBuild,

                event_type = ev.Kind,
                event_time_utc = ev.CreatedUtc.ToString("o"),
                session_id = ev.SessionId,
                session_started_utc = ev.StartedUtc?.ToString("o"),
                session_ended_utc = ev.EndedUtc?.ToString("o")
            };
        }

        private Uri BuildAuthServerUri(string path)
        {
            var baseUrl = (_settings.AuthServerBaseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = "https://joesscanner.com";

            baseUrl = baseUrl.TrimEnd('/');

            path = (path ?? string.Empty).Trim();
            if (!path.StartsWith("/"))
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

        private List<SessionEvent> LoadQueue()
        {
            var path = GetQueuePath();
            if (!File.Exists(path))
                return new List<SessionEvent>();

            try
            {
                var json = File.ReadAllText(path);
                var items = JsonSerializer.Deserialize<List<SessionEvent>>(json) ?? new List<SessionEvent>();
                return items;
            }
            catch
            {
                return new List<SessionEvent>();
            }
        }

        private void SaveQueue(List<SessionEvent> items)
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

        private void EnqueueEvent(SessionEvent ev)
        {
            try
            {
                var queue = LoadQueue();

                // Drop old items before adding.
                var cutoff = DateTime.UtcNow - MaxAge;
                queue = queue.Where(x => x.CreatedUtc >= cutoff).ToList();

                queue.Add(ev);
                SaveQueue(queue);
            }
            catch
            {
            }
        }

        private sealed class SessionEvent
        {
            public DateTime CreatedUtc { get; set; }
            public string SessionId { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty;
            public DateTime? StartedUtc { get; set; }
            public DateTime? EndedUtc { get; set; }
        }
    }
}
