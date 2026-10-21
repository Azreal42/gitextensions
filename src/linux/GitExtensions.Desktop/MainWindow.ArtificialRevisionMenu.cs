using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private MenuItem ArtificialRevisionAction(string name, string key, string text, Func<Task> action, string icon)
    {
        MenuItem item = new() { Name = name, Header = RevisionHeader(key, text), Icon = OriginalIconImage(icon) };
        item.Click += async (_, args) =>
        {
            if (args.Source != item || !item.IsEnabled || _busy || _repository is not { IsBare: false }
                || (_revisionActionItem ?? RevisionContextItem) is not ArtificialHistoryRevision) { return; }
            try { await WithRevisionActionFocusAsync(action); }
            catch (Exception error) { AppendOutput(error.Message); _state.Text = error.Message; }
        };
        return item;
    }

    private async Task ResetArtificialRevisionAsync()
    {
        if (_repository is not { IsBare: false } repository || (_revisionActionItem ?? RevisionContextItem) is not ArtificialHistoryRevision revision) { return; }
        GitCompareRevisionKind kind = revision.Kind;
        ResetChangesDialog.Decision decision = ResetChangesDialog.Decision.Cancel;
        bool confirmed = false;
        await RunAsync(T("Reset changes"), async current =>
        {
            if (!ReferenceEquals(current, repository)) { return; }
            GitStatus status = await repository.GetStatusAsync(Token);
            Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(repository, _repository)) { return; }
            // Windows bases both confirmations on the complete worktree status, including intent-to-add.
            GitChange[] worktree = status.Changes.Where(change => !change.IsIgnored && change.WorkTreeStatus != '.').ToArray();
            bool New(GitChange change) => change.WorkTreeStatus is 'A' or '?';
            ResetChangesDialog dialog = new(worktree.Any(change => !New(change)), worktree.Any(New),
                T("Are you sure you want to reset your changes?")) { RequestedThemeVariant = ActualThemeVariant };
            try { decision = await dialog.ShowDialog<ResetChangesDialog.Decision>(WorkflowOwner); }
            finally { dialog.Close(); }
            Token.ThrowIfCancellationRequested();
            confirmed = ReferenceEquals(repository, _repository);
        }, refresh: false);
        if (!confirmed || decision == ResetChangesDialog.Decision.Cancel || LastException is not null || _lifetime.IsCancellationRequested
            || !IsVisible || !ReferenceEquals(repository, _repository)) { return; }
        await RunFileMenuOperationAsync(T("Reset changes"), token => repository.ResetArtificialRevisionAsync(kind,
            decision == ResetChangesDialog.Decision.ResetAndDelete, token), changes: true);
    }
}
