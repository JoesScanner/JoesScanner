using System.Runtime.CompilerServices;
using JoesScanner.Helpers;

namespace JoesScanner.Services;

internal sealed partial class DirectoryRecorderPolicy
{
    private static readonly ConditionalWeakTable<ISettingsService, DirectoryRecorderPolicy> Policies = new();
    private volatile SavedScannerConnection[] _profiles = [];

    internal static DirectoryRecorderPolicy For(ISettingsService settings) =>
        Policies.GetValue(settings, static _ => new());

    // The lifetime belongs to one recorder configuration. An A -> B -> A switch
    // can restore an identity, but never revives a request from its earlier login.
    private readonly Dictionary<Guid, RecorderOperation> _operations = [];
    private bool _loaded;

    internal void Update(IReadOnlyList<SavedScannerConnection> profiles)
    {
        lock (_operations)
        {
            foreach (var previous in _profiles)
            {
                var current = profiles.FirstOrDefault(p => p.Id == previous.Id);
                if (current == null || current.Enabled != previous.Enabled || current.Endpoint != previous.Endpoint ||
                    current.DirectoryBlocked != previous.DirectoryBlocked || current.DirectoryFirewall != previous.DirectoryFirewall)
                    Retire(previous.Id);
            }
            _profiles = [.. profiles];
            _loaded = true;
        }
    }

    internal RecorderOperation Capture(Guid id, Uri endpoint)
    {
        lock (_operations)
        {
            if (_loaded && !(_profiles.FirstOrDefault(p => p.Id == id) is { DirectoryBlocked: false } profile &&
                RecordingStorageReference.ServerKey(profile.Endpoint) == RecordingStorageReference.ServerKey(endpoint.AbsoluteUri)))
                throw new RecordingAccessException("The recorder configuration has changed.");
            if (!_operations.TryGetValue(id, out var owner))
                _operations[id] = owner = new RecorderOperation();
            return owner;
        }
    }

    internal void Retire(Guid id)
    {
        lock (_operations)
            if (_operations.Remove(id, out var owner))
                owner.Retire();
    }

    internal SavedScannerConnection? Find(Uri uri, Guid? id = null)
    {
        if (id is { } recorder)
            return _profiles.FirstOrDefault(profile => profile.Id == recorder);

        SavedScannerConnection? best = null;
        foreach (var profile in _profiles)
            if ((best == null || profile.Endpoint.Length > best.Endpoint.Length) && Contains(profile.Endpoint, uri))
                best = profile;
        return best;
    }

    internal static bool Contains(string endpoint, Uri request)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var recorder) || !HostedServerRules.SameOrigin(recorder, request))
            return false;
        var path = recorder.AbsolutePath.TrimEnd('/');
        return path.Length == 0 || request.AbsolutePath.Equals(path, StringComparison.Ordinal) ||
            request.AbsolutePath.StartsWith(path + "/", StringComparison.Ordinal);
    }
}
