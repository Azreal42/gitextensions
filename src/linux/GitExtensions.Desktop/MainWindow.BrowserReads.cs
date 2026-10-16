using Avalonia.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    // FormBrowse.UpdateTargets and RevisionDiffControl's two cancellation sequences:
    // updating a selection is a read of one panel, never a busy repository operation.
    private sealed class BrowserRead
    {
        internal CancellationTokenSource? Current;
        internal int Pending;
        internal void Cancel() { Current?.Cancel(); Current = null; }
    }

    private readonly BrowserRead _revisionRead = new(), _revisionDiffRead = new();
    private readonly BrowserRead _treeRead = new(), _treeFileRead = new();
    private readonly BrowserRead _workingDiffRead = new(), _signatureRead = new();
    private BrowserRead[] BrowserReads => [_revisionRead, _revisionDiffRead, _treeRead, _treeFileRead, _workingDiffRead, _signatureRead, _worktreeMenuRead, _goToCommitRead, _quickReferenceRead];
    private string? _loadedDiffSelection;
    private string RevisionSelectionIdentity => string.Join('\0', SelectedRevisionDiffCommits().Select(commit => commit.Hash));

    private void CancelRevisionReads()
    {
        foreach (BrowserRead read in new[] { _revisionRead, _revisionDiffRead, _treeRead, _treeFileRead, _signatureRead, _quickReferenceRead }) { read.Cancel(); }
        _loadedDiffSelection = null;
    }

    private void CancelBrowserReads()
    {
        if (_revisionRead.Current is not null || _revisionDiffRead.Current is not null) { _loadedDiffSelection = null; }
        foreach (BrowserRead read in BrowserReads) { read.Cancel(); }
    }

    private async Task ReadBrowserAsync(BrowserRead sequence, string label, Func<GitRepository, CancellationToken, Task> action,
        CancellationToken parent = default)
    {
        if (_busy || _repository is not GitRepository repository || _lifetime.IsCancellationRequested) { return; }
        sequence.Cancel();
        using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, parent);
        sequence.Current = read; sequence.Pending++;
        CancellationToken token = read.Token;
        LastException = null;
        try { await action(repository, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested && _repository == repository)
            {
                LastException = exception;
                _state.Text = label + " failed — see command output";
                AppendOutput($"{label}: {exception.Message}");
            }
        }
        finally
        {
            sequence.Pending--;
            if (sequence.Current == read) { sequence.Current = null; }
            if (sequence == _workingDiffRead && !_lifetime.IsCancellationRequested) { UpdatePartialStageActions(); }
        }
    }

    private Task ReadRevisionDiffAsync(TreeViewItem item, CancellationToken parent = default)
        => ReadBrowserAsync(_revisionDiffRead, "File diff", (repository, token) => ShowRevisionDiffItemAsync(repository, item, token), parent);

    private Task ReadCommitFilesAsync(string revision)
        => ReadBrowserAsync(_revisionRead, "Commit diff", (repository, token) => LoadCommitFilesAsync(repository, revision, cancellationToken: token));

    private Task LoadVisibleRevisionPanelAsync()
    {
        if (_commits.SelectedItem is not GitCommit commit) { return Task.CompletedTask; }
        return (_tabs.SelectedItem as TabItem)?.Name switch
        {
            "DiffTab" when _loadedDiffSelection != RevisionSelectionIdentity => ReadCommitFilesAsync(commit.Hash),
            "FileTreeTab" => ReadBrowserAsync(_treeRead, "File tree", async (repository, token) =>
            {
                if (_fileTreeRepository != repository || _fileTreeRevision != commit.Hash)
                {
                    var entries = await repository.GetTreeAsync(commit.Hash, cancellationToken: token);
                    token.ThrowIfCancellationRequested();
                    SetFileTreePaths(repository, commit.Hash, entries);
                }
                if (!_fileGrepSettings.ShowSearchBox || _fileGrepSettings.Expression.Length == 0)
                {
                    if (SelectedTreePath is string path) { _filePath.Text = path; await ShowFileAsync(path, token); }
                    else { ShowSelectedTreeFolder(); }
                }
            }),
            "GpgTab" => ReadBrowserAsync(_signatureRead, "Commit signature", async (repository, token) =>
            {
                GitResult result = await repository.RunAsync(["show", "--no-patch", "--format=Signature status: %G?%nSigner: %GS%nKey: %GK%nFingerprint: %GF", commit.Hash], token);
                token.ThrowIfCancellationRequested();
                _gpgInfo.Text = result.StandardOutput + result.StandardError;
            }),
            _ => Task.CompletedTask
        };
    }
}
