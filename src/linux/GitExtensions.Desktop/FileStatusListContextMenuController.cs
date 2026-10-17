using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>One menu order, native predicates and shortcut dispatcher for file-list hosts.</summary>
internal sealed class FileStatusListContextMenuController(FileStatusListView view, TranslationCatalog translations, FileMenuHost host)
{
    private readonly Dictionary<MenuItem, Func<Task?>> _executions = [];

    private string T(string value) => translations.Translate(value);
    internal object[] BuildItems(FileMenuSelection? selection)
    {
        _executions.Clear();
        if (selection is null) { return []; }
        FileMenuPolicy p = new(selection);
        bool ready = !host.IsBusy() && host.IsCurrent(selection);
        FileMenuCapabilities c = host.Capabilities;
        MenuItem Item(string name, string label, string? icon, FileMenuCommand command, bool enabled = true, KeyGesture? gesture = null)
        {
            MenuItem item = CommandItem("FileStatusList", name, label, icon,
                () => command == FileMenuCommand.ResetToFirst && !p.CanResetFirst ? Task.CompletedTask : ExecuteAsync(command, selection),
                command, selection, gesture, bindHotkey: name != "tsmiResetFileTo");
            if (command == FileMenuCommand.ResetToFirst && !p.CanResetFirst) { item.Tag = null; }
            item.IsEnabled = ready && enabled;
            if (name != "tsmiOpenWithDifftool" && command is (FileMenuCommand.DiffFirstToSecond or FileMenuCommand.DiffSecondToWorkingTree or FileMenuCommand.DiffFirstToWorkingTree
                or FileMenuCommand.DiffTwoSelected or FileMenuCommand.DiffWithRemembered))
            {
                string[] tools = host.GetCustomDiffTools().ToArray();
                if (tools.Length > 1)
                {
                    for (int index = 0; index < tools.Length; index++)
                    {
                        string tool = tools[index];
                        MenuItem choice = CommandItem("FileStatusList", name + "Tool" + index, tool, null,
                            () => ExecuteAsync(command, selection with { CustomDiffTool = tool }), command, selection,
                            index == 0 ? gesture : null, bindHotkey: index == 0);
                        choice.IsEnabled = item.IsEnabled;
                        if (index == 0) { choice.FontWeight = FontWeight.Bold; }
                        item.Items.Add(choice);
                    }
                    item.Items.Add(new Separator());
                    item.Items.Add(host.CreateAction("FileStatusList", name + "DisableCustomDiffTools", "Disable menu item", null,
                        () => ExecuteAsync(FileMenuCommand.DisableCustomDiffTools, selection), null));
                }
            }
            return item;
        }
        MenuItem Container(string name, string label, string? icon, bool enabled)
        {
            MenuItem item = OriginalMenuItems.Create(translations, "FileStatusList", name, label, icon);
            item.IsEnabled = ready && enabled;
            return item;
        }
        List<object> items = [];
        if (p.Single && p.Submodule)
        { items.Add(Item("OpenSubmodule", "Open with Git Extensions", "FolderSubmodule.png", FileMenuCommand.OpenSubmodule)); }
        TreeViewItem[] nodes = view.Tree.SelectedItems?.OfType<TreeViewItem>().ToArray() ?? [];
        if (nodes.Any(node => node.Items.Count > 0))
        {
            items.Add(Item("FileTreeSelectAll", "S&elect all", "FileTree.png", FileMenuCommand.SelectAll));
            items.Add(Item("FileTreeCollapseAll", "C&ollapse all", "CollapseAll.png", FileMenuCommand.CollapseAll));
            items.Add(Item("FileTreeExpandAll", "E&xpand all", "ExpandAll.png", FileMenuCommand.ExpandAll));
            items.Add(new Separator());
        }
        if (p.CanSubmodule && !p.DisplayOnly)
        {
            items.Add(Item("tsmiUpdateSubmodule", "&Update submodule", "SubmodulesUpdate.png", FileMenuCommand.UpdateSubmodule, !p.Bare));
            items.Add(Item("tsmiResetSubmoduleChanges", "R&eset submodule changes", "ResetWorkingDirChanges.png", FileMenuCommand.ResetSubmodule, !p.Bare));
            items.Add(Item("tsmiStashSubmoduleChanges", "S&tash submodule changes", "stash.png", FileMenuCommand.StashSubmodule, !p.Bare));
            items.Add(Item("tsmiCommitSubmoduleChanges", "&Commit submodule changes", "RepoStateDirtySubmodules.png", FileMenuCommand.CommitSubmodule, !p.Bare));
            items.Add(new Separator());
        }
        if (p.WorkTree && !p.DisplayOnly) { items.Add(Item("tsmiStageFile", "&Stage selected", "Stage.png", FileMenuCommand.Stage, !p.Bare, new(Key.S))); }
        if (p.Index && !p.DisplayOnly) { items.Add(Item("tsmiUnstageFile", "&Unstage selected", "Unstage.png", FileMenuCommand.Unstage, !p.Bare, new(Key.U))); }
        MenuItem reset = Item("tsmiResetFileTo", "&Reset file(s) to", "ResetWorkingDirChanges.png", FileMenuCommand.ResetToFirst, p.CanReset);
        if (p.CanResetSecond)
        { reset.Items.Add(Item("tsmiResetFileToSelected", T("Second: B ") + Describe(selection.Leaves.Select(leaf => leaf.Second)), null, FileMenuCommand.ResetToSecond, p.CanReset)); }
        if (p.CanResetFirst)
        {
            MenuItem first = Item("tsmiResetFileToParent", T("First: A ") + Describe(p.First), null, FileMenuCommand.ResetToFirst, p.CanReset, new(Key.R));
            first.FontWeight = FontWeight.Bold; reset.Items.Add(first);
        }
        items.Add(reset);
        if (p.WorkTree && p.Single && !p.Submodule && !p.DisplayOnly)
        {
            items.Add(Item("tsmiResetChunkOfFile", "Reset chunk of file...", null, FileMenuCommand.ResetChunk, !p.Bare));
            items.Add(Item("tsmiInteractiveAdd", "Interactive add...", null, FileMenuCommand.InteractiveAdd, !p.Bare));
        }
        if (c.CherryPick && c.SupportLinePatching && p.Single && !p.WorkTree && !p.DisplayOnly)
        { items.Add(Item("tsmiCherryPickChanges", "Cherr&y pick changes", "CherryPick.png", FileMenuCommand.CherryPick, !p.Bare)); }
        items.Add(new Separator());
        MenuItem diff = Item("tsmiOpenWithDifftool", "Open with &difftool", "Diff.png", FileMenuCommand.DiffFirstToSecond, p.Any && !p.DisplayOnly && p.CanNativeDiff, new(Key.F3));
        if (p.Any)
        {
            foreach ((string name, string label) in new[]
            {
                ("tsmiSecondDiffCaption", T("Second: B ") + Describe(selection.Leaves.Select(leaf => leaf.Second))),
                ("tsmiFirstDiffCaption", T("First: A ") + Describe(p.First))
            }) { diff.Items.Add(new MenuItem { Name = name, Header = label, IsEnabled = false }); }
        }
        MenuItem ab = Item("tsmiDiffFirstToSelected", "&First -> Second", null, FileMenuCommand.DiffFirstToSecond, p.Second is not null && p.First.Length > 0 && p.CanNativeDiff && !p.DisplayOnly);
        ab.FontWeight = FontWeight.Bold; diff.Items.Add(ab);
        if (!p.HideLocalDiffs)
        {
            diff.Items.Add(Item("tsmiDiffSelectedToLocal", "&Second -> Working directory", null, FileMenuCommand.DiffSecondToWorkingTree, p.CanDiffSecondLocal && !p.Bare && !p.DisplayOnly, new(Key.F3, KeyModifiers.Alt | KeyModifiers.Shift)));
            diff.Items.Add(Item("tsmiDiffFirstToLocal", "First -> &Working directory", null, FileMenuCommand.DiffFirstToWorkingTree, p.CanDiffFirstLocal && !p.Bare && !p.DisplayOnly, new(Key.F3, KeyModifiers.Alt)));
        }
        if (selection.Leaves.Count is 1 or 2)
        {
            diff.Items.Add(new Separator());
            if (selection.Leaves.Count == 2)
            { diff.Items.Add(Item("tsmiDiffTwoSelected", "&Diff the selected files", null, FileMenuCommand.DiffTwoSelected, !p.DisplayOnly && !p.Submodule && p.CanNativeDiff)); }
            if (p.Single)
            {
                if (host.GetRemembered() is FileMenuLeaf remembered)
                {
                    diff.Items.Add(Item("tsmiDiffWithRemembered", string.Format(T("Diff selected with remembered file '{0}'"), remembered.File.Path), null,
                        FileMenuCommand.DiffWithRemembered, remembered != selection.Leaves[0] && !p.DisplayOnly && !p.Submodule && (!p.Bare || remembered.Second.Kind == GitCompareRevisionKind.Commit && p.Second?.Kind == GitCompareRevisionKind.Commit)));
                }
                diff.Items.Add(Item("tsmiRememberSecondRevDiff", "&Remember Second for diff", null, FileMenuCommand.RememberSecond, !p.Deleted && !p.Submodule && !p.DisplayOnly));
                diff.Items.Add(Item("tsmiRememberFirstRevDiff", "R&emember First for diff", null, FileMenuCommand.RememberFirst, p.First.Length == 1 && p.First[0].Kind != GitCompareRevisionKind.WorkingTree && selection.Leaves[0].File.Status != 'A' && !p.Submodule && !p.DisplayOnly));
            }
        }
        items.Add(diff);
        if (p.Single && p.AllFilesExist)
        {
            items.Add(Item("tsmiOpenWorkingDirectoryFile", "&Open working directory file", "EditFile.png", FileMenuCommand.OpenWorkingFile, !p.Bare, new(Key.F4, KeyModifiers.Shift)));
            items.Add(Item("tsmiOpenWorkingDirectoryFileWith", "Open working directory file with...", "EditFile.png", FileMenuCommand.OpenWorkingFileWith, !p.Bare, new(Key.F4, KeyModifiers.Control | KeyModifiers.Shift)));
        }
        if (p.CanOpenRevision)
        {
            items.Add(Item("tsmiOpenRevisionFile", "Ope&n this revision (temp file)", "ViewFile.png", FileMenuCommand.OpenRevisionFile, p.CanShowInTree, new(Key.F3, KeyModifiers.Control)));
            items.Add(Item("tsmiOpenRevisionFileWith", "Open this revision &with... (temp file)", "ViewFile.png", FileMenuCommand.OpenRevisionFileWith, p.CanShowInTree, new(Key.F3, KeyModifiers.Control | KeyModifiers.Shift)));
        }
        if (p.Single && p.AllFilesExist)
        { items.Add(Item("tsmiEditWorkingDirectoryFile", "&Edit working directory file", "EditFile.png", FileMenuCommand.EditWorkingFile, !p.Bare, new(Key.F4))); }
        if (p.CanSave) { items.Add(Item("tsmiSaveAs", "S&ave selected as...", "SaveAs.png", FileMenuCommand.SaveAs, true, new(Key.S, KeyModifiers.Control))); }
        if (p.CanMove) { items.Add(Item("tsmiMove", "Rena&me / move", "FileStatusRenamed.png", FileMenuCommand.RenameMove, !p.Bare && !p.DisplayOnly && selection.Folder != "", new(Key.F2))); }
        if (p.AllFilesOrUntrackedDirectoriesExist && p.Artificial && !p.DisplayOnly)
        { items.Add(Item("tsmiDeleteFile", "De&lete file", "DeleteFile.png", FileMenuCommand.DeleteFile, !p.Bare, new(Key.Delete))); }
        items.Add(new Separator());
        MenuItem copy = Container("CopyPathsToolStripMenuItem", "Copy &path(s)", "CopyToClipboard.png", selection.CopyPaths.Count > 0 && !p.DisplayOnly);
        MenuItem Copy(string name, string label, FileMenuCommand command, KeyGesture? gesture = null)
        {
            MenuItem item = CommandItem("CopyPathsToolStripMenuItem", name, label, null, () => ExecuteAsync(command, selection), command, selection, gesture);
            item.IsEnabled = ready && selection.CopyPaths.Count > 0 && !p.DisplayOnly; return item;
        }
        copy.Items.Add(Copy("copyRelativePathsPosixToolStripMenuItem", "Copy relative path(s) - &POSIX", FileMenuCommand.CopyRelativePosix));
        copy.Items.Add(Copy("copyRelativePathsNativeToolStripMenuItem", "Copy relative path(s) - &native", FileMenuCommand.CopyRelativeNative));
        MenuItem full = Copy("copyFullPathsNativeToolStripMenuItem", "Copy &full path(s) - native", FileMenuCommand.CopyFullNative, new(Key.C, KeyModifiers.Control)); full.FontWeight = FontWeight.Bold; copy.Items.Add(full);
        items.Add(copy);
        if (p.Any && !p.DisplayOnly) { items.Add(Item("tsmiShowInFolder", "Show &in folder", "BrowseFileExplorer.png", FileMenuCommand.ShowInFolder, !p.Bare)); }
        items.Add(new Separator());
        if (c.ShowInFileTree && !selection.IsFileTree && p.CanShowInTree)
        { items.Add(Item("tsmiShowInFileTree", "Show in File &tree", "FileTree.png", FileMenuCommand.ShowInFileTree, true, new(Key.T))); }
        if (c.FilterInGrid) { items.Add(Item("tsmiFilterFileInGrid", "Filter file in grid", "FunnelPencil.png", FileMenuCommand.FilterInGrid, p.CanHistory, new(Key.F))); }
        MenuItem history = Item("tsmiFileHistory", "File &history", "FileHistory.png", FileMenuCommand.FileHistory, p.CanHistory && !p.DisplayOnly, new(Key.H));
        if (c.ShowInFileTree) { history.FontWeight = FontWeight.Bold; } items.Add(history);
        items.Add(Item("tsmiBlame", "&Blame", "Blame.png", FileMenuCommand.Blame, p.CanBlame && !p.DisplayOnly, new(Key.B)));
        items.Add(Item("tsmiFindFile", "&Find file...", null, FileMenuCommand.FindFile));
        if (c.Grep)
        {
            items.Add(Item("tsmiOpenFindInCommitFilesGitGrepDialog", "Find in &commit files using git-grep...", "ViewFile.png", FileMenuCommand.FindInCommitFiles, true, new(Key.F, KeyModifiers.Control | KeyModifiers.Shift)));
            MenuItem grep = Item("tsmiShowFindInCommitFilesGitGrep", "Show 'Find in commit fi&les using git-grep'", null, FileMenuCommand.ToggleFindInCommitFiles);
            grep.ToggleType = MenuItemToggleType.CheckBox; grep.IsChecked = host.GrepVisible(); items.Add(grep);
        }
        bool ignore = p.WorkTree && !(p.Single && p.Submodule) && !p.DisplayOnly;
        bool stop = p.Single && p.Tracked && !p.Submodule && !p.DisplayOnly;
        if (ignore || stop) { items.Add(new Separator()); }
        if (ignore)
        {
            items.Add(Item("tsmiAddFileToGitIgnore", "Add file to &.gitignore", "AddToGitIgnore.png", FileMenuCommand.AddToIgnore, !p.Bare));
            items.Add(Item("tsmiAddFileToGitInfoExclude", "Add file to .git/info/exclude", "AddToGitIgnore.png", FileMenuCommand.AddToLocalExclude, !p.Bare));
            if (p.Tracked)
            {
                MenuItem skip = Item("tsmiSkipWorktree", "S&kip worktree", null, FileMenuCommand.ToggleSkipWorktree, !p.Bare);
                skip.ToggleType = MenuItemToggleType.CheckBox; skip.IsChecked = selection.Leaves.Any(leaf => leaf.IsSkipWorktree); items.Add(skip);
                MenuItem assume = Item("tsmiAssumeUnchanged", "Assu&me unchanged", null, FileMenuCommand.ToggleAssumeUnchanged, !p.Bare);
                assume.ToggleType = MenuItemToggleType.CheckBox; assume.IsChecked = selection.Leaves.Any(leaf => leaf.IsAssumeUnchanged); items.Add(assume);
            }
        }
        if (stop) { items.Add(Item("tsmiStopTracking", "Stop tracking this file", "StopTrackingFile.png", FileMenuCommand.StopTracking, !p.Bare)); }
        if (selection.IsFileTree && view.Tree.Items.OfType<TreeViewItem>().Any(item => item.IsExpanded))
        { items.Add(Item("FileTreeCollapseRootFolders", "Collap&se root folders", "TreeCollapseAll.png", FileMenuCommand.CollapseRootFolders)); }
        if (!selection.IsFileTree) { items.Add(new Separator()); items.AddRange(view.CreateSortContextMenuItems(action => ApplyViewOption(selection, action))); }
        return items.ToArray();
    }

