using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GitExtensions.Linux.Desktop;

/// <summary>A stable native ItemsSource: append in ranges, reorder by Git identity, never Reset the loaded grid.</summary>
internal sealed class IncrementalRevisionRows : ObservableCollection<object>
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    internal int IndexOfKey(string key) => _indices.GetValueOrDefault(key, -1);
    internal int KeyIndex(string key) => IndexOfKey(key);
    internal int IndexOfHash(string hash) => IndexOfKey(hash);

    internal void PublishOrder(IReadOnlyList<object> desired)
    {
        CheckReentrancy();
        if (desired.Count < Count) { throw new InvalidOperationException("A streamed append cannot remove an admitted revision."); }
        for (int index = 0; index < desired.Count; index++)
        {
            object item = desired[index];
            string key = HistoryRevisionItems.Key(item);
            if (index < Count && HistoryRevisionItems.Key(this[index]) == key) { continue; }
            if (_indices.TryGetValue(key, out int oldIndex))
            {
                // Real source Move, not WritableSelectedItems.Move. The caller restores selection/caret by key afterward.
                Move(oldIndex, index);
                for (int changed = Math.Min(oldIndex, index); changed <= Math.Max(oldIndex, index); changed++)
                { _indices[HistoryRevisionItems.Key(this[changed])] = changed; }
            }
            else if (index == Count)
            {
                int end = index;
                while (end < desired.Count && !_indices.ContainsKey(HistoryRevisionItems.Key(desired[end]))) { end++; }
                object[] tail = Enumerable.Range(index, end - index).Select(i => desired[i]).ToArray();
                AppendRange(tail); index = end - 1;
            }
            else
            {
                Insert(index, item);
                for (int changed = index; changed < Count; changed++) { _indices[HistoryRevisionItems.Key(this[changed])] = changed; }
            }
        }
    }

    private void AppendRange(object[] items)
    {
        if (items.Length == 0) { return; }
        int first = Count;
        foreach (object item in items) { _indices.Add(HistoryRevisionItems.Key(item), Items.Count); Items.Add(item); }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items, first));
    }
}
