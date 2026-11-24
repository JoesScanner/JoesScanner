namespace JoesScanner.Services;

internal enum ServerConnectionStatus { Stopped, Disabled, Connecting, Monitoring, Reconnecting, Unavailable, SetupRequired }

internal sealed class ServerConnectionManager(TimeProvider clock,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Connection> _connections = [];
    private long _generation = Random.Shared.NextInt64(1, long.MaxValue / 2);

    internal event Action<Connection>? StatusChanged;
    internal event Action<Connection>? Retired;

    internal sealed class Connection
    {
        private readonly CancellationTokenSource _lifetime;

        internal Connection(SavedScannerConnection profile, long generation, CancellationToken session)
        {
            Profile = profile;
            Generation = generation;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(session);
            Token = _lifetime.Token;
        }
        private int _status = (int)ServerConnectionStatus.Connecting;
        private long? _outageStarted;

        internal SavedScannerConnection Profile { get; }
        internal long Generation { get; }
        internal CancellationToken Token { get; }
        internal Task Worker { get; set; } = Task.CompletedTask;
        internal ServerConnectionStatus Status => (ServerConnectionStatus)Volatile.Read(ref _status);

        internal async Task RetireAsync()
        {
            try
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
                await Worker.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Add(() => $"Recorder connection cleanup failed ({ex.GetType().Name}).");
            }
            finally { _lifetime.Dispose(); }
        }

        internal bool SetStatus(ServerConnectionStatus status, TimeProvider clock)
        {
            if (status == ServerConnectionStatus.Monitoring)
                _outageStarted = null;
            if (status == ServerConnectionStatus.Reconnecting)
            {
                _outageStarted ??= clock.GetTimestamp();
                if (clock.GetElapsedTime(_outageStarted.Value) >= TimeSpan.FromMinutes(2))
                    status = ServerConnectionStatus.Unavailable;
            }
            return Interlocked.Exchange(ref _status, (int)status) != (int)status;
        }
    }

    internal Connection[] Snapshot()
    {
        lock (_gate)
            return _connections.Values.ToArray();
    }

    internal bool IsCurrent(Connection connection)
    {
        lock (_gate)
            return IsCurrentLocked(connection);
    }

    private bool IsCurrentLocked(Connection connection) =>
        !connection.Token.IsCancellationRequested &&
        ReferenceEquals(_connections.GetValueOrDefault(connection.Profile.Id), connection);

    internal void Synchronize(IReadOnlyList<SavedScannerConnection> profiles, CancellationToken session,
        Func<Connection, Task> monitor, Guid? restart = null)
    {
        var enabled = profiles.Where(profile => profile.Enabled).ToDictionary(profile => profile.Id);
        List<Connection> outgoing = [];
        lock (_gate)
        {
            foreach (var (id, connection) in _connections.ToArray())
            {
                if (!enabled.TryGetValue(id, out var profile) || profile.Endpoint != connection.Profile.Endpoint ||
                    profile.DirectoryId != connection.Profile.DirectoryId || restart == id)
                {
                    _connections.Remove(id);
                    outgoing.Add(connection);
                }
            }

            foreach (var profile in enabled.Values)
            {
                if (_connections.ContainsKey(profile.Id))
                    continue;
                var connection = new Connection(profile, ++_generation, session);
                _connections.Add(profile.Id, connection);
                connection.Worker = Task.Run(() => RunAsync(connection, monitor));
            }
        }

        foreach (var connection in outgoing)
        {
            _ = connection.RetireAsync();
            Retired?.Invoke(connection);
        }
    }

    internal void Retire(Func<SavedScannerConnection, bool> retire)
    {
        List<Connection> outgoing = [];
        lock (_gate)
        {
            foreach (var (id, connection) in _connections.ToArray())
            {
                if (!retire(connection.Profile))
                    continue;
                _connections.Remove(id);
                outgoing.Add(connection);
            }
        }
        foreach (var connection in outgoing)
        {
            _ = connection.RetireAsync();
            Retired?.Invoke(connection);
        }
    }

    internal void Report(Connection connection, ServerConnectionStatus status)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(connection) || !connection.SetStatus(status, clock))
                return;
        }
        StatusChanged?.Invoke(connection);
    }

    private async Task RunAsync(Connection connection, Func<Connection, Task> monitor)
    {
        var token = connection.Token;
        var wait = TimeSpan.FromSeconds(2);
        try
        {
            while (IsCurrent(connection))
            {
                try
                {
                    await monitor(connection).ConfigureAwait(false);
                    if (connection.Status == ServerConnectionStatus.SetupRequired)
                        break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    AppLog.Add(() => $"Recorder connection failed ({ex.GetType().Name}).");
                }
                catch (Exception) when (token.IsCancellationRequested) { break; }

                if (!IsCurrent(connection))
                    break;
                if (connection.Status == ServerConnectionStatus.Monitoring)
                    wait = TimeSpan.FromSeconds(2);
                Report(connection, ServerConnectionStatus.Reconnecting);
                if (delay != null)
                    await delay(wait, token).ConfigureAwait(false);
                else
                    await Task.Delay(wait, clock, token).ConfigureAwait(false);
                wait = TimeSpan.FromSeconds(Math.Min(30, wait.TotalSeconds * 2));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }

    }
}