    private Task ExecuteAsync(FileMenuCommand command, FileMenuSelection selection)
    {
        if (host.IsBusy() || !host.IsCurrent(selection)) { return Task.CompletedTask; }
        TreeViewItem[] nodes = view.Tree.SelectedItems?.OfType<TreeViewItem>().ToArray() ?? [];
        static void SetExpanded(TreeViewItem item, bool expanded)
        { item.IsExpanded = expanded; foreach (TreeViewItem child in item.Items.OfType<TreeViewItem>()) { SetExpanded(child, expanded); } }
        switch (command)
        {
            case FileMenuCommand.FindFile: return FindFileAsync(selection);
            case FileMenuCommand.SelectAll: view.SelectTags(view.SelectedTags); return Task.CompletedTask;
            case FileMenuCommand.CollapseAll: foreach (TreeViewItem node in nodes) { SetExpanded(node, false); } return Task.CompletedTask;
            case FileMenuCommand.ExpandAll: foreach (TreeViewItem node in nodes) { SetExpanded(node, true); } return Task.CompletedTask;
            case FileMenuCommand.CollapseRootFolders:
                TreeViewItem? selected = view.Tree.SelectedItem as TreeViewItem;
                TreeViewItem? root = selected;
                while (root?.Parent is TreeViewItem parent) { root = parent; }
                if (root is not null && view.Tree.Items.Contains(root)) { view.Tree.SelectedItem = root; }
                foreach (TreeViewItem item in view.Tree.Items.OfType<TreeViewItem>()) { item.IsExpanded = false; }
                return Task.CompletedTask;
            default: return host.ExecuteAsync(command, selection);
        }
    }

