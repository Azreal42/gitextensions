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

    /// <summary>Resolve a finished query snapshot without mutating the published source.</summary>
    internal IReadOnlyList<object> ResolveSnapshotItems(IReadOnlyList<object> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        object[] snapshot = desired.ToArray();
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (object item in snapshot)
        {
            string key = HistoryRevisionItems.Key(item);
            if (string.IsNullOrEmpty(key) || item is GitCommit && (key is ArtificialHistoryRevision.WorkingTreeKey or ArtificialHistoryRevision.IndexKey))
            { throw new ArgumentException("A snapshot must use actual commit keys or typed artificial endpoints.", nameof(desired)); }
            if (!keys.Add(key)) { throw new ArgumentException("A snapshot cannot contain duplicate revision keys.", nameof(desired)); }
        }
        for (int index = 0; index < snapshot.Length; index++)
        {
            object item = snapshot[index];
            if (_indices.TryGetValue(HistoryRevisionItems.Key(item), out int oldIndex)
                && HistoryRevisionItems.HasSameSnapshotData(this[oldIndex], item))
            { snapshot[index] = this[oldIndex]; }
        }
        return snapshot;
    }

    /// <summary>Publish a complete query generation by key, retaining unchanged objects and concrete notifications.</summary>
    internal void PublishSnapshot(IReadOnlyList<object> desired)
    {
        CheckReentrancy();
        // Validate the whole input before the first removal/replacement or notification.
        // Resolution is deliberately repeated even if the caller normalized before graph construction.
        IReadOnlyList<object> snapshot = ResolveSnapshotItems(desired);
        HashSet<string> keys = snapshot.Select(HistoryRevisionItems.Key).ToHashSet(StringComparer.Ordinal);
        for (int index = Count - 1; index >= 0; index--)
        {
            if (!keys.Contains(HistoryRevisionItems.Key(this[index]))) { RemoveAt(index); }
        }
        for (int index = 0; index < snapshot.Count; index++)
        {
            object item = snapshot[index];
            string key = HistoryRevisionItems.Key(item);
            if (_indices.TryGetValue(key, out int oldIndex))
            {
                if (oldIndex != index) { Move(oldIndex, index); }
                if (!ReferenceEquals(this[index], item)) { this[index] = item; }
            }
            else { Insert(index, item); }
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs change)
    {
        // Observers can query keys synchronously from the notification. Publish the new
        // index first; updating it after base.Move/Remove would expose the previous order.
        switch (change.Action)
        {
            case NotifyCollectionChangedAction.Add:
                IndexFrom(change.NewStartingIndex);
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (object item in change.OldItems!) { _indices.Remove(HistoryRevisionItems.Key(item)); }
                IndexFrom(change.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace:
                foreach (object item in change.OldItems!) { _indices.Remove(HistoryRevisionItems.Key(item)); }
                for (int index = change.NewStartingIndex; index < change.NewStartingIndex + change.NewItems!.Count; index++)
                { _indices[HistoryRevisionItems.Key(this[index])] = index; }
                break;
            case NotifyCollectionChangedAction.Move:
                int first = Math.Min(change.OldStartingIndex, change.NewStartingIndex);
                int last = Math.Max(change.OldStartingIndex, change.NewStartingIndex);
                for (int index = first; index <= last; index++) { _indices[HistoryRevisionItems.Key(this[index])] = index; }
                break;
            case NotifyCollectionChangedAction.Reset:
                _indices.Clear(); IndexFrom(0);
                break;
        }
        base.OnCollectionChanged(change);
    }

    private void IndexFrom(int first)
    {
        for (int index = first; index < Count; index++) { _indices[HistoryRevisionItems.Key(this[index])] = index; }
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
