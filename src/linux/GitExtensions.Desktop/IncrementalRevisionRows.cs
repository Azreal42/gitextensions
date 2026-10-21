using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GitExtensions.Linux.Desktop;

/// <summary>A stable native ItemsSource: append in ranges, reorder by Git identity, never Reset the loaded grid.</summary>
internal sealed class IncrementalRevisionRows : ObservableCollection<GitCommit>
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
    internal int IndexOfHash(string hash) => _indices.GetValueOrDefault(hash, -1);

    internal void PublishOrder(IReadOnlyList<GitCommit> desired)
    {
        CheckReentrancy();
        if (desired.Count < Count) { throw new InvalidOperationException("A streamed append cannot remove an admitted revision."); }
        for (int index = 0; index < desired.Count; index++)
        {
            GitCommit commit = desired[index];
            if (index < Count && this[index].Hash == commit.Hash) { continue; }
            if (_indices.TryGetValue(commit.Hash, out int oldIndex))
            {
                // Real source Move, not WritableSelectedItems.Move. The caller restores selection/caret by key afterward.
                Move(oldIndex, index);
                for (int changed = Math.Min(oldIndex, index); changed <= Math.Max(oldIndex, index); changed++)
                { _indices[this[changed].Hash] = changed; }
            }
            else if (index == Count)
            {
                int end = index;
                while (end < desired.Count && !_indices.ContainsKey(desired[end].Hash)) { end++; }
                GitCommit[] tail = Enumerable.Range(index, end - index).Select(i => desired[i]).ToArray();
                AppendRange(tail); index = end - 1;
            }
            else
            {
                Insert(index, commit);
                for (int changed = index; changed < Count; changed++) { _indices[this[changed].Hash] = changed; }
            }
        }
    }

    private void AppendRange(GitCommit[] commits)
    {
        if (commits.Length == 0) { return; }
        int first = Count;
        foreach (GitCommit commit in commits) { _indices.Add(commit.Hash, Items.Count); Items.Add(commit); }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, commits, first));
    }
}