    private void ApplyViewOption(FileMenuSelection selection, Action apply)
    {
        if (host.IsBusy() || !host.IsCurrent(selection) || TopLevel.GetTopLevel(view) is not Window owner) { return; }
        TreeViewItem? previous = view.FocusedSelectedItem ?? view.Tree.SelectedItem as TreeViewItem; object? tag = previous?.Tag; string filter = view.FilterText;
        Func<bool>? restoreFocus = view.CaptureSelectedRowFocus();
        apply();
        // A context-menu action replaces the row containers. Resolve the current
        // row only for this action; ordinary rebuilds and filtering never focus.
        Dispatcher.UIThread.Post(() =>
        {
            if (!owner.IsVisible || !owner.IsEffectivelyEnabled || owner.OwnedWindows.Any(window => window.IsVisible)
                || !view.IsEffectivelyVisible || !view.IsEffectivelyEnabled || host.IsBusy() || !host.IsCurrent(selection) || view.FilterText != filter) { return; }
            IInputElement? focused = owner.FocusManager?.GetFocusedElement();
            if (focused is Visual control && focused is not MenuItem && !view.Tree.IsKeyboardFocusWithin
                && !ReferenceEquals(focused, previous) && TopLevel.GetTopLevel(control) is { IsVisible: true }) { return; }
            if (restoreFocus?.Invoke() == true) { return; }
            if (tag is not null) { view.FindItemByTag(tag)?.Focus(); }
        }, DispatcherPriority.Input);
    }

