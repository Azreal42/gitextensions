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
    private readonly BrowserRead _workingDiffRead = new(), _signatureRead = new(), _commitInformationRead = new();
    private BrowserRead[] BrowserReads => [_revisionRead, _revisionDiffRead, _treeRead, _treeFileRead, _workingDiffRead, _signatureRead, _commitInformationRead, _worktreeMenuRead, _goToCommitRead, _quickReferenceRead, _referenceSortingRead];
    private Task? _commitInfoLoadTask;
    private bool _commitInfoClosing, _commitInfoCloseReady;
    private Window? _closingCommitInfoOwner;
    internal Task CommitInformationCompletion => _commitInfo.Completion;
    private string? _loadedDiffSelection;
    private string RevisionSelectionIdentity => string.Join('\0', SelectedRevisionDiffCommits().Select(commit => commit.Hash));

    private void CancelRevisionReads()
    {
        foreach (BrowserRead read in new[] { _revisionRead, _revisionDiffRead, _treeRead, _treeFileRead, _signatureRead, _quickReferenceRead, _commitInformationRead }) { read.Cancel(); }
        _commitInfo.Cancel(); _commitInfoLoadTask = null;
        _loadedDiffSelection = null;
    }

    private void CancelBrowserReads()
    {
        if (_revisionRead.Current is not null || _revisionDiffRead.Current is not null) { _loadedDiffSelection = null; }
        foreach (BrowserRead read in BrowserReads) { read.Cancel(); }
        _commitInfo.Cancel(); _commitInfoLoadTask = null;
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
        Task information = LoadVisibleCommitInformationAsync();
        Task panel = (_tabs.SelectedItem as TabItem)?.Name switch
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
        return Task.WhenAll(information, panel);
    }

    private Task LoadVisibleCommitInformationAsync()
    {
        if (_commitInfoClosing || _repository is null || _commits.SelectedItem is not GitCommit
            || _commitInfoPosition == GitCommands.CommitInfoPosition.BelowList && (_tabs.SelectedItem as TabItem)?.Name != "CommitInfoTab")
        { return Task.CompletedTask; }
        // SetRevision invalidates this task, including a refresh of the same OID.
        // Mere tab/placement changes reuse the active or completed read.
        return _commitInfoLoadTask ??= ReadBrowserAsync(_commitInformationRead, "Commit information", (_, token) => _commitInfo.LoadAsync(token));
    }

    private void ClearCommitInformation()
    {
        _commitInformationRead.Cancel(); _commitInfoLoadTask = null;
        _commitInfo.SetMessage("");
    }

    private void InitializeCommitInformation()
    {
        _commitInfo.InteractionError += message => { if (!_commitInfoClosing) { _state.Text = message; AppendOutput(message); } };
        _commitInfo.CommandRequested += async (command, argument) =>
        {
            if (_busy || _commitInfoClosing || _repository is not GitRepository repository) { return; }
            try
            {
                switch (command)
                {
                    case CommitInformationCommand.NavigateRevision when argument is not null: await NavigateCommitInformationAsync(repository, argument, resolveReference: false); break;
                    case CommitInformationCommand.NavigateReference when argument is not null: await NavigateCommitInformationAsync(repository, argument, resolveReference: true); break;
                    case CommitInformationCommand.AddNotes when argument is not null:
                        await ShowRepositoryDialogAsync(new NoteDialog(repository, argument));
                        if (_repository == repository && !_commitInfoClosing && _commits.SelectedItem is GitCommit selected)
                        { UpdateCommitInfo(selected); await LoadVisibleCommitInformationAsync(); }
                        break;
                    case CommitInformationCommand.Backward: await NavigateRevisionHistoryAsync(-1); break;
                    case CommitInformationCommand.Forward: await NavigateRevisionHistoryAsync(1); break;
                }
            }
            catch (Exception exception) { if (!_commitInfoClosing && _repository == repository) { LastException = exception; _state.Text = exception.Message; AppendOutput(exception.Message); } }
        };
        Closing += (_, args) =>
        {
            if (_commitInfoCloseReady) { return; }
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) { _closingCommitInfoOwner = Owner as Window; }
            _commitInfoClosing = true; _commitInformationRead.Cancel(); _commitInfo.BeginClose();
            Task pending = _commitInfo.Completion;
            if (!pending.IsCompleted) { args.Cancel = true; _ = CloseAfterCommitInformationAsync(pending); }
        };
        Closed += (_, _) =>
        {
            _commitInfoCloseReady = true;
            Window? owner = _closingCommitInfoOwner; _closingCommitInfoOwner = null;
            if (owner is not null) { Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (owner.IsVisible) { owner.Close(); } }); }
        };
    }

    private async Task CloseAfterCommitInformationAsync(Task pending)
    {
        try { await pending; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppendOutput("Commit information: " + exception.Message); }
        finally
        {
            _commitInfoCloseReady = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (IsVisible) { Close(); } });
        }
    }

    private async Task NavigateCommitInformationAsync(GitRepository repository, string target, bool resolveReference)
    {
        string? hash = resolveReference ? null : target;
        if (resolveReference)
        {
            await RunAsync("Go to commit", async current =>
            { if (current == repository) { hash = await repository.ResolveRevisionAsync(target, Token); } }, refresh: false);
        }
        if (_commitInfoClosing || _repository != repository || hash is null) { return; }
        if (_history.Any(commit => commit.Hash == hash)) { SelectVisibleRevision(hash); }
        else { await ShowFilteredRevisionErrorAsync(hash); }
    }
}
