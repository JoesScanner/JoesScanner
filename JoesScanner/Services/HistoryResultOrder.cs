using System.Collections.ObjectModel;
using JoesScanner.Models;

namespace JoesScanner.Services;

internal static class HistoryResultOrder
{
    internal static int Compare(CallItem first, CallItem second)
    {
        var byTime = Nullable.Compare(second.RecordingTimeUtc, first.RecordingTimeUtc);
        if (byTime != 0)
            return byTime;

        return string.CompareOrdinal(second.BackendId, first.BackendId);
    }

    // Most pages append. Imported recordings can have timestamps out of file
    // order. Insert those in place while retaining the existing card objects.
    internal static void Add(ObservableCollection<CallItem> cards, IReadOnlyList<CallItem> page)
    {
        var index = 0;
        foreach (var call in page)
        {
            while (index < cards.Count && Compare(cards[index], call) <= 0)
                index++;
            cards.Insert(index++, call);
        }
    }
}
