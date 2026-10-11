using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private string? _comparisonBase;
    private MenuItem? _compareToBaseMenu;

    private MenuItem BuildComparisonMenu()
    {
        _compareToBaseMenu = MenuAction("Compare to BASE", () => ShowComparisonAsync(new(_comparisonBase ?? throw new InvalidOperationException(T("Select a BASE revision first."))), new(CurrentRevision)));
        _compareToBaseMenu.IsEnabled = false;
        return MenuGroup("Compare",
            MenuAction("Open selected commits with difftool", OpenComparisonToolAsync),
            MenuAction("Compare to branch…", CompareToBranchAsync),
            MenuAction("Compare with current branch", () => ShowComparisonAsync(new(CurrentRevision), new("HEAD"))),
            MenuAction("Select as BASE to compare", () =>
            {
                if (_commits.SelectedItem is GitCommit selected) { _comparisonBase = selected.Hash; _compareToBaseMenu.IsEnabled = true; }
                return Task.CompletedTask;
            }),
            _compareToBaseMenu,
            MenuAction("Compare to working directory", () => ShowComparisonAsync(new(CurrentRevision), new("", GitCompareRevisionKind.WorkingTree))),
            MenuAction("Compare selected commits", CompareSelectedCommitsAsync),
            MenuAction("Range diff", ShowSelectedRangeDiffAsync, "DiffR.png"));
    }

    private Task ShowComparisonAsync(GitCompareRevision from, GitCompareRevision to)
        => _repository is null || _busy ? Task.CompletedTask : ShowRepositoryDialogAsync(new CompareRevisionsDialog(_repository, from, to));

    private async Task CompareToBranchAsync()
    {
        if (_repository is null || _busy) { return; }
        GitRepository repository = _repository;
        GitCompareRevision selected = new(CurrentRevision);
        GitReference? branch = null;
        await RunAsync("Compare to branch", async _ => branch = await CompareBranchPicker.ShowAsync(WorkflowOwner, repository, selected, (_references.SelectedItem as GitReference)?.Name), refresh: false);
        if (branch is not null && _repository == repository) { await ShowComparisonAsync(new(branch.Name), selected); }
    }

    private GitCommit[] SelectedComparisonCommits()
    {
        HashSet<string> selected = (_commits.SelectedItems?.OfType<GitCommit>() ?? []).Select(commit => commit.Hash).ToHashSet(StringComparer.Ordinal);
        return _commits.Items.OfType<GitCommit>().Where(commit => selected.Contains(commit.Hash)).ToArray();
    }

    private Task CompareSelectedCommitsAsync()
    {
        GitCommit[] selected = SelectedComparisonCommits();
        if (selected.Length == 1) { return CompareAsync(); }
        if (selected.Length == 0) { _state.Text = T("You must have two commits selected to compare"); return Task.CompletedTask; }
        return ShowComparisonAsync(new(selected[^1].Hash), new(selected[0].Hash));
    }

    private async Task OpenComparisonToolAsync()
    {
        if (_repository is null || _busy) { return; }
        GitRepository originalRepository = _repository;
        GitCommit[] selected = SelectedComparisonCommits();
        string to = selected.FirstOrDefault()?.Hash ?? CurrentRevision;
        string from = selected.Length > 1 ? selected[^1].Hash : selected.FirstOrDefault()?.Parents.FirstOrDefault() ?? to;
        await RunAsync("Open selected commits with difftool", async repository =>
        {
            GitComparison comparison = await repository.GetComparisonAsync(new(from), new(to), Token);
            GitResult result = await repository.OpenComparisonDiffToolAsync(comparison, directory: true, cancellationToken: Token);
            AppendOutput(result.StandardOutput + result.StandardError);
        }, refresh: false);
        if (_repository == originalRepository && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
    }
}