    private async Task FindFileAsync(FileMenuSelection selection)
    {
        if (TopLevel.GetTopLevel(view) is not Window owner) { return; }
        FileListFindDialog dialog = new(view.Entries, selection.Repository.Path, translations) { RequestedThemeVariant = owner.ActualThemeVariant };
        await dialog.ShowDialog(owner);
        if (dialog.SelectedEntry is not { } chosen || host.IsBusy() || !host.IsCurrent(selection) || !owner.IsVisible) { return; }
        if (!view.Entries.Any(entry => Equals(entry.Tag, chosen.Tag))) { return; }
        view.SelectTags([chosen.Tag]);
        view.FindItemByTag(chosen.Tag)?.Focus();
    }

    private MenuItem CommandItem(string category, string name, string label, string? icon, Func<Task> action,
        FileMenuCommand command, FileMenuSelection selection, KeyGesture? fixedGesture, bool bindHotkey = true)
    {
        Task? operation = null;
        MenuItem item = host.CreateAction(category, name, label, icon, () =>
        {
            Task started = InvokeActionAsync(action); operation = started; return started;
        }, fixedGesture);
        item.Tag = command;
        _executions[item] = () => operation;
        if (bindHotkey && HotkeyName(command, selection.IsFileTree) is { } hotkey) { DesktopHotkeys.Bind(item, "BrowseDiff", hotkey); }
        return item;
    }

