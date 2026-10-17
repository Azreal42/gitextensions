using Avalonia.Controls;
using GitExtensions.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private FileHistoryBlameController? _fileHistoryInlineBlame;
    private bool _fileHistoryInlineBlameAvailable;

    private Control BuildFileHistoryBlameContent(FileContentView normal)
    {
        FileHistoryBlameController view = new(normal) { Name = "FileHistoryInlineBlameHost" };
        _fileHistoryInlineBlame = view; view.SetAvailable(_fileHistoryInlineBlameAvailable);
        view.InteractionError += message => { if (!_lifetime.IsCancellationRequested) { _state.Text = message; AppendOutput(message); } };
        view.ReloadRequested += async () =>
        {
            if (_repository is not null && SelectedTreePath is { } path && !_busy && !_lifetime.IsCancellationRequested)
            { await ShowFileAsync(path); }
        };
        view.NavigationRequested += NavigateFileHistoryInlineBlameAsync;
        return view;
    }

    private void SetFileHistoryInlineBlameAvailable(bool available)
    { _fileHistoryInlineBlameAvailable = available; _fileHistoryInlineBlame?.SetAvailable(available); }

    private Task<bool> TryLoadFileHistoryInlineBlameAsync(GitRepository repository, string path, GitCompareRevision source,
        CancellationToken token, bool isSubmodule = false)
    {
        return _fileHistoryInlineBlame?.TryLoadAsync(repository, path, source, token, isSubmodule) ?? Task.FromResult(false);
    }

    private Task FileHistoryInlineBlameCompletion => _fileHistoryInlineBlame?.Completion ?? Task.CompletedTask;
    private void CancelFileHistoryInlineBlame(bool showNormal = true) => _fileHistoryInlineBlame?.Cancel(showNormal);
    private Task CloseFileHistoryInlineBlameAsync() => _fileHistoryInlineBlame?.CloseAsync() ?? Task.CompletedTask;

    private async Task NavigateFileHistoryInlineBlameAsync(FileHistoryBlameNavigation target)
    {
        if (_fileHistoryInlineBlame?.IsClosing != false || _repository != target.Repository || _lifetime.IsCancellationRequested || _busy) { return; }
        if (!_history.Any(commit => commit.Hash == target.Hash)) { await ShowFilteredRevisionErrorAsync(target.Hash); return; }
        SelectVisibleRevision(target.Hash, focus: false);
        if (_fileHistoryInlineBlame?.IsClosing != false || _repository != target.Repository || CurrentRevision != target.Hash || _lifetime.IsCancellationRequested) { return; }
        _filePath.Text = target.Path;
        if (target.ShowChanges)
        {
            _tabs.SelectedItem = _tabs.Items.OfType<TabItem>().First(item => item.Name == "DiffTab");
            await ReadCommitFilesAsync(target.Hash);
            if (_fileHistoryInlineBlame?.IsClosing != false || _repository != target.Repository || CurrentRevision != target.Hash || _lifetime.IsCancellationRequested
                || (_tabs.SelectedItem as TabItem)?.Name != "DiffTab") { return; }
            RevisionDiffFileSelection? file = _revisionDiffGroups.SelectMany(group => group.Files.Select(file => new RevisionDiffFileSelection(group, file)))
                .FirstOrDefault(selected => selected.File.Path == target.Path || selected.File.OriginalPath == target.Path);
            if (file is not null) { _revisionFilesView?.SelectTags([file]); }
        }
        else
        {
            _tabs.SelectedItem = _tabs.Items.OfType<TabItem>().First(item => item.Name == "FileTreeTab");
            await LoadVisibleRevisionPanelAsync();
            if (_fileHistoryInlineBlame?.IsClosing != false || _repository != target.Repository || CurrentRevision != target.Hash || _lifetime.IsCancellationRequested
                || (_tabs.SelectedItem as TabItem)?.Name != "FileTreeTab" || _fileTreeRepository != target.Repository
                || _fileTreeSource is not { Kind: GitCompareRevisionKind.Commit } source || source.Revision != target.Hash) { return; }
            _fileHistoryInlineBlame?.SetPosition(target.Repository, target.Hash, target.Path, target.Line);
            _fileTreeView?.SelectTags([target.Path]);
            if (SelectedTreePath == target.Path) { await ShowFileAsync(target.Path); }
            else { _state.Text = T("The revision is not visible with the current history filters.") + " " + target.Path; }
        }
    }
}
