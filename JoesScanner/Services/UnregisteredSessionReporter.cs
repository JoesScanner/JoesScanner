using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace JoesScanner.Services
{
    public sealed class UnregisteredSessionReporter : IUnregisteredSessionReporter
    {
                private const string QueueTable = "unregistered_session_queue";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        private readonly ISettingsService _settings;
        private readonly HttpClient _httpClient;
        private readonly string _dbPath;

        public UnregisteredSessionReporter(ISettingsService settings, IDatabasePathProvider dbPathProvider)
        {
            _dbPath = (dbPathProvider ?? throw new ArgumentNullException(nameof(dbPathProvider))).DbPath;

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
            var existingSessionId = AppStateStore.GetString("unregistered_active_session_id", string.Empty);
            if (!string.IsNullOrWhiteSpace(existingSessionId))
                return;

            var sessionId = Guid.NewGuid().ToString();

            AppStateStore.SetString("unregistered_active_session_id", sessionId);

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

            var sessionId = AppStateStore.GetString("unregistered_active_session_id", string.Empty);
            if (string.IsNullOrWhiteSpace(sessionId))
                return;

            // Clear first so a crash during write does not block future sessions.
            AppStateStore.SetString("unregistered_active_session_id", string.Empty);

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
                // Drop old events first.
                PurgeOldRows();

                while (true)
                {
                    var next = TryDequeueNext();
                    if (next == null)
                        return;

                    var (rowId, ev) = next.Value;

                    var ok = await TrySendEventAsync(ev, cancellationToken).ConfigureAwait(false);
                    if (!ok)
                        return;

                    DeleteRow(rowId);
                }
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
                event_time_utc = ev.CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
                session_id = ev.SessionId,
                session_started_utc = ev.StartedUtc?.ToString("o", CultureInfo.InvariantCulture),
                session_ended_utc = ev.EndedUtc?.ToString("o", CultureInfo.InvariantCulture)
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

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            DbBootstrapper.EnsureInitialized(connection);
            return connection;
        }

        private void PurgeOldRows()
        {
            var cutoff = DateTime.UtcNow - MaxAge;

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {QueueTable} WHERE created_utc < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        private (long rowId, SessionEvent ev)? TryDequeueNext()
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, payload_json FROM {QueueTable} ORDER BY id ASC LIMIT 1;";

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            var id = reader.GetInt64(0);
            var json = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

            try
            {
                var ev = JsonSerializer.Deserialize<SessionEvent>(json) ?? new SessionEvent();
                return (id, ev);
            }
            catch
            {
                // If an item is corrupt, drop it and continue.
                DeleteRow(id);
                return null;
            }
        }

        private void DeleteRow(long rowId)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {QueueTable} WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", rowId);
            cmd.ExecuteNonQuery();
        }

        private void EnqueueEvent(SessionEvent ev)
        {
            try
            {
                var json = JsonSerializer.Serialize(ev);

                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
INSERT INTO {QueueTable} (created_utc, payload_json)
VALUES ($c, $p);";
                cmd.Parameters.AddWithValue("$c", (ev.CreatedUtc == default ? DateTime.UtcNow : ev.CreatedUtc).ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$p", json ?? "{}");
                cmd.ExecuteNonQuery();

                // Keep table from growing without bound.
                PurgeOldRows();
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
