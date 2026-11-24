using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using JoesScanner.Models;

namespace JoesScanner.ViewModels;

// Separate panes contain references to the canonical Live cards. The display
// has no connection, playback lease, or queue state.
internal sealed class LiveCallDisplay(ObservableCollection<CallItem> calls, bool separate = false,
    Func<CallItem, bool>? favorite = null)
{
    private readonly Dictionary<Guid, ServerProjection> _projections = [];
    private readonly ServerProjection _favorites = new([]);
    private readonly HashSet<CallItem> _observedCards = [];
    private Guid[] _servers = [];
    private bool _observing;

    internal const int CardLimit = 500;
    internal const double PanelMinimumWidth = 310;
    internal const double PanelSpacing = 8;
    internal ObservableCollection<CallItem> Combined => calls;
    internal ReadOnlyObservableCollection<CallItem> Favorites => _favorites.Cards;
    internal IReadOnlyList<Guid> Servers => _servers;
    internal bool Separate { get; private set; } = separate;
    internal Guid? SelectedServer { get; private set; }

    internal void SetSeparate(bool value) => Separate = value;

    internal void SetServers(IEnumerable<Guid> enabled)
    {
        var servers = enabled.ToArray();
        if (SelectedServer is not { } selected || !servers.Contains(selected))
        {
            var previous = SelectedServer is { } removed ? Array.IndexOf(_servers, removed) : 0;
            SelectedServer = servers.Length == 0 ? null : servers[Math.Clamp(previous, 0, servers.Length - 1)];
        }
        _servers = servers;
        RefreshFavorites();
    }

    internal bool Select(Guid server)
    {
        if (!_servers.Contains(server) || SelectedServer == server)
            return false;
        SelectedServer = server;
        return true;
    }

    internal bool MoveSelection(int direction)
    {
        if (SelectedServer is not { } selected || direction is not (-1 or 1))
            return false;
        var next = Array.IndexOf(_servers, selected) + direction;
        return next >= 0 && next < _servers.Length && Select(_servers[next]);
    }

    internal Guid[] VisibleServers(double width, bool sideBySide)
    {
        if (!Separate || SelectedServer is not { } selected || _servers.Length == 0)
            return [];

        var fits = sideBySide && double.IsFinite(width) && width > 0
            ? (int)Math.Min(_servers.Length, (width + PanelSpacing) / (PanelMinimumWidth + PanelSpacing))
            : 1;
        var count = Math.Max(1, fits);
        var first = Math.Min(Array.IndexOf(_servers, selected), _servers.Length - count);
        return _servers.AsSpan(first, count).ToArray();
    }

    internal ReadOnlyObservableCollection<CallItem> ForServer(Guid id)
    {
        if (!_projections.TryGetValue(id, out var projection))
        {
            projection = new ServerProjection(calls.Where(call => call.ServerId == id));
            _projections.Add(id, projection);
        }
        return projection.Cards;
    }

    internal void ForgetServer(Guid id) => _projections.Remove(id);

    internal void Observe()
    {
        if (_observing)
            return;
        _observing = true;
        calls.CollectionChanged += OnCallsChanged;
        ReconcileProjections();
        ObserveFavoriteCards();
        RefreshFavorites();
    }

    internal void Detach()
    {
        if (!_observing)
            return;
        _observing = false;
        calls.CollectionChanged -= OnCallsChanged;
        foreach (var card in _observedCards)
            card.PropertyChanged -= OnFavoriteMetadataChanged;
        _observedCards.Clear();
    }

    private void ObserveFavoriteCards()
    {
        if (favorite == null || !_observing)
            return;
        foreach (var old in _observedCards.Except(calls).ToArray())
        {
            old.PropertyChanged -= OnFavoriteMetadataChanged;
            _observedCards.Remove(old);
        }
        foreach (var added in calls)
            if (_observedCards.Add(added))
                added.PropertyChanged += OnFavoriteMetadataChanged;
    }

    private void OnFavoriteMetadataChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CallItem.CardMetadata))
            RefreshFavorites();
    }

    internal void RefreshFavorites()
    {
        if (favorite == null)
            return;
        var valid = new List<CallItem>();
        foreach (var card in calls)
        {
            var saved = favorite(card);
            card.SetFavorite(saved);
            if (saved && card.ServerId is { } id && _servers.Contains(id))
                valid.Add(card);
        }

        var current = _favorites.Items;
        var members = valid.ToHashSet();
        for (var i = current.Count - 1; i >= 0; i--)
            if (!members.Contains(current[i]))
                current.RemoveAt(i);
        for (var i = 0; i < valid.Count; i++)
        {
            if (i < current.Count && ReferenceEquals(current[i], valid[i]))
                continue;
            var previous = current.IndexOf(valid[i]);
            if (previous >= 0)
                current.Move(previous, i);
            else
                current.Insert(i, valid[i]);
        }
    }

    private void ReconcileProjections()
    {
        foreach (var (id, projection) in _projections)
        {
            var current = calls.Where(call => call.ServerId == id).ToArray();
            if (!projection.Items.SequenceEqual(current))
            {
                projection.Items.Clear();
                foreach (var card in current)
                    projection.Items.Add(card);
            }
        }
    }

    private void OnCallsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ObserveFavoriteCards();
        RefreshFavorites();
        if (e.Action is NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Reset)
        {
            ReconcileProjections();
            return;
        }

        if (e.OldItems != null)
            foreach (CallItem old in e.OldItems)
                if (old.ServerId is { } id && _projections.TryGetValue(id, out var projection))
                    projection.Items.Remove(old);

        if (e.NewItems == null)
            return;
        var offset = e.NewStartingIndex;
        foreach (CallItem added in e.NewItems)
        {
            if (added.ServerId is { } id && _projections.TryGetValue(id, out var projection))
            {
                var index = calls.Take(offset).Count(card => card.ServerId == id);
                projection.Items.Insert(index, added);
            }
            offset++;
        }
    }

    // This only evicts a display reference. The durable queue, including rows
    // without visible cards, retains its own lifetime and arrival order.
    internal static bool InsertCard(ObservableCollection<CallItem> visible, CallItem card, int index, CallItem? playing)
    {
        if (index >= CardLimit || visible.Contains(card))
            return false;
        if (visible.Count == CardLimit)
        {
            var oldest = visible.Count - 1;
            if (ReferenceEquals(visible[oldest], playing))
                oldest--;
            if (index > oldest)
                return false;
            visible.RemoveAt(oldest);
        }
        visible.Insert(index, card);
        return true;
    }

    private sealed class ServerProjection
    {
        internal ServerProjection(IEnumerable<CallItem> source)
        {
            Items = new ObservableCollection<CallItem>(source);
            Cards = new ReadOnlyObservableCollection<CallItem>(Items);
        }

        internal ObservableCollection<CallItem> Items { get; }
        internal ReadOnlyObservableCollection<CallItem> Cards { get; }
    }
}
