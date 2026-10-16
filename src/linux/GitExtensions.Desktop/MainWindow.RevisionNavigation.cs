using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private readonly List<string> _revisionNavigation = [];
    private int _revisionNavigationIndex = -1;
    private GitRepository? _revisionNavigationRepository;
    private bool _movingRevisionNavigation;

    private MenuItem BuildRevisionNavigateMenu() => BuildHistoryNavigateMenu(context: true);

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
        _commits.Focus();
    }

    private async Task NavigateToMergeBaseAsync()
    {
        GitCommit[] selected = SelectedComparisonCommits();
        if (selected.Length == 0) { return; }
        string? hash = null; GitRepository? repository = _repository;
        await RunAsync("Merge base", async r =>
        {
            string[] revisions = selected.Length == 1 ? [selected[0].Hash, "HEAD"] : selected.Select(commit => commit.Hash).ToArray();
            GitResult result = await r.RunAsync(new[] { "merge-base", "--octopus" }.Concat(revisions).ToArray(), Token);
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

    private MenuItem BuildRevisionViewMenu() => BuildHistoryViewMenu(context: true);
}