    private static async Task InvokeActionAsync(Func<Task> action) => await action();

    private static string? HotkeyName(FileMenuCommand command, bool tree) => command switch
    {
        FileMenuCommand.AddToIgnore => "AddFileToGitIgnore",
        FileMenuCommand.Blame => "Blame", FileMenuCommand.DeleteFile => "DeleteSelectedFiles",
        FileMenuCommand.EditWorkingFile => "EditFile", FileMenuCommand.FilterInGrid => "FilterFileInGrid",
        FileMenuCommand.FindFile => "FindFile",
        FileMenuCommand.FindInCommitFiles => tree ? "FindInCommitFilesUsingGitGrep_FileTreeTab" : "FindInCommitFilesUsingGitGrep_DiffTab",
        FileMenuCommand.OpenRevisionFile => "OpenAsTempFile", FileMenuCommand.OpenRevisionFileWith => "OpenAsTempFileWith",
        FileMenuCommand.DiffFirstToSecond => "OpenWithDifftool", FileMenuCommand.DiffFirstToWorkingTree => "OpenWithDifftoolFirstToLocal",
        FileMenuCommand.DiffSecondToWorkingTree => "OpenWithDifftoolSelectedToLocal",
        FileMenuCommand.OpenWorkingFile => "OpenWorkingDirectoryFile", FileMenuCommand.OpenWorkingFileWith => "OpenWorkingDirectoryFileWith",
        FileMenuCommand.RenameMove => "RenameMove", FileMenuCommand.ResetToFirst => "ResetSelectedFiles",
        FileMenuCommand.ShowInFileTree => "ShowFileTree", FileMenuCommand.FileHistory => "ShowHistory",
        FileMenuCommand.Stage => "StageSelectedFile", FileMenuCommand.Unstage => "UnStageSelectedFile", _ => null
    };

