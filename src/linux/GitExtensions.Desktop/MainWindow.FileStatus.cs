using Avalonia.Controls;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private FileStatusListView? _changesFilesView;
    private GitChange? SelectedWorkingChange => (_changes.SelectedItem as TreeViewItem)?.Tag as GitChange;
    private IEnumerable<GitChange> SelectedWorkingChanges => _changesFilesView?.SelectedTags.OfType<GitChange>() ?? [];
    private IEnumerable<GitChange> FilteredWorkingChanges => _changesFilesView?.FilteredEntries.Select(entry => entry.Tag).OfType<GitChange>() ?? [];

    private Control BuildWorkingFilesView()
    {
        _changesFilesView = new FileStatusListView(_changes) { Name = "WorkingFileStatusList" };
        _changesFilesView.DisplayChanged += async (_, _) =>
        {
            if (_refreshing || _busy) { return; }
            if (SelectedWorkingChange is not null) { await LoadSelectedDiffAsync(); }
            else { ClearWorkingDiff(); }
        };
        return _changesFilesView;
    }

    private void SetWorkingFiles(IReadOnlyList<GitChange> changes, string? selectedPath, bool preserveSelection)
    {
        _changesFilesView!.SetGroups([new FileStatusListGroup("working", "working", new TextBlock { Text = T("Working directory") },
            changes.Select(change => new FileStatusListEntry(change, change.Path, change.OriginalPath,
                change.IsConflict ? 'U' : change.IsUntracked ? 'A' : change.IsModified ? change.WorkTreeStatus : change.IndexStatus,
                change.SimilarityPercent)).ToArray())], preserveSelection);
        if (selectedPath is not null && changes.FirstOrDefault(change => change.Path == selectedPath) is GitChange selected)
        { _changesFilesView.SelectTags([selected]); }
    }

    private void ClearWorkingDiff()
    {
        _changesContent?.ClearImage(); _changesDiff.Text = ""; _selectedPatch = null; _hunks.ItemsSource = null;
    }

    private static IEnumerable<TreeViewItem> FileStatusTreeItems(TreeView tree)
        => FileStatusTreeItems(tree.Items.OfType<TreeViewItem>());

    private static IEnumerable<TreeViewItem> FileStatusTreeItems(IEnumerable<TreeViewItem> items)
    {
        foreach (TreeViewItem item in items)
        {
            yield return item;
            foreach (TreeViewItem child in FileStatusTreeItems(item.Items.OfType<TreeViewItem>())) { yield return child; }
        }
    }
}
