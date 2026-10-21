using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private IReadOnlySet<string>? _highlightedHistoryBranch, _relatedHistory;
    private Dictionary<string, (DateTimeOffset Date, string Notes)> _historyMetadata = new(StringComparer.Ordinal);
    private IReadOnlyList<GitCommit>? _relationshipSource;
    private string? _relationshipRevision;

    private GitLogQuery ConfigureHistoryViewQuery(GitLogQuery query)
    {
        _fileHistoryPreferences = _fileHistoryPreferencesStore.Load();
        return _fileHistoryPreferences.ConfigureQuery(query with
        {
            IncludeStashes = _historyView.ShowStashes,
            IncludeLatestStashInBranchFilter = _historyView.ShowStashes,
            IncludeSessionRefs = _historyView.ShowSessionCheckpoints, Sort = _historyView.Sort
        });
    }

    private GitReference[] HistoryViewReferences(IEnumerable<GitReference> references)
        => RevisionHistoryPresentation.FilterReferences(references, _historyView);

    private async Task LoadHistoryViewMetadataAsync(GitRepository repository, IReadOnlyList<GitCommit> commits, CancellationToken token)
    {
        if ((_historyView.ShowAuthorDate && !_historyView.ShowNotes && !_historyView.ShowNotesColumn) || commits.Count == 0)
        { _historyMetadata = new(StringComparer.Ordinal); return; }
        Dictionary<string, (DateTimeOffset, string)> result = new(StringComparer.Ordinal);
        // Bounded batches keep argv below native limits, including with a 100k history window.
        foreach (GitCommit[] batch in commits.Chunk(200))
        {
            string format = _historyView.ShowNotes || _historyView.ShowNotesColumn ? "%H%x00%cI%x00%N" : "%H%x00%cI%x00";
            GitResult read = await repository.RunAsync(new[] { "show", "--no-patch", "--no-ext-diff", "--no-textconv", "--encoding=UTF-8", "-z", "--format=" + format }
                .Concat(batch.Select(c => c.Hash)).Append("--").ToArray(), token);
            if (!read.Success) { throw new GitCommandException(["show"], read); }
            string[] values = read.StandardOutput.Split('\0');
            if (values.Length != batch.Length * 3 + 1) { throw new FormatException("Invalid commit metadata or binary Git note."); }
            for (int i = 0; i < batch.Length; i++)
            {
                if (values[i * 3] != batch[i].Hash) { throw new FormatException("Commit metadata did not match its requested revision."); }
                result.Add(values[i * 3], (DateTimeOffset.Parse(values[i * 3 + 1], CultureInfo.InvariantCulture), values[i * 3 + 2]));
            }
        }
        token.ThrowIfCancellationRequested(); _historyMetadata = result;
    }

    private async Task ChangeHistoryViewAsync(Func<HistoryViewPreferences, HistoryViewPreferences> change, bool reload)
    {
        bool showArtificial = _historyView.ShowArtificialCommits;
        HistoryViewPreferences desired = change(_historyView);
        TrackColorsViewChange(_historyView, desired);
        _historyView = desired;
        bool artificialChanged = showArtificial != _historyView.ShowArtificialCommits;
        if (artificialChanged) { _parentChildNavigation?.Clear(); }
        _historyQuery = _historyQuery with { Reflog = _historyView.ShowReflog };
        if (reload && _repository is not null) { await RefreshAsync(); }
        else if (artificialChanged) { await RepublishArtificialHistoryVisibilityAsync(); }
        ApplyHistoryViewPresentation(); RefreshHistoryMenuStates();
    }

    private async Task RepublishArtificialHistoryVisibilityAsync()
    {
        if (_repository is null || _historyStatus is not GitStatus status || _lifetime.IsCancellationRequested) { return; }
        using WindowWork work = TrackWindowWork();
        OperationFocus? focus = CaptureOperationFocus();
        HistoryBookmark bookmark = CaptureHistoryBookmark();
        string selection = RevisionSelectionIdentity;
        bool refreshing = _refreshing;
        try
        {
            _refreshing = true;
            try
            {
                PublishHistorySnapshot(_history, status, _navigationReferences, _historyQuery, _historySnapshotAncestors,
                    _historySnapshotAncestors, bookmark, changedRepository: false, requested: null, first: null, retainedComparison: _browseInitialComparison);
                if (bookmark.Anchor is not null && HistoryItem(bookmark.Anchor) is null)
                { _commits.Selection.AnchorIndex = PrimaryHistoryItem is { } current ? _commits.Items.IndexOf(current) : -1; }
                if (focus?.HistoryKey is string key && HistoryItem(key) is null)
                { focus = focus with { Input = _commits, HistoryKey = PrimaryHistoryItem is { } current ? HistoryRevisionItems.Key(current) : null }; }
                if (PrimaryHistoryItem is null && selection != RevisionSelectionIdentity)
                {
                    // The View menu can own focus while its last history row is
                    // removed. Return to the empty grid, not that dismissed menu.
                    if (focus is null || focus.Input is MenuItem)
                    { focus = new OperationFocus(_repository, _commits, HistoryKey: null); }
                    // Unborn history can consist of only the two artificial rows. Once hidden,
                    // no old diff, tree or grep source may continue representing one of them.
                    ClearRevisionDiff(); ClearRevisionPreview();
                    _diff.Text = status.IsUnborn ? "This repository has no commits yet." : T("No commits match the current history filters.");
                    CancelFileGrep(); _fileGrepNavigation?.Cancel(); _fileGrepResult = null;
                    _fileTreeSource = null; _fileTreeRevision = null; _fileTreeRepository = null;
                    _allFileTreePaths = []; _fileTreeEntries = new Dictionary<string, GitComparisonTreeEntry>();
                    UpdateFileTreeEntries([]); ShowSelectedTreeFolder();
                }
            }
            finally { _refreshing = refreshing; }
            if (selection != RevisionSelectionIdentity)
            { _historySelectionWork = PublishHistorySelectionChange(); await _historySelectionWork; }
        }
        finally
        {
            // Use the existing guarded restoration after a real popup has dismissed.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RestoreOperationFocus(focus), Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ApplyHistoryViewPresentation()
    {
        _historyGraph.IsVisible = _historyView.ShowGraph;
        _historyGraph.References = HistoryViewReferences(_navigationReferences);
        ApplyHistoryToolbarVisibility();
        UpdateHistoryPresentation();
    }

    private void ApplyHistoryRowOptions(Control row, GitCommit commit)
    {
        bool dim = _historyView.DrawNonRelativesTextGray
            && (_highlightedHistoryBranch ?? _relatedHistory) is { Count: > 0 } related
            && !related.Contains(commit.Hash);
        RevisionHistoryPresentation.Apply(row, commit, _historyView, HistoryViewReferences(_navigationReferences),
            _historyMetadata.GetValueOrDefault(commit.Hash), dim, T);
    }

    private IReadOnlySet<string>? RelatedHistory(string? revision, bool descendants)
    {
        if (revision is null) { return null; }
        Dictionary<string, GitCommit> byHash = _history.ToDictionary(commit => commit.Hash, StringComparer.Ordinal);
        HashSet<string> related = new(StringComparer.Ordinal); Stack<string> queue = new(); queue.Push(revision);
        while (queue.TryPop(out string? current))
        {
            if (!related.Add(current) || !byHash.TryGetValue(current, out GitCommit? commit)) { continue; }
            foreach (string parent in commit.Parents) { queue.Push(parent); }
        }
        if (descendants)
        {
            Dictionary<string, string[]> children = _history.SelectMany(c => c.Parents.Select(p => (Parent: p, Child: c.Hash)))
                .GroupBy(x => x.Parent).ToDictionary(g => g.Key, g => g.Select(x => x.Child).ToArray(), StringComparer.Ordinal);
            HashSet<string> visited = new(StringComparer.Ordinal); queue.Push(revision);
            while (queue.TryPop(out string? current))
            {
                if (!visited.Add(current)) { continue; }
                related.Add(current);
                foreach (string child in children.GetValueOrDefault(current) ?? []) { queue.Push(child); }
            }
        }
        return related;
    }

    private Task HighlightHistoryBranchAsync()
    {
        _highlightedHistoryBranch = _highlightedHistoryBranch is null ? RelatedHistory(CurrentRevision, false) : null;
        UpdateHistoryPresentation(); return Task.CompletedTask;
    }

    private static bool IsFilterToolbarItem(Control control) => control.Name is "BrowseHistoryScope" or "HistoryToolbarBranches" or "BranchHistoryFilterType" or "HistoryToolbarQuickFilter";

    private void ApplyHistoryToolbarVisibility()
    {
        RefreshScriptsEnabled();
        if (_browserToolbar is null) { return; }
        _browserToolbar.SetItemVisibility(control => (IsFilterToolbarItem(control) ? _historyView.ShowFilterToolbar : _historyView.ShowMainToolbar)
            && (control.Name is null || !_historyView.HiddenToolbarItems.Contains(control.Name, StringComparer.Ordinal)));
        _browserToolbar.IsVisible = _historyView.ShowMainToolbar || _historyView.ShowFilterToolbar;
    }

    private MenuItem BuildHistoryToolbarsMenu()
    {
        MenuItem result = new() { Name = "ViewToolbars", Header = T("Toolbars") };
        MenuItem Group(string name, string label, Func<bool> state, Func<HistoryViewPreferences, HistoryViewPreferences> change, bool filter)
        {
            MenuItem group = OriginalAction("FormBrowse", name, label, null, () => ChangeHistoryViewAsync(change, false));
            group.ToggleType = MenuItemToggleType.CheckBox;
            group.SubmenuOpened += (_, args) =>
            {
                if (args.Source != group) { return; }
                if (group.Items.Count == 0 && _browserToolbar is not null)
                {
                    foreach (Control control in _browserToolbar.AllItems.Where(c => IsFilterToolbarItem(c) == filter && c.Name != "BrowseCancelOperation"))
                    {
                        string? key = control.Name; if (key is null) { continue; }
                        string caption = ToolTip.GetTip(control) as string ?? key switch
                        {
                            "RepositoryPath" => T("Repository"), "CommitInfoPositionControl" => T("Commit info position"),
                            "HistoryToolbarBranches" => T("Branches"), "BranchHistoryFilterType" => T("Branch filter suggestions"),
                            "HistoryToolbarQuickFilter" => T("Filter"), _ => key
                        };
                        string? icon = (control as Button)?.Content is Image image ? image.Tag as string
                            : control.GetVisualDescendants().OfType<Image>().Select(i => i.Tag as string).FirstOrDefault(i => i is not null);
                        HistoryCommand command = new(key, caption, () => ChangeHistoryViewAsync(p => p with
                        { HiddenToolbarItems = p.HiddenToolbarItems.Contains(key) ? p.HiddenToolbarItems.Where(k => k != key).ToArray() : [.. p.HiddenToolbarItems, key] }, false), icon,
                            Checked: () => !_historyView.HiddenToolbarItems.Contains(key));
                        foreach (object item in CreateHistoryMenuItems([command], "Toolbar")) { group.Items.Add(item); }
                    }
                }
                group.IsChecked = state(); RefreshHistoryMenuStates();
            };
            return group;
        }
        MenuItem main = Group("ViewMainToolbar", "Main toolbar", () => _historyView.ShowMainToolbar, p => p with { ShowMainToolbar = !p.ShowMainToolbar }, false);
        MenuItem filters = Group("ViewFilterToolbar", "Filter toolbar", () => _historyView.ShowFilterToolbar, p => p with { ShowFilterToolbar = !p.ShowFilterToolbar }, true);
        MenuItem scripts = OriginalAction("FormBrowse", "ViewScriptsToolbar", "Scripts toolbar", null,
            () => ChangeHistoryViewAsync(p => p with { ShowScriptsToolbar = !p.ShowScriptsToolbar }, false));
        scripts.ToggleType = MenuItemToggleType.CheckBox;
        result.ItemsSource = new[] { main, filters, scripts };
        result.SubmenuOpened += (_, _) => { main.IsChecked = _historyView.ShowMainToolbar; filters.IsChecked = _historyView.ShowFilterToolbar; scripts.IsChecked = _historyView.ShowScriptsToolbar; };
        return result;
    }
}
