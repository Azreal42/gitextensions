using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private bool _selectingFileContext;
    private bool _fileContextNeedsDetails;
    private readonly List<string> _temporaryFileDirectories = [];
    private sealed record FileMenuSelection(GitRepository Repository, string Revision, string? Parent, GitRevisionFile[] Files, string[] CopyPaths, string? Folder)
    {
        internal string[] Paths => Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        internal string[] ResetPaths => Files.SelectMany(file => file.Status == 'R' && file.OriginalPath is not null
            ? new[] { file.Path, file.OriginalPath } : [file.Path]).Distinct(StringComparer.Ordinal).ToArray();
    }

    private FileMenuSelection? GetFileMenuSelection(bool isFileTree)
    {
        if (_repository is not GitRepository repository) { return null; }
        TreeView tree = isFileTree ? _tree : _revisionFiles;
        FileStatusListView? view = isFileTree ? _fileTreeView : _revisionFilesView;
        GitRevisionFile[] files;
        string revision;
        string? parent;
        if (isFileTree)
        {
            if (_fileTreeRevision is not string selectedRevision) { return null; }
            revision = selectedRevision;
            parent = _history.FirstOrDefault(commit => commit.Hash == revision)?.Parents.FirstOrDefault();
            files = view!.SelectedTags.OfType<string>().Select(path => new GitRevisionFile(path, ' ')).ToArray();
        }
        else
        {
            RevisionDiffFileSelection[] selected = view!.SelectedTags.OfType<RevisionDiffFileSelection>().ToArray();
            // A reset or temporary-file action must never silently mix different comparisons.
            if (selected.Length == 0 || selected.Select(item => (item.Group.Revision, item.Group.BaseRevision)).Distinct().Count() != 1) { return null; }
            revision = selected[0].Group.Revision; parent = selected[0].Group.BaseRevision;
            files = selected.Select(item => item.File).DistinctBy(file => file.Path).ToArray();
        }
        string[] copy = tree.SelectedItems!.OfType<TreeViewItem>().Select(item => item.Tag switch
        {
            string path => path,
            RevisionDiffFileSelection selected => selected.File.Path,
            FileStatusListNodeTag { Kind: FileStatusListNodeKind.Folder } folder => folder.Key,
            _ => null
        }).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (copy.Length == 0) { copy = files.Select(file => file.Path).ToArray(); }
        return new(repository, revision, parent, files, copy, view!.SelectedFolderPath);
    }

    private void ConfigureFileContextMenu(TreeView tree, bool isFileTree)
    {
        ContextMenu menu = new() { Name = isFileTree ? "FileTreeContextMenu" : "RevisionFilesContextMenu" };
        tree.ContextMenu = menu;
        tree.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(tree).Properties.IsRightButtonPressed) { return; }
            TreeViewItem? item = (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            if (item is null) { return; }
            _selectingFileContext = true;
            if (!tree.SelectedItems!.Contains(item)) { _fileContextNeedsDetails = true; tree.SelectedItem = item; }
            item.Focus();
        }, RoutingStrategies.Tunnel);
        menu.Opening += (_, _) =>
        {
            _selectingFileContext = true;
            menu.ItemsSource = BuildFileMenuItems(isFileTree, GetFileMenuSelection(isFileTree));
        };
        menu.Closed += (_, _) =>
        {
            _selectingFileContext = false;
            if (!_fileContextNeedsDetails) { return; }
            _fileContextNeedsDetails = false;
            Dispatcher.UIThread.Post(async () =>
            {
                if (_busy || _refreshing || _lifetime.IsCancellationRequested) { return; }
                if (isFileTree && SelectedTreePath is string path) { _filePath.Text = path; await ShowSelectedTreeFileAsync(path); }
                else if (isFileTree) { ShowSelectedTreeFolder(); }
                else if (!isFileTree && tree.SelectedItem is TreeViewItem selected)
                { await ReadRevisionDiffAsync(selected); }
            });
        };
        tree.AddHandler(InputElement.DoubleTappedEvent, async (_, args) =>
        {
            if (GetFileMenuSelection(isFileTree) is { Files.Length: 1 } selection && (args.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.Tag is not FileStatusListNodeTag)
            { args.Handled = true; await ShowFileHistoryAsync(selection); }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        Closed += (_, _) =>
        {
            foreach (string directory in _temporaryFileDirectories)
            { try { Directory.Delete(directory, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
        };
    }

    private object[] BuildFileMenuItems(bool isFileTree, FileMenuSelection? selection)
    {
        bool valid = selection is { Files.Length: > 0 } && !_busy;
        bool single = valid && selection!.Files.Length == 1 && selection.Folder is null;
        bool folderSelected = valid && selection!.Folder is not null;
        bool blob = single && (!isFileTree || _fileTreeEntries.GetValueOrDefault(selection!.Files[0].Path)?.IsSubmodule != true);
        bool worktree = valid && !selection!.Repository.IsBare;
        bool exists = single && worktree && File.Exists(Path.Combine(selection!.Repository.Path, selection.Paths[0]));
        MenuItem Item(string name, string label, string? icon, Func<FileMenuSelection, Task> action, bool enabled = true, KeyGesture? gesture = null)
        {
            MenuItem item = OriginalAction("FileStatusList", name, label, icon,
                () => selection is not null && _repository == selection.Repository && !_busy ? action(selection) : Task.CompletedTask, gesture);
            item.IsEnabled = enabled; return item;
        }
        MenuItem reset = Item("tsmiResetFileTo", "&Reset file(s) to", "ResetWorkingDirChanges.png", _ => Task.CompletedTask, worktree);
        reset.Items.Add(Item("tsmiResetFileToSelected", T("Second: B ") + ShortRevision(selection?.Revision), null,
            s => ResetFileSelectionAsync(s, s.Revision), worktree));
        if (selection?.Parent is string parent)
        { reset.Items.Add(Item("tsmiResetFileToParent", T("First: A ") + ShortRevision(parent), null, s => ResetFileSelectionAsync(s, parent), worktree)); }
        MenuItem diff = Item("tsmiOpenWithDifftool", "Open with &difftool", "Diff.png", _ => Task.CompletedTask, worktree);
        if (selection?.Parent is string first)
        { diff.Items.Add(Item("tsmiDiffFirstToSelected", "&First -> Second", null, s => OpenFileDiffToolAsync(s, first, workingTree: false), worktree)); }
        diff.Items.Add(Item("tsmiDiffSelectedToLocal", "&Second -> Working directory", null, s => OpenFileDiffToolAsync(s, s.Revision, workingTree: true), worktree));
        if (selection?.Parent is string baseRevision)
        { diff.Items.Add(Item("tsmiDiffFirstToLocal", "First -> &Working directory", null, s => OpenFileDiffToolAsync(s, baseRevision, workingTree: true), worktree)); }
        MenuItem copy = Item("CopyPathsToolStripMenuItem", "Copy &path(s)", "CopyToClipboard.png", _ => Task.CompletedTask, selection?.CopyPaths.Length > 0);
        MenuItem Copy(string name, string label, bool full, KeyGesture? gesture = null)
        {
            MenuItem item = OriginalAction("CopyPathsToolStripMenuItem", name, label, null,
                () => CopyFilePathsAsync(selection, full), gesture);
            item.IsEnabled = selection?.CopyPaths.Length > 0; return item;
        }
        copy.ItemsSource = new object[]
        {
            Copy("copyRelativePathsPosixToolStripMenuItem", "Copy relative path(s) - &POSIX", false),
            Copy("copyRelativePathsNativeToolStripMenuItem", "Copy relative path(s) - &native", false),
            Copy("copyFullPathsNativeToolStripMenuItem", "Copy &full path(s) - native", true, new(Key.C, KeyModifiers.Control))
        };
        ((MenuItem)copy.Items[2]!).FontWeight = FontWeight.Bold;
        List<object> items = [reset, new Separator(), diff];
        if (exists)
        {
            items.Add(Item("tsmiOpenWorkingDirectoryFile", "&Open working directory file", "EditFile.png", s => OpenWorkingFileAsync(s, false), gesture: new(Key.F4, KeyModifiers.Shift)));
            items.Add(Item("tsmiOpenWorkingDirectoryFileWith", "Open working directory file with...", "EditFile.png", s => OpenWorkingFileAsync(s, true), gesture: new(Key.F4, KeyModifiers.Control | KeyModifiers.Shift)));
        }
        items.Add(Item("tsmiOpenRevisionFile", "Ope&n this revision (temp file)", "ViewFile.png", s => OpenRevisionFileAsync(s, false), blob && selection!.Files[0].Status != 'D', new(Key.F3, KeyModifiers.Control)));
        items.Add(Item("tsmiOpenRevisionFileWith", "Open this revision &with... (temp file)", "ViewFile.png", s => OpenRevisionFileAsync(s, true), blob && selection!.Files[0].Status != 'D', new(Key.F3, KeyModifiers.Control | KeyModifiers.Shift)));
        if (exists) { items.Add(Item("tsmiEditWorkingDirectoryFile", "&Edit working directory file", "EditFile.png", s => OpenWorkingFileAsync(s, false), gesture: new(Key.F4))); }
        items.Add(Item("tsmiSaveAs", "S&ave selected as...", "SaveAs.png", SaveFileSelectionAsync, valid && (!isFileTree || selection!.Files.All(file => _fileTreeEntries.GetValueOrDefault(file.Path)?.IsSubmodule != true)) && selection!.Files.All(file => file.Status != 'D'), new(Key.S, KeyModifiers.Control)));
        if (exists || folderSelected && worktree) { items.Add(Item("tsmiMove", "Rena&me / move", null, RenameFileSelectionAsync, true, new(Key.F2))); }
        items.Add(new Separator()); items.Add(copy);
        items.Add(Item("tsmiShowInFolder", "Show &in folder", "BrowseFileExplorer.png", ShowFileFolderAsync, worktree));
        items.Add(new Separator());
        if (!isFileTree) { items.Add(Item("tsmiShowInFileTree", "Show in File &tree", "FileTree.png", ShowSelectionInFileTreeAsync, single && selection!.Files[0].Status != 'D')); }
        items.Add(Item("tsmiFilterFileInGrid", "Filter file in grid", "FunnelPencil.png", FilterFileInHistoryAsync, single || folderSelected, new(Key.F)));
        MenuItem history = Item("tsmiFileHistory", "File &history", "FileHistory.png", ShowFileHistoryAsync, single, new(Key.H));
        history.FontWeight = FontWeight.Bold; items.Add(history);
        items.Add(Item("tsmiBlame", "&Blame", "Blame.png", ShowFileBlameAsync, blob && selection!.Files[0].Status != 'D', new(Key.B)));
        MenuItem find = OriginalAction("FileStatusList", "tsmiFindFile", "&Find file...", null, () => FindFileInPaneAsync(isFileTree));
        find.IsEnabled = _repository is not null && !_busy; items.Add(find);
        items.Add(OriginalAction("FileStatusList", "tsmiOpenFindInCommitFilesGitGrepDialog", "Find in &commit files using git-grep...", "ViewFile.png", isFileTree ? ShowFileGrepDialogAsync : OpenFileGrepFromDiffAsync));
        MenuItem showGrep = OriginalAction("FileStatusList", "tsmiShowFindInCommitFilesGitGrep", "Show 'Find in commit fi&les using git-grep'", null, () =>
        {
            UpdateFileGrepSettings(_fileGrepSettings with { ShowSearchBox = !_fileGrepSettings.ShowSearchBox });
            FocusDetail("FileTreeTab"); return Task.CompletedTask;
        });
        showGrep.ToggleType = MenuItemToggleType.CheckBox; showGrep.IsChecked = _fileGrepSettings.ShowSearchBox; items.Add(showGrep);
        if (blob && worktree)
        { items.Add(new Separator()); items.Add(Item("tsmiStopTracking", "Stop tracking this file", "StopTrackingFile.png", StopTrackingFileAsync)); }
        TreeView tree = isFileTree ? _tree : _revisionFiles;
        TreeViewItem[] nodes = tree.SelectedItems!.OfType<TreeViewItem>().ToArray();
        if (nodes.Any(node => node.Items.Count > 0))
        {
            MenuItem TreeAction(string name, string caption, string icon, Action action)
                => OriginalAction("FileStatusList", name, caption, icon, () => { action(); return Task.CompletedTask; });
            void SetExpanded(TreeViewItem item, bool expanded)
            {
                item.IsExpanded = expanded;
                foreach (TreeViewItem child in item.Items.OfType<TreeViewItem>()) { SetExpanded(child, expanded); }
            }
            int index = items.IndexOf(find);
            items.InsertRange(index, new object[]
            {
                TreeAction("FileTreeSelectAll", "S&elect all", "FileTree.png", tree.SelectAll),
                TreeAction("FileTreeCollapseAll", "C&ollapse all", "CollapseAll.png", () => { foreach (TreeViewItem node in nodes) { SetExpanded(node, false); } }),
                TreeAction("FileTreeExpandAll", "E&xpand all", "ExpandAll.png", () => { foreach (TreeViewItem node in nodes) { SetExpanded(node, true); } }),
                new Separator()
            });
        }
        if (isFileTree && tree.Items.OfType<TreeViewItem>().Any(item => item.IsExpanded))
        {
            items.Add(OriginalAction("FileStatusList", "FileTreeCollapseRootFolders", "Collap&se root folders", "TreeCollapseAll.png", () =>
            {
                TreeViewItem? selected = tree.SelectedItem as TreeViewItem;
                TreeViewItem? root = tree.Items.OfType<TreeViewItem>().FirstOrDefault(item => item == selected || FileStatusTreeItems(item.Items.OfType<TreeViewItem>()).Contains(selected));
                if (root is not null) { tree.SelectedItem = root; }
                foreach (TreeViewItem item in tree.Items.OfType<TreeViewItem>()) { item.IsExpanded = false; }
                return Task.CompletedTask;
            }));
        }
        return items.ToArray();
    }

    private static string ShortRevision(string? revision) => revision is null ? "" : revision[..Math.Min(8, revision.Length)];
    private Task CopyFilePathsAsync(FileMenuSelection? selection, bool full)
        => selection is not null && Clipboard is { } clipboard ? clipboard.SetTextAsync(string.Join(Environment.NewLine,
            selection.CopyPaths.Select(path => full ? Path.Combine(selection.Repository.Path, path) : path))) : Task.CompletedTask;

    private Task LaunchFileAsync(string path, string application, string workingDirectory)
    {
        ExternalLauncher(new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = workingDirectory, ArgumentList = { path } });
        return Task.CompletedTask;
    }
    private async Task<string?> ChooseFileApplicationAsync()
    {
        string[]? values = await Dialogs.PromptAsync(WorkflowOwner, T("Open with"), (T("Application executable"), "/usr/bin/"));
        if (values is null) { return null; }
        string executable = values[0];
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) { throw new ArgumentException(T("Select an existing application executable.")); }
        return executable;
    }
    private async Task OpenWorkingFileAsync(FileMenuSelection selection, bool withApplication)
    {
        string? application = withApplication ? await ChooseFileApplicationAsync() : "xdg-open";
        if (application is not null) { await LaunchFileAsync(Path.Combine(selection.Repository.Path, selection.Paths.Single()), application, selection.Repository.Path); }
    }
    private async Task OpenRevisionFileAsync(FileMenuSelection selection, bool withApplication)
    {
        string? application = withApplication ? await ChooseFileApplicationAsync() : "xdg-open";
        if (application is null) { return; }
        await RunAsync("Open this revision", async repository =>
        {
            byte[] bytes = await repository.GetFileBytesAsync(selection.Paths.Single(), selection.Revision, Token);
            string directory = Path.Combine(Path.GetTempPath(), "gitextensions-file-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
            else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            _temporaryFileDirectories.Add(directory);
            string file = Path.Combine(directory, Path.GetFileName(selection.Paths.Single()));
            await File.WriteAllBytesAsync(file, bytes, Token);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            await LaunchFileAsync(file, application, repository.Path);
        }, refresh: false);
    }
    private async Task SaveFileSelectionAsync(FileMenuSelection selection)
    {
        if (selection.Files.Length == 1)
        {
            IStorageFile? target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = T("Save selected as..."), SuggestedFileName = Path.GetFileName(selection.Paths[0]), ShowOverwritePrompt = true });
            if (target is null) { return; }
            await RunAsync("Save selected as", async repository =>
            {
                byte[] bytes = await repository.GetFileBytesAsync(selection.Paths[0], selection.Revision, Token);
                await using Stream output = await target.OpenWriteAsync(); output.SetLength(0); await output.WriteAsync(bytes, Token);
            }, refresh: false);
        }
        else
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("Save selected as..."), AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not string directory) { return; }
            await RunAsync("Save selected as", async repository =>
            {
                foreach (string path in selection.Paths)
                {
                    string destination = Path.Combine(directory, path);
                    if (File.Exists(destination) || Directory.Exists(destination)) { throw new IOException("Destination already exists: " + destination); }
                    for (string? parent = Path.GetDirectoryName(destination); parent is not null && parent != directory; parent = Path.GetDirectoryName(parent))
                    {
                        if (new DirectoryInfo(parent).LinkTarget is not null) { throw new IOException("Destination is beneath a symbolic link: " + destination); }
                    }
                }
                foreach (string path in selection.Paths)
                {
                    byte[] bytes = await repository.GetFileBytesAsync(path, selection.Revision, Token);
                    string destination = Path.Combine(directory, path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using FileStream output = new(destination, FileMode.CreateNew); await output.WriteAsync(bytes, Token);
                }
            }, refresh: false);
        }
    }
    private Task OpenFileDiffToolAsync(FileMenuSelection selection, string first, bool workingTree)
        => RunAsync("Open with difftool", async repository =>
        {
            GitComparison comparison = new(new(first), new(selection.Revision, workingTree ? GitCompareRevisionKind.WorkingTree : GitCompareRevisionKind.Commit), selection.Files, []);
            foreach (GitRevisionFile file in selection.Files) { await repository.OpenComparisonDiffToolAsync(comparison, file, cancellationToken: Token); }
        }, refresh: false);
    private async Task ResetFileSelectionAsync(FileMenuSelection selection, string revision)
    {
        HashSet<string> source = (await selection.Repository.GetTreeAsync(revision, cancellationToken: _lifetime.Token)).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        ResetChangesDialog dialog = new(selection.ResetPaths.Any(source.Contains), selection.ResetPaths.Any(path => !source.Contains(path)),
            T("Are you sure you want to reset all selected files to") + " " + ShortRevision(revision) + "?\n\n" + string.Join('\n', selection.ResetPaths)) { RequestedThemeVariant = ActualThemeVariant };
        ResetChangesDialog.Decision decision = await dialog.ShowDialog<ResetChangesDialog.Decision>(WorkflowOwner);
        if (decision == ResetChangesDialog.Decision.Cancel || _repository != selection.Repository || _busy) { return; }
        await RunAsync("Reset files", repository => repository.RestoreRevisionFilesAsync(revision, selection.ResetPaths, decision == ResetChangesDialog.Decision.ResetAndDelete, Token));
    }
    private async Task RenameFileSelectionAsync(FileMenuSelection selection)
    {
        string path = selection.Folder ?? selection.Paths.Single();
        string[]? value = await Dialogs.PromptAsync(WorkflowOwner, T("Rename / move"), (T("New name"), path));
        if (value is null || _repository != selection.Repository || _busy) { return; }
        await RunAsync("Rename / move", repository => repository.MoveTrackedFileAsync(path, value[0], Token));
    }
    private async Task StopTrackingFileAsync(FileMenuSelection selection)
    {
        if (!await Dialogs.ConfirmAsync(WorkflowOwner, T("Stop tracking this file"), T("Stop tracking this file") + "?\n\n" + selection.Paths.Single()) || _repository != selection.Repository || _busy) { return; }
        await RunAsync("Stop tracking this file", repository => repository.StopTrackingFileAsync(selection.Paths.Single(), Token));
    }
    private Task ShowFileFolderAsync(FileMenuSelection selection)
    {
        string full = Path.Combine(selection.Repository.Path, selection.CopyPaths.FirstOrDefault() ?? selection.Paths[0]);
        string directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        while (!Directory.Exists(directory) && Path.GetDirectoryName(directory) is string parent) { directory = parent; }
        return LaunchFileAsync(directory, "xdg-open", selection.Repository.Path);
    }
    private Task ShowFileHistoryAsync(FileMenuSelection selection) => RunAsync("File history", async repository =>
    {
        FileHistoryDialog dialog = new(repository, selection.Folder ?? selection.Paths.Single(), selection.Revision) { RequestedThemeVariant = ActualThemeVariant };
        dialog.Opened += async (_, _) => await dialog.InitializeAsync(); await dialog.ShowDialog(WorkflowOwner);
    }, refresh: false);
    private Task ShowFileBlameAsync(FileMenuSelection selection) => RunAsync("Blame", async repository =>
    {
        string path = selection.Paths.Single();
        string identity = string.Join('\0', repository.Path, path, selection.Revision, "blob");
        bool sameFile = _fileContent is { } content && content.TextView.IsDocumentReady && content.TextView.DocumentIdentity == identity;
        BlameDialog dialog = new(repository, path, selection.Revision, sameFile ? _fileContent!.TextView.CurrentFileLine : 1,
            fileEncodingName: sameFile ? _fileContent!.TextView.FileEncodingName : null) { RequestedThemeVariant = ActualThemeVariant };
        dialog.Opened += async (_, _) => await dialog.InitializeAsync(); await dialog.ShowDialog(WorkflowOwner);
    }, refresh: false);
    private Task FilterFileInHistoryAsync(FileMenuSelection selection)
    { _historyQuery = _historyQuery with { Path = selection.Folder ?? selection.Paths.Single(), Skip = 0 }; return RefreshAsync(); }
    private async Task ShowSelectionInFileTreeAsync(FileMenuSelection selection)
    {
        SetFileTreePaths(selection.Repository, selection.Revision, await selection.Repository.GetTreeAsync(selection.Revision, cancellationToken: _lifetime.Token));
        FocusDetail("FileTreeTab"); _fileTreeView!.SelectTags([selection.Paths.Single()]);
    }
    private async Task FindFileInPaneAsync(bool isFileTree)
    {
        string[]? value = await Dialogs.PromptAsync(WorkflowOwner, T("Find file..."), (T("File name"), ""));
        if (value is null) { return; }
        FileStatusListView? view = isFileTree ? _fileTreeView : _revisionFilesView;
        view!.FilterText = Regex.Escape(value[0]); view.Tree.Focus();
    }
    private async Task OpenFileGrepFromDiffAsync()
    { FocusDetail("FileTreeTab"); await ShowFileGrepDialogAsync(); }

    private Task<bool> HandleFilePaneShortcutAsync(KeyEventArgs args)
    {
        if ((args.Key, args.KeyModifiers) is not ((Key.F4, KeyModifiers.None or KeyModifiers.Shift or (KeyModifiers.Control | KeyModifiers.Shift))
            or (Key.F3, KeyModifiers.Control or (KeyModifiers.Control | KeyModifiers.Shift)) or (Key.S or Key.C, KeyModifiers.Control)
            or (Key.F2 or Key.F or Key.H or Key.B, KeyModifiers.None))) { return Task.FromResult(false); }
        bool tree = _tree.IsKeyboardFocusWithin;
        if (!tree && !_revisionFiles.IsKeyboardFocusWithin) { return Task.FromResult(false); }
        IEnumerable<MenuItem> Descendants(IEnumerable<object> items)
        {
            foreach (MenuItem item in items.OfType<MenuItem>())
            {
                yield return item;
                foreach (MenuItem child in Descendants(item.Items.Cast<object>())) { yield return child; }
            }
        }
        MenuItem? action = Descendants(BuildFileMenuItems(tree, GetFileMenuSelection(tree)))
            .FirstOrDefault(item => item.IsEnabled && item.InputGesture is { } gesture && gesture.Key == args.Key && gesture.KeyModifiers == args.KeyModifiers);
        if (action is null) { return Task.FromResult(false); }
        args.Handled = true; action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, action));
        return Task.FromResult(true);
    }
}
