using JoesScanner.Helpers;
using JoesScanner.Models;

namespace JoesScanner.Services;

public sealed class FavoritesService : IFavoriteTalkgroupMembership
{
    internal const string LocalOnlyNotice = "Favorites are saved on this device only and will not sync with other devices.";

    private sealed record Snapshot(string Owner, FavoritesCollection Collection,
        HashSet<(Guid ServerId, FavoriteTalkgroupIdentity Identity)> Membership,
        HashSet<Guid> Enabled, IReadOnlyDictionary<string, string> ServerNames)
    {
        internal static Snapshot Empty { get; } = new(FavoritesRepository.Device, new([], [], false), [], [],
            new Dictionary<string, string>());
    }

    internal sealed record VerifiedLogin(long AccountVersion, Uri Endpoint, FavoriteSyncResponse FirstPage,
        CancellationToken NetworkGeneration);

    private readonly ISettingsService _settings;
    private readonly FavoritesRepository _db;
    private readonly FavoritesSyncClient _client;
    private readonly PrivacyNetworkPolicy _network;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _lifetimeGate = new();
    private CancellationTokenSource _accountLifetime = new();
    private volatile IReadOnlyList<SavedScannerConnection> _profiles = [];
    private Snapshot _snapshot = Snapshot.Empty;
    private int _started;
    private int _forcePoll;
    private long _rejectedVersion = -1;
    private string? _syncNotice;

    public FavoritesService(ISettingsService settings, FavoritesRepository db)
        : this(settings, db, WordPressSession.CreateClient(settings))
    {
    }

    internal FavoritesService(ISettingsService settings, FavoritesRepository db, HttpClient client)
    {
        _settings = settings;
        _db = db;
        _network = PrivacyNetworkPolicy.For(settings);
        _client = new FavoritesSyncClient(client);
        _settings.AccountIdentityChanged += OnAccountChanged;
        _settings.LocalOnlyModeChanged += (_, _) =>
        {
            UpdateSnapshot();
            Wake(force: true);
            Changed?.Invoke();
        };
        try { UpdateSnapshot(); }
        catch (Exception ex)
        {
            SetNotice("Favorites could not be loaded from this device.");
            AppLog.Add(() => $"Favorites could not be loaded ({ex.GetType().Name}).");
        }
    }

    internal event Action? Changed;

    internal string OwnerKey => Volatile.Read(ref _snapshot).Owner;
    internal FavoritesCollection Collection => Volatile.Read(ref _snapshot).Collection;
    internal bool IsSignedIn => OwnerKey != FavoritesRepository.Device;
    internal string StatusText => _settings.LocalOnlyMode ? LocalOnlyNotice
        : Volatile.Read(ref _syncNotice) ?? (IsSignedIn
            ? Collection.Conflicts.Length > 0 ? "Some Favorites need a sync decision."
            : Collection.HasPending ? "Favorites changes are waiting to sync." : "Favorites sync is enabled."
            : "Sign in to sync Favorites with your other devices.");

    bool IFavoriteTalkgroupMembership.Contains(TalkgroupKey key, FavoriteTalkgroupIdentity? identity) =>
        Contains(key.ServerId, identity);

