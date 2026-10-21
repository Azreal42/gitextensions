using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private GitCompareRevision? _comparisonBase;
    private MenuItem? _compareToBaseMenu;

    private MenuItem BuildComparisonMenu()
    {
        _compareToBaseMenu = MenuAction("Compare to BASE", CompareToBaseHotkeyAsync);
        _compareToBaseMenu.IsEnabled = false;
        return new MenuItem { Name = "RevisionCompare", Header = T("Compare"), Icon = OriginalIconImage("Diff.png"), ItemsSource = new object[] {
            MenuAction("Open selected commits with difftool", OpenComparisonToolAsync),
            new Separator(),
            MenuAction("Compare to branch…", CompareToBranchAsync),
            MenuAction("Compare with current branch", CompareToCurrentBranchAsync),
            MenuAction("Select as BASE to compare", SelectComparisonBaseAsync),
            _compareToBaseMenu,
            MenuAction("Compare to working directory", CompareToWorkingDirectoryAsync),
            MenuAction("Compare selected commits", CompareSelectedCommitsAsync),
            new Separator(), MenuAction("Range diff", ShowSelectedRangeDiffAsync, "DiffR.png") } };
    }

    private Task ShowComparisonAsync(GitCompareRevision from, GitCompareRevision to)
        => _repository is null || _busy ? Task.CompletedTask : ShowRepositoryDialogAsync(new CompareRevisionsDialog(_repository, from, to));

    private Task SelectComparisonBaseAsync()
    {
        if (CurrentRelativeRevision() is { } selected)
        { _comparisonBase = selected; if (_compareToBaseMenu is not null) { _compareToBaseMenu.IsEnabled = true; } }
        return Task.CompletedTask;
    }

    private Task CompareToCurrentBranchAsync() => CurrentRelativeRevision() is { } selected
        ? ShowComparisonAsync(selected, new("HEAD")) : Task.CompletedTask;

    private Task CompareToWorkingDirectoryAsync() => CurrentRelativeRevision() is not { } selected ? Task.CompletedTask
        : selected.Kind == GitCompareRevisionKind.WorkingTree
            ? Dialogs.ShowErrorAsync(this, "Cannot diff working directory to itself")
            : ShowComparisonAsync(selected, new("", GitCompareRevisionKind.WorkingTree));

    private async Task CompareToBranchAsync()
    {
        if (_repository is null || _busy) { return; }
        GitRepository repository = _repository;
        if (CurrentRelativeRevision() is not { } selected) { return; }
        GitReference? branch = null;
        await RunAsync("Compare to branch", async _ => branch = await CompareBranchPicker.ShowAsync(WorkflowOwner, repository, selected, (_references.SelectedItem as GitReference)?.Name), refresh: false);
        if (branch is not null && _repository == repository) { await ShowComparisonAsync(new(branch.Name), selected); }
    }

    private GitCommit[] SelectedComparisonCommits()
    {
        if (SelectedHistoryItems().Any(item => item is ArtificialHistoryRevision)) { return []; }
        HashSet<string> selected = (_commits.SelectedItems?.OfType<GitCommit>() ?? []).Select(commit => commit.Hash).ToHashSet(StringComparer.Ordinal);
        return _commits.Items.OfType<GitCommit>().Where(commit => selected.Contains(commit.Hash)).ToArray();
    }

    private Task CompareSelectedCommitsAsync()
    {
        object[] selected = SelectedHistoryItems();
        if (selected.Length == 1 && selected[0] is GitCommit) { return CompareAsync(); }
        if (selected.Length == 0) { _state.Text = T("You must have two commits selected to compare"); return Task.CompletedTask; }
        GitCompareRevision to = HistoryRevisionItems.Revision(selected[0]);
        GitCompareRevision? from = selected.Length > 1 ? HistoryRevisionItems.Revision(selected[^1]) : RelativeRevisionParents(to).FirstOrDefault();
        return from is null ? Task.CompletedTask : ShowComparisonAsync(from, to);
    }

    private async Task OpenComparisonToolAsync()
    {
        if (_repository is null || _busy) { return; }
        GitRepository originalRepository = _repository;
        GitCompareRevision[] selected = SelectedHistoryRevisions();
        if (selected.Length == 0) { return; }
        GitCompareRevision to = selected[0];
        GitCompareRevision from = selected.Length > 1 ? selected[^1] : RelativeRevisionParents(to).FirstOrDefault() ?? new("", GitCompareRevisionKind.EmptyTree);
        await RunAsync("Open selected commits with difftool", async repository =>
        {
            GitComparison comparison = await repository.GetComparisonAsync(from, to, Token);
            MarkStandaloneMayHaveRepositoryChanges();
            GitResult result = await repository.OpenComparisonDiffToolAsync(comparison, directory: true, cancellationToken: Token);
            AppendOutput(result.StandardOutput + result.StandardError);
        }, refresh: false);
        if (_repository == originalRepository && !_lifetime.IsCancellationRequested) { await RefreshAsync(); }
    }
}
