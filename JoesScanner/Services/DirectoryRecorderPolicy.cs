using System.Runtime.CompilerServices;
using JoesScanner.Helpers;

namespace JoesScanner.Services;

internal sealed class DirectoryRecorderPolicy
{
    private static readonly ConditionalWeakTable<ISettingsService, DirectoryRecorderPolicy> Policies = new();
    private volatile SavedScannerConnection[] _profiles = [];

    internal static DirectoryRecorderPolicy For(ISettingsService settings) =>
        Policies.GetValue(settings, static _ => new());

    internal void Update(IReadOnlyList<SavedScannerConnection> profiles) => _profiles = [.. profiles];

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