    internal bool Contains(Guid? serverId, FavoriteTalkgroupIdentity? identity)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return serverId is { } id && identity != null && snapshot.Enabled.Contains(id) && snapshot.Membership.Contains((id, identity));
    }

    internal bool Contains(CallItem call) => Contains(call.ServerId, FavoriteTalkgroupIdentity.From(call.CardMetadata));

    internal bool IsSaved(CallItem call) => call.ServerId is { } id && FavoriteTalkgroupIdentity.From(call.CardMetadata) is { } identity
        && Volatile.Read(ref _snapshot).Membership.Contains((id, identity));

    internal void SetProfiles(IReadOnlyList<SavedScannerConnection> profiles)
    {
        if (_profiles.SequenceEqual(profiles))
            return;
        _profiles = profiles;
        UpdateSnapshot();
    }

    internal void SetCall(string owner, CallItem call, bool saved, string? groupId = null) => Edit(owner, () =>
    {
        var profile = _profiles.FirstOrDefault(profile => profile.Id == call.ServerId);
        if (profile == null || FavoriteTalkgroupIdentity.From(call.CardMetadata) is not { } identity)
            return;
        var key = DirectoryKey(profile) ?? _db.EnsureLink(owner, profile.Id);
        var name = string.IsNullOrWhiteSpace(call.CardMetadata?.Talkgroup) ||
            call.CardMetadata.Talkgroup.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? identity.Talkgroup : call.CardMetadata.Talkgroup;
        _db.Set(owner, FavoriteValue.Talkgroup(key, identity, name, groupId), deleted: !saved);
    });

    internal void SetValue(string owner, FavoriteValue value, bool deleted = false) =>
        Edit(owner, () => _db.Set(owner, value, deleted));

    internal void Resolve(string owner, string id, bool useLocal) => Edit(owner, () => _db.Resolve(owner, id, useLocal));

    internal void ImportDevice(string owner) => Edit(owner, () => _db.ImportDevice(owner));

    internal string? GetLinkCode(string owner, Guid server)
    {
        var key = WithOwner(owner, () => _profiles.Any(profile => profile.Id == server && DirectoryKey(profile) == null)
            ? _db.EnsureLink(owner, server) : null);
        if (key != null)
            UpdateSnapshot();
        return key;
    }

    internal bool Link(string owner, Guid server, string code)
    {
        var linked = false;
        Edit(owner, () =>
        {
            if (_profiles.Any(profile => profile.Id == server && DirectoryKey(profile) == null))
                linked = _db.Link(owner, server, code.Trim().ToLowerInvariant());
        });
        return linked;
    }

    internal bool HasServerFavorites(SavedScannerConnection profile)
    {
        var owner = OwnerKey;
        var key = DirectoryKey(profile) ?? _db.ReadLinks(owner).GetValueOrDefault(profile.Id);
        return key != null && Collection.Values.Any(value => value.ServerKey == key);
    }

    internal void DeleteServerFavorites(string owner, SavedScannerConnection profile) => Edit(owner, () =>
    {
        var key = DirectoryKey(profile) ?? _db.ReadLinks(owner).GetValueOrDefault(profile.Id);
        if (key != null)
            _db.DeleteServer(owner, key);
    });

    internal string ServerName(FavoriteValue value) => value.ServerKey is { } key &&
        Volatile.Read(ref _snapshot).ServerNames.TryGetValue(key, out var name) ? name : "Unlinked recorder";

    private static string? DirectoryKey(SavedScannerConnection profile) => profile.DirectoryId is > 0
        ? "d:" + profile.DirectoryId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : HostedServerRules.IsProvidedDefaultServerUrl(profile.Endpoint) ? "d:joe" : null;

    private T? WithOwner<T>(string expected, Func<T> action)
    {
        var session = WordPressSession.Capture(_settings);
        T? result = default;
        session.TryApplyIdentity(() =>
        {
            var owner = Owner(session)?.Key ?? FavoritesRepository.Device;
            if (owner == expected)
                result = action();
        });
        return result;
    }

    private void Edit(string expected, Action action)
    {
        if (WithOwner(expected, () =>
        {
            action();
            return true;
        }) != true)
            return;
        UpdateSnapshot();
        Wake();
    }

    private void UpdateSnapshot()
    {
        var session = WordPressSession.Capture(_settings);
        var changed = false;
        session.TryApplyIdentity(() =>
        {
            var owner = Owner(session)?.Key ?? FavoritesRepository.Device;
            var collection = _db.Read(owner);
            var links = _db.ReadLinks(owner);
            var byServer = collection.Values.Where(value => value.ServerKey != null).ToLookup(value => value.ServerKey);
            var membership = new HashSet<(Guid, FavoriteTalkgroupIdentity)>();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var profiles = _profiles;
            var enabled = profiles.Where(profile => profile.Enabled).Select(profile => profile.Id).ToHashSet();
            foreach (var profile in profiles)
            {
                var key = DirectoryKey(profile) ?? links.GetValueOrDefault(profile.Id);
                if (key == null)
                    continue;
                names[key] = profile.Name;
                foreach (var value in byServer[key])
                    if (value.Identity is { } identity)
                        membership.Add((profile.Id, identity));
            }

            var previous = _snapshot;
            changed = previous.Owner != owner || previous.Collection.HasPending != collection.HasPending ||
                !previous.Collection.Values.SequenceEqual(collection.Values) ||
                !previous.Collection.Conflicts.SequenceEqual(collection.Conflicts) || !previous.Membership.SetEquals(membership) ||
                !previous.Enabled.SetEquals(enabled) || !previous.ServerNames.SequenceEqual(names);
            if (changed)
                Volatile.Write(ref _snapshot, new(owner, collection, membership, enabled, names));
        });
        if (changed)
            Changed?.Invoke();
    }

    private FavoritesOwner? Owner(WordPressSession session)
    {
        if (_settings.VerifiedFavoritesUserId is not { } user)
            return null;
        try
        {
            if (_settings.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out var login, out var password) &&
                !string.IsNullOrWhiteSpace(login) && !string.IsNullOrEmpty(password))
                return new(session.Endpoint("favorites/sync").AbsoluteUri, user);
        }
        catch (CredentialStorageException) { }
        return null;
    }

    private void OnAccountChanged(object? sender, EventArgs args)
    {
        CancellationTokenSource retired;
        lock (_lifetimeGate)
        {
            retired = _accountLifetime;
            _accountLifetime = new();
        }
        retired.Cancel();
        retired.Dispose();
        Volatile.Write(ref _syncNotice, null);
        // Settings has already unbound the last verified account. Never keep
        // publishing it if the local store cannot be read at this instant.
        Volatile.Write(ref _snapshot, Snapshot.Empty);
        try { UpdateSnapshot(); }
        catch (Exception ex) { AppLog.Add(() => $"Favorites cache could not be refreshed ({ex.GetType().Name})."); }
        Changed?.Invoke();
        Wake(force: true);
    }

    private CancellationToken AccountToken()
    {
        lock (_lifetimeGate)
            return _accountLifetime.Token;
    }

    internal async Task<VerifiedLogin> VerifyLoginAsync(string username, string password, CancellationToken token)
    {
        var session = WordPressSession.Capture(_settings);
        var endpoint = session.Endpoint("favorites/sync");
        var generation = _network.CloudToken();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, generation, AccountToken());
        if (!session.IsCurrentAccount)
            throw new OperationCanceledException(linked.Token);
        var response = await _client.ExchangeAsync(endpoint, username, password, new(null, []), linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return new(session.AccountVersion, endpoint, response, generation);
    }

    internal bool CanCompleteLogin(VerifiedLogin login) => !login.NetworkGeneration.IsCancellationRequested &&
        _settings.ReadAccountSession().AccountVersion == login.AccountVersion;

    internal void CompleteLogin(VerifiedLogin login, string username, string password)
    {
        _settings.TryApplyAccountIdentity(login.AccountVersion, () =>
        {
            if (login.NetworkGeneration.IsCancellationRequested)
                return;
            _db.Apply(new FavoritesOwner(login.Endpoint.AbsoluteUri, login.FirstPage.UserId).Key, login.FirstPage);
            _settings.SetServerCredentials(HostedServerRules.DefaultServerUrl, username, password);
            _settings.VerifiedFavoritesUserId = login.FirstPage.UserId;
            Interlocked.Exchange(ref _rejectedVersion, -1);
        });
        SetNotice(null);
        UpdateSnapshot();
        Wake(force: true);
    }

    internal void SignOut() => _settings.ClearServerCredentials(HostedServerRules.DefaultServerUrl);

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _ = Task.Run(RunAsync);
    }

    internal void Wake(bool force = false)
    {
        if (force)
            Interlocked.Exchange(ref _forcePoll, 1);
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RunAsync()
    {
        var nextRetry = DateTimeOffset.MinValue;
        var failures = 0;
        var delay = TimeSpan.FromMinutes(1);
        while (true)
        {
            await _wake.WaitAsync(delay).ConfigureAwait(false);
            delay = TimeSpan.FromMinutes(1);
            if (Interlocked.Exchange(ref _forcePoll, 0) != 0)
                nextRetry = DateTimeOffset.MinValue;
            if (!_network.AllowsCloud)
                continue;
            var remaining = nextRetry - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                delay = remaining;
                continue;
            }

            try
            {
                await SyncOnceAsync().ConfigureAwait(false);
                failures = 0;
                nextRetry = DateTimeOffset.MinValue;
                if (Collection.HasPending && _network.AllowsCloud)
                    delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (!_network.AllowsCloud)
            {
                // Local mutations remain durable throughout Local Only Mode.
            }
            catch (Exception ex)
            {
                failures = Math.Min(failures + 1, 5);
                delay = TimeSpan.FromSeconds(15 * (1 << failures));
                nextRetry = DateTimeOffset.UtcNow + delay;
                SetNotice("Favorites sync is unavailable. Saved changes are kept on this device.");
                AppLog.Add(() => $"Favorites sync will retry ({ex.GetType().Name}).");
            }
        }
    }

    internal async Task SyncOnceAsync()
    {
        if (!_network.AllowsCloud)
            return;
        var session = WordPressSession.Capture(_settings);
        if (Interlocked.Read(ref _rejectedVersion) == session.AccountVersion)
            return;

        string? username = null;
        string? password = null;
        FavoritesOwner? owner = null;
        if (!session.TryApplyIdentity(() =>
            {
                if (_settings.TryGetServerCredentials(HostedServerRules.DefaultServerUrl, out var user, out var secret))
                    (username, password) = (user, secret);
                owner = Owner(session);
            }) || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return;

        var cloud = _network.CloudToken();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cloud, AccountToken());
        if (!session.IsCurrentAccount)
            return;
        var endpoint = session.Endpoint("favorites/sync");
        bool Apply(Action action)
        {
            var applied = false;
            session.TryApplyIdentity(() =>
            {
                if (linked.IsCancellationRequested)
                    return;
                action();
                applied = true;
            });
            return applied;
        }

        try
        {
            if (owner == null)
            {
                var response = await _client.ExchangeAsync(endpoint, username, password, new(null, []), linked.Token).ConfigureAwait(false);
                owner = new(endpoint.AbsoluteUri, response.UserId);
                if (!Apply(() =>
                {
                    _db.Apply(owner.Key, response);
                    _settings.VerifiedFavoritesUserId = response.UserId;
                }))
                    return;
                UpdateSnapshot();
            }

            await _client.ReconcileAsync(_db, owner, endpoint, username, password, Apply, linked.Token).ConfigureAwait(false);
            if (Apply(() => SetNotice(null)))
                UpdateSnapshot();
        }
        catch (FavoritesAccountRejectedException)
        {
            if (Apply(() =>
            {
                _settings.VerifiedFavoritesUserId = null;
                Interlocked.Exchange(ref _rejectedVersion, session.AccountVersion);
                SetNotice("Sign in again to sync Favorites. Existing changes are saved for their original account.");
            }))
                UpdateSnapshot();
        }
        catch (Exception) when (linked.IsCancellationRequested)
        {
            // Retired account and privacy generations cannot publish responses.
        }
    }

    private void SetNotice(string? notice)
    {
        if (Interlocked.Exchange(ref _syncNotice, notice) != notice)
            Changed?.Invoke();
    }
}