    private static FileMenuCommand? CommandFor(string name, bool tree) => name switch
    {
        "AddFileToGitIgnore" => FileMenuCommand.AddToIgnore,
        "Blame" => FileMenuCommand.Blame, "DeleteSelectedFiles" => FileMenuCommand.DeleteFile,
        "EditFile" => FileMenuCommand.EditWorkingFile, "FilterFileInGrid" => FileMenuCommand.FilterInGrid,
        "FindFile" => FileMenuCommand.FindFile,
        "FindInCommitFilesUsingGitGrep_DiffTab" when !tree => FileMenuCommand.FindInCommitFiles,
        "FindInCommitFilesUsingGitGrep_FileTreeTab" when tree => FileMenuCommand.FindInCommitFiles,
        "OpenAsTempFile" => FileMenuCommand.OpenRevisionFile, "OpenAsTempFileWith" => FileMenuCommand.OpenRevisionFileWith,
        "OpenWithDifftool" => FileMenuCommand.DiffFirstToSecond, "OpenWithDifftoolFirstToLocal" => FileMenuCommand.DiffFirstToWorkingTree,
        "OpenWithDifftoolSelectedToLocal" => FileMenuCommand.DiffSecondToWorkingTree,
        "OpenWorkingDirectoryFile" => FileMenuCommand.OpenWorkingFile, "OpenWorkingDirectoryFileWith" => FileMenuCommand.OpenWorkingFileWith,
        "RenameMove" => FileMenuCommand.RenameMove, "ResetSelectedFiles" => FileMenuCommand.ResetToFirst,
        "ShowFileTree" => FileMenuCommand.ShowInFileTree, "ShowHistory" => FileMenuCommand.FileHistory,
        "StageSelectedFile" => FileMenuCommand.Stage, "UnStageSelectedFile" => FileMenuCommand.Unstage,
        // Parent commands belong to the Browse owner; Visual Studio has no Linux action.
        "GoToFirstParent" or "GoToLastParent" or "OpenInVisualStudio" => null,
        _ => null
    };

