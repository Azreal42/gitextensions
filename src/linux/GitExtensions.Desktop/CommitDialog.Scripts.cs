using Avalonia.Controls;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

public sealed partial class CommitDialog
{
    private void InitializeFileScripts()
    {
        DesktopScriptExecutor.SetReadingPositionProvider(this, () => (_diff.CurrentFileLine, _diff.Editor.TextArea.Caret.Column));
        DesktopScriptExecutor.SetFileProvider(this, () => CaptureScriptFiles(_viewStaged ? _stagedView : _unstagedView, _viewStaged));
        foreach (var entry in new[] { (View: _unstagedView, Staged: false), (View: _stagedView, Staged: true) })
        {
            // Rules and index flags have already attached their handlers and menu items.
            ContextMenu menu = entry.View.Tree.ContextMenu ??= new() { Name = entry.Staged ? "StagedScriptsMenu" : "UnstagedScriptsMenu" };
            IReadOnlyList<object> ownedScripts = [];
            menu.Opening += (_, _) =>
            {
                FileMenuSelection selection = CaptureScriptFiles(entry.View, entry.Staged);
                object[] scripts = ScriptMenuItems.Build(new ScriptsPreferencesStore().Load().Scripts,
                    script => script.OnEvent == ScriptEvent.ShowInFileList,
                    script => () => RunFileScriptAsync(script, selection), !IsBusy, _translations, "FileStatusList");
                ownedScripts = ScriptMenuItems.ReplaceOwned(menu, ownedScripts, scripts);
            };
        }
    }
    private FileMenuSelection CaptureScriptFiles(FileStatusListView view, bool staged)
    {
        GitCompareRevision first = staged ? new(_status?.Head ?? "", _status?.Head is null ? GitCompareRevisionKind.EmptyTree : GitCompareRevisionKind.Commit)
            : new("", GitCompareRevisionKind.Index);
        GitCompareRevision second = new("", staged ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree);
        GitChange[] changes = view.SelectedTags.OfType<GitChange>().ToArray();
        FileMenuLeaf[] leaves = changes.Select(change => new FileMenuLeaf(
            new(change.Path, staged ? change.IndexStatus : change.WorkTreeStatus, change.OriginalPath), first, second,
            Stage: staged ? FileMenuStage.Index : FileMenuStage.WorkingTree, IsTracked: !change.IsUntracked, IsSubmodule: change.IsSubmodule)).ToArray();
        return new(_repository, leaves, changes.Select(change => change.Path).ToArray(), view.SelectedFolderPath);
    }
    private Task RunFileScriptAsync(ScriptDefinition script, FileMenuSelection selection) => RunAsync(script.GetDisplayName(), async () =>
    {
        ScriptMayHaveChangedRepository = true; HasRepositoryChanges = true;
        try { await DesktopScriptExecutor.ExecuteAsync(this, _repository, script, OperationToken, selection); }
        finally
        {
            if (!script.RunInBackground && !OperationToken.IsCancellationRequested && !_closeWhenIdle)
            { await RefreshDataAsync(); RepositoryChanged?.Invoke(this, EventArgs.Empty); }
        }
    });
}
