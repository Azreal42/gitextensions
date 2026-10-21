using Avalonia.Controls;
using Avalonia.VisualTree;
using GitCommands.Git;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private sealed record RevisionBisectTarget(GitRepository Repository, string Hash);
    private RevisionBisectTarget? _revisionBisectTarget;
    private MenuItem[] _revisionBisectActions = [];
    private Separator? _revisionBisectSeparator;

    private object[] BuildRevisionBisectMenuItems()
    {
        MenuItem Item(string name, string key, string text, string icon, GitBisectOption? verdict)
        {
            MenuItem item = RevisionAction(name, key, text, () => ContinueRevisionBisectAsync(verdict), icon, worktree: true);
            if (item.Icon is Image image) { image.Tag = icon; }
            return item;
        }
        _revisionBisectActions =
        [
            Item("RevisionBisectBad", "markRevisionAsBadToolStripMenuItem", "Mark revision as bad", "BisectBad.png", GitBisectOption.Bad),
            Item("RevisionBisectGood", "markRevisionAsGoodToolStripMenuItem", "Mark revision as good", "BisectGood.png", GitBisectOption.Good),
            Item("RevisionBisectSkip", "bisectSkipRevisionToolStripMenuItem", "Skip revision", "BisectSkip.png", GitBisectOption.Skip),
            Item("RevisionBisectStop", "stopBisectToolStripMenuItem", "Stop bisect", "BisectStop.png", null)
        ];
        _revisionBisectSeparator = new Separator { Name = "RevisionBisectSeparator" };
        RefreshRevisionBisectMenu();
        return [.. _revisionBisectActions, _revisionBisectSeparator];
    }

    private static bool HasRevisionBisectSession(GitRepository? repository)
        // Match Module.InTheMiddleOfBisect. GitDirectory is already discovered,
        // including linked-worktree metadata; opening the menu starts no Git read.
        => repository?.Info is { IsBare: false } info
            && File.Exists(System.IO.Path.Combine(info.GitDirectory, "BISECT_START"));

    private void CaptureRevisionBisectTarget()
    {
        _revisionBisectTarget = null;
        if (_repository is not GitRepository repository) { return; }
        // A ListBox SelectedItem can remain the anchor of a multiple selection.
        // Capture the actual row before focus transfers into the popup instead.
        GitCommit? selected = (FocusManager?.GetFocusedElement() as Control)?.GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>().FirstOrDefault(row => row.FindAncestorOfType<ListBox>() == _commits)?.DataContext as GitCommit;
        GitCommit[] selection = _commits.SelectedItems?.OfType<GitCommit>().ToArray() ?? [];
        if (selected is null || !selection.Contains(selected))
        {
            selected = selection.FirstOrDefault(commit => commit.Hash == _quickSearchLastCommit)
                ?? _commits.SelectedItem as GitCommit;
        }
        if (selected is not null && selection.Contains(selected)) { _revisionBisectTarget = new(repository, selected.Hash); }
    }

    private void RefreshRevisionBisectMenu()
    {
        bool available = !_busy && !_lifetime.IsCancellationRequested && HasRevisionBisectSession(_repository)
            && _revisionBisectTarget?.Repository == _repository && _commits.SelectedItems?.OfType<GitCommit>().Any() == true;
        foreach (MenuItem item in _revisionBisectActions) { item.IsVisible = item.IsEnabled = available; }
        if (_revisionBisectSeparator is not null) { _revisionBisectSeparator.IsVisible = available; }
    }

    private async Task ContinueRevisionBisectAsync(GitBisectOption? verdict)
    {
        // Menu dismissal may precede Click. This immutable invocation belongs to
        // that opening, not to a later selection, HEAD, refresh or repository.
        RevisionBisectTarget? target = _revisionBisectTarget;
        _revisionBisectTarget = null;
        if (target is null || _busy || _lifetime.IsCancellationRequested || target.Repository != _repository
            || !HasRevisionBisectSession(target.Repository)) { return; }
        bool commandStarted = false, refreshed = false;
        string description = verdict is null ? "git bisect reset" : "git bisect " + verdict.Value.ToString().ToLowerInvariant() + " " + target.Hash;
        OperationFocus historyFocus = CurrentHistoryOperationFocus(new(target.Repository, null, target.Hash, PreserveHistorySelection: true));
        _revisionActionFocus = historyFocus;
        await using var historyMutation = BeginHistoryMutationScope();
        try
        {
            await RunHistoryMutationAsync("Bisect", async repository =>
            {
                if (repository != target.Repository || !HasRevisionBisectSession(repository)) { return; }
                GitProcessDialogResult? outcome = null;
                try
                {
                    // Windows uses dialog settings for grid Stop, but not for verdicts.
                    outcome = await GitProcessDialog.ShowAsync(WorkflowOwner, repository.Path, description, verdict is null, (token, output) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (_repository != target.Repository || !HasRevisionBisectSession(repository))
                        { throw new InvalidOperationException("The bisect session is no longer active."); }
                        commandStarted = true; MarkStandaloneMayHaveRepositoryChanges();
                        return verdict is null ? repository.ResetBisectAsync(cancellationToken: token, outputReceived: output)
                            : repository.MarkBisectAsync(verdict.Value, target.Hash, token, output);
                    }, Token);
                    if (outcome.Result is GitResult result) { AppendOutput(result.StandardOutput + result.StandardError); }
                }
                finally
                {
                    // Native failure/cancel can leave real bisect refs or a changed checkout.
                    // Refresh before reporting it; toolbar cancellation gets a fresh-token fallback below.
                    if (commandStarted && _repository == target.Repository && !_lifetime.IsCancellationRequested && !Token.IsCancellationRequested)
                    { await RefreshDataAsync(repository); refreshed = true; }
                }
                if (outcome?.WasCancelled == true) { throw new OperationCanceledException(); }
                if (outcome?.Error is Exception error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); }
            }, refresh: false);
        }
        finally
        {
            if (commandStarted && !refreshed && _repository == target.Repository && !_lifetime.IsCancellationRequested)
            { await RefreshWithOperationFocusAsync(CurrentHistoryOperationFocus(historyFocus)); }
        }
    }
}
