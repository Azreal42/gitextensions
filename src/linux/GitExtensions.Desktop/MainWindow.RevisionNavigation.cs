using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly List<string> _revisionNavigation = [];
    private int _revisionNavigationIndex = -1;
    private GitRepository? _revisionNavigationRepository;
    private bool _movingRevisionNavigation;

    private MenuItem BuildRevisionNavigateMenu()
    {
        MenuItem Item(string name, string label, Func<Task> action, string? icon = null, Func<bool>? available = null)
            => RevisionAction("RevisionNavigate" + name, name, label, action, icon, available: available);
        return new MenuItem
        {
            Name = "RevisionNavigate", Header = RevisionHeader("navigateToolStripMenuItem", "&Navigate"), Icon = OriginalIconImage("GotoCommit.png"),
            ItemsSource = new object[]
            {
                Item("Current", "Go to current revision", () => NavigateToRevisionAsync("HEAD"), "GotoCurrentRevision.png"),
                Item("Commit", "Go to commit...", GoToRevisionAsync, "GotoCommit.png"), new Separator(),
                Item("Child", "Go to child commit", () => { SelectRelativeCommit(false); return Task.CompletedTask; }, "GoToChildCommit.png",
                    () => _commits.SelectedItem is GitCommit commit && _history.Any(child => child.Parents.Contains(commit.Hash))),
                Item("Parent", "Go to parent commit", () => { SelectRelativeCommit(true); return Task.CompletedTask; }, "GoToParentCommit.png", HasRevisionParent),
                Item("FirstParent", "Go to first parent commit", () => { SelectRelativeCommit(true); return Task.CompletedTask; }, "GoToFirstParentCommit.png", HasRevisionParent),
                Item("LastParent", "Go to last parent commit", () => { SelectRelativeCommit(true, true); return Task.CompletedTask; }, "GoToLastParentCommit.png", HasRevisionParent),
                Item("MergeBase", "Go to common ancestor (merge base)", NavigateToMergeBaseAsync, "GoToMergeBaseCommit.png", () => SelectedComparisonCommits().Length >= 2), new Separator(),
                Item("Backward", "Navigate backward", () => NavigateRevisionHistoryAsync(-1), "NavigateBackward.png", () => _revisionNavigationIndex > 0),
                Item("Forward", "Navigate forward", () => NavigateRevisionHistoryAsync(1), "NavigateForward.png", () => _revisionNavigationIndex >= 0 && _revisionNavigationIndex + 1 < _revisionNavigation.Count), new Separator(),
                Item("Search", "Quick search", () => { FocusHistoryFilter(); return Task.CompletedTask; }),
                Item("SearchPrevious", "Quick search previous", () => SearchRevisionAsync(-1)),
                Item("SearchNext", "Quick search next", () => SearchRevisionAsync(1))
            }
        };
    }

    private bool HasRevisionParent() => _commits.SelectedItem is GitCommit { Parents.Count: > 0 };

    private void RememberRevisionNavigation()
    {
        if (_refreshing || _movingRevisionNavigation || _commits.SelectedItem is not GitCommit commit) { return; }
        if (_revisionNavigationRepository != _repository)
        { _revisionNavigation.Clear(); _revisionNavigationIndex = -1; _revisionNavigationRepository = _repository; }
        if (_revisionNavigationIndex >= 0 && _revisionNavigation[_revisionNavigationIndex] == commit.Hash) { return; }
        _revisionNavigation.RemoveRange(_revisionNavigationIndex + 1, _revisionNavigation.Count - _revisionNavigationIndex - 1);
        _revisionNavigation.Add(commit.Hash); _revisionNavigationIndex = _revisionNavigation.Count - 1;
    }

    private async Task GoToRevisionAsync()
    {
        string[]? values = await Dialogs.PromptAsync(WorkflowOwner, T("Go to commit"), (T("Commit"), ""));
        if (values is not null && !string.IsNullOrWhiteSpace(values[0])) { await NavigateToRevisionAsync(values[0]); }
    }

    private async Task NavigateToRevisionAsync(string revision)
    {
        GitRepository? repository = _repository; string? hash = null;
        await RunAsync("Go to commit", async r => hash = await r.ResolveRevisionAsync(revision, Token), refresh: false);
        if (_repository != repository || hash is null) { return; }
        SelectVisibleRevision(hash);
    }

    private void SelectVisibleRevision(string hash)
    {
        GitCommit? row = _history.FirstOrDefault(commit => commit.Hash == hash);
        if (row is null) { _state.Text = T("The revision is not visible with the current history filters.") + " " + hash; return; }
        _commits.SelectedItems?.Clear(); _commits.SelectedItem = row; _commits.ScrollIntoView(row);
    }

    private async Task NavigateToMergeBaseAsync()
    {
        GitCommit[] selected = SelectedComparisonCommits();
        if (selected.Length < 2) { return; }
        string? hash = null; GitRepository? repository = _repository;
        await RunAsync("Merge base", async r =>
        {
            GitResult result = await r.RunAsync(new[] { "merge-base", "--octopus" }.Concat(selected.Select(commit => commit.Hash)).ToArray(), Token);
            if (!result.Success) { throw new GitCommandException(["merge-base"], result); }
            hash = result.StandardOutput.Trim();
        }, refresh: false);
        if (_repository == repository && !string.IsNullOrEmpty(hash)) { SelectVisibleRevision(hash); }
    }

    private Task NavigateRevisionHistoryAsync(int direction)
    {
        int next = _revisionNavigationIndex + direction;
        if (_revisionNavigationRepository != _repository || next < 0 || next >= _revisionNavigation.Count) { return Task.CompletedTask; }
        _movingRevisionNavigation = true;
        try { _revisionNavigationIndex = next; SelectVisibleRevision(_revisionNavigation[next]); }
        finally { _movingRevisionNavigation = false; RefreshRevisionContextMenu(); }
        return Task.CompletedTask;
    }

    private Task SearchRevisionAsync(int direction)
    {
        string text = _quickHistoryFilter.Text ?? "";
        GitCommit[] rows = _history.ToArray();
        if (text.Length == 0 || rows.Length == 0) { FocusHistoryFilter(); return Task.CompletedTask; }
        int index = Array.FindIndex(rows, commit => commit.Hash == CurrentRevision);
        for (int step = 1; step <= rows.Length; step++)
        {
            GitCommit candidate = rows[((index + direction * step) % rows.Length + rows.Length) % rows.Length];
            if (candidate.Subject.Contains(text, StringComparison.CurrentCultureIgnoreCase) || candidate.Hash.Contains(text, StringComparison.OrdinalIgnoreCase)
                || candidate.AuthorName.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            { SelectVisibleRevision(candidate.Hash); break; }
        }
        return Task.CompletedTask;
    }

    private MenuItem BuildRevisionViewMenu()
    {
        MenuItem[] branches = HistoryScopeChoices("RevisionContext");
        string[] labels = ["Show all branches", "Show current branch only", "Show filtered branches"];
        for (int i = 0; i < branches.Length; i++) { branches[i].Header = T(labels[i]); }
        return new MenuItem
        {
            Name = "RevisionView", Header = T("View"), Icon = OriginalIconImage("AdvancedSettings.png"), ItemsSource = new object[]
            {
                branches[0], branches[1], branches[2], new Separator(),
                RevisionAction("RevisionAdvancedFilter", "AdvancedFilter", "Advanced filter...", FilterHistoryAsync), new Separator(),
                new MenuItem { Header = T("Settings persistence"), IsEnabled = false }, SaveViewSettingsMenu()
            }
        };
    }
}