    private static IEnumerable<MenuItem> EnabledItems(IEnumerable<object> items)
    {
        foreach (MenuItem item in items.OfType<MenuItem>())
        {
            if (!item.IsEnabled || !item.IsVisible) { continue; }
            yield return item;
            foreach (MenuItem child in EnabledItems(item.Items.Cast<object>())) { yield return child; }
        }
    }

    /// <summary>Accepts the current enabled menu command; the existing host click scope owns errors and focus.</summary>
    internal Task<bool> ExecuteCommandAsync(FileMenuCommand command)
        => host.IsBusy() || !view.IsEffectivelyEnabled ? Task.FromResult(false) : ExecuteCommandAsync(command, host.CaptureSelection());

    private Task<bool> ExecuteCommandAsync(FileMenuCommand command, FileMenuSelection? selection, Action? accepted = null)
    {
        if (selection is null || host.IsBusy() || !host.IsCurrent(selection) || !view.IsEffectivelyEnabled) { return Task.FromResult(false); }
        MenuItem? item = EnabledItems(BuildItems(selection)).FirstOrDefault(item => item.Tag is FileMenuCommand value && value == command);
        return item is null ? Task.FromResult(false) : InvokeItemAsync(item, accepted);
    }

    private async Task<bool> InvokeItemAsync(MenuItem item, Action? accepted)
    {
        Func<Task?>? operation = _executions.GetValueOrDefault(item);
        if (operation is null) { return false; }
        accepted?.Invoke();
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        if (operation() is not { } started) { return false; }
        // The same host event handler reports failures, including its owned error window.
        // True means the available command was accepted; never retry a second binding on failure.
        try { await started; } catch (Exception) { }
        return true;
    }

    internal Task<bool> HandleShortcutAsync(KeyEventArgs args)
    {
        if (args.Handled || host.IsBusy() || !view.IsEffectivelyEnabled || !view.Tree.IsKeyboardFocusWithin) { return Task.FromResult(false); }
        HotkeyCommand? hotkey = DesktopHotkeys.Match("BrowseDiff", args);
        FileMenuSelection? selection = host.CaptureSelection();
        if (selection is null || !host.IsCurrent(selection)) { return Task.FromResult(false); }
        if (hotkey?.Name == "SelectFirstGroupChanges")
        { args.Handled = true; view.SelectFirstGroup(); return Task.FromResult(true); }
        if (hotkey is not null)
        {
            FileMenuCommand? command = CommandFor(hotkey.Name, selection.IsFileTree);
            return command is { } value ? ExecuteCommandAsync(value, selection, () => args.Handled = true) : Task.FromResult(false);
        }
        // These two ordinary menu shortcuts have no Windows BrowseDiff preset.
        FileMenuCommand? fixedCommand = (args.Key, args.KeyModifiers) switch
        {
            (Key.C, KeyModifiers.Control) => FileMenuCommand.CopyFullNative,
            (Key.S, KeyModifiers.Control) => FileMenuCommand.SaveAs,
            _ => null
        };
        return fixedCommand is { } fixedValue ? ExecuteCommandAsync(fixedValue, selection, () => args.Handled = true) : Task.FromResult(false);
    }
    private string Describe(IEnumerable<GitCompareRevision> endpoints)
        => FileMenuRevisionDescription.Describe(endpoints, T);
}
