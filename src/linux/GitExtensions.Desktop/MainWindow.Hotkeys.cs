using Avalonia.Controls;
using Avalonia.Input;

namespace GitExtensions.Linux.Desktop;

public sealed partial class MainWindow
{
    private static readonly Dictionary<string, string> HistoryHotkeyNames = new(StringComparer.Ordinal)
    {
        ["ToggleBetweenArtificialAndHeadCommits"] = "ToggleBetweenArtificialAndHeadCommits",
        ["GotoCurrentRevision"] = "SelectCurrentRevision", ["GotoCommit"] = "GoToCommit",
        ["GotoChildCommit"] = "GoToChild", ["GotoParentCommit"] = "GoToParent",
        ["GotoFirstParentCommit"] = "GoToFirstParent", ["GotoLastParentCommit"] = "GoToLastParent",
        ["GotoMergeBaseCommit"] = "GoToMergeBase", ["NavigateBackward"] = "NavigateBackward", ["NavigateForward"] = "NavigateForward",
        ["PrevQuickSearch"] = "PrevQuickSearch", ["NextQuickSearch"] = "NextQuickSearch",
        ["ShowAllBranches"] = "ShowAllBranches", ["ShowCurrentBranchOnly"] = "ShowCurrentBranchOnly",
        ["ShowFilteredBranches"] = "ShowFilteredBranches", ["ShowReflogReferences"] = "ShowReflogReferences",
        ["filterToolStripMenuItem"] = "RevisionFilter", ["drawNonrelativesGrayToolStripMenuItem"] = "ToggleDrawNonRelativesGray",
        ["HighlightSelectedBranch"] = "ToggleHighlightSelectedBranch", ["ShowRemoteBranches"] = "ShowRemoteBranches",
        ["showTagsToolStripMenuItem"] = "ToggleShowTags", ["showGitNotesToolStripMenuItem"] = "ToggleShowGitNotes",
        ["showAuthorDateToolStripMenuItem"] = "ToggleAuthorDateCommitDate", ["showRelativeDateToolStripMenuItem"] = "ToggleShowRelativeDate",
        ["showRevisionGraphColumnToolStripMenuItem"] = "ToggleRevisionGraph", ["showGitNotesColumnToolStripMenuItem"] = "ToggleShowGitNotesColumn"
    };

    private static string? BrowseHotkeyName(string label) => label switch
    {
        "Open repository…" or "Open repository" => "OpenRepo", "Close" => "CloseRepository",
        "Commit…" or "Commit" => "Commit", "Create branch…" => "CreateBranch", "Checkout branch…" or "Checkout branch" => "CheckoutBranch",
        "Create tag…" => "CreateTag", "Merge…" => "MergeBranches", "Rebase…" => "Rebase", "Pull…" or "Pull" or "Pull/Fetch…" => "PullOrFetch",
        "Push…" or "Push" => "Push", "Settings…" => "OpenSettings", "Terminal" or "Git bash" => "GitBash",
        "Show left panel" or "Toggle left panel" => "ToggleLeftPanel", "Worktrees…" => "ManageWorkTrees", _ => null
    };

    private static (string Context, string Command)? MenuHotkeyIdentity(string label)
    {
        if (BrowseHotkeyName(label) is { } browse) { return ("Browse", browse); }
        string? grid = label switch
        {
            "Open selected commits with difftool" => "OpenCommitsWithDifftool", "Compare to branch…" => "CompareToBranch",
            "Compare with current branch" => "CompareToCurrentBranch", "Select as BASE to compare" => "SelectAsBaseToCompare",
            "Compare to BASE" => "CompareToBase", "Compare to working directory" => "CompareToWorkingDirectory",
            "Compare selected commits" => "CompareSelectedCommits", "Create a fixup commit..." => "CreateFixupCommit",
            "Create a squash commit..." => "CreateSquashCommit", "Create an amend commit..." => "CreateAmendCommit", _ => null
        };
        return grid is null ? null : ("RevisionGrid", grid);
    }

    private static Task InvokeHotkey(Action action) { action(); return Task.CompletedTask; }

    private Func<Task>? BrowseHotkeyAction(string command) => command switch
    {
        "OpenRepo" => ChooseRepositoryAsync, "CloseRepository" => CloseRepositoryAsync, "OpenSettings" => ShowSettingsAsync,
        "GitBash" => OpenTerminalAsync, "GitGui" when _repository?.IsBare == false => () => LaunchGitToolAsync("gui"),
        "GitGitK" when _repository is not null => () => LaunchGitToolAsync("gitk"),
        "Commit" => () => ShowWorkflowAsync("Changes"), "CreateBranch" => CreateBranchAsync, "CreateTag" => CreateTagAsync,
        "CheckoutBranch" => OpenBranchCheckoutAsync, "MergeBranches" => MergeAsync, "Rebase" => RebaseAsync,
        "PullOrFetch" => PullAsync, "Push" => PushAsync, "QuickFetch" => QuickFetchAsync,
        "QuickPull" => () => ExecutePullActionAsync(GitExtensions.Extensibility.Git.GitPullAction.Merge),
        "QuickPullOrFetch" => DefaultPullAsync, "QuickPush" => QuickPushAsync, "Stash" => QuickStashAsync, "StashPop" => QuickStashPopAsync,
        "StashStaged" when _supportsStashStaged == true => QuickStashStagedAsync, "ManageWorkTrees" => () => ManageWorktreesAsync(),
        "AddNotes" => NotesAsync, "ToggleLeftPanel" => ToggleNavigationAsync,
        "FocusLeftPanel" => () => InvokeHotkey(() => _navigation.Focus()), "FocusRevisionGrid" => () => InvokeHotkey(() => _commits.Focus()),
        "FocusCommitInfo" => () => InvokeHotkey(FocusCommitInformation), "FocusDiff" => () => InvokeHotkey(() => FocusDetail("DiffTab")),
        "FocusFileTree" => () => InvokeHotkey(() => FocusDetail("FileTreeTab")), "FocusGpgInfo" => () => InvokeHotkey(() => FocusDetail("GpgTab")),
        "FocusGitConsole" => () => InvokeHotkey(() => FocusDetail("ConsoleTab")), "FocusFilter" => () => InvokeHotkey(FocusHistoryFilter),
        "FocusNextTab" => () => InvokeHotkey(() => CycleDetails(1)), "FocusPrevTab" => () => InvokeHotkey(() => CycleDetails(-1)),
        "GoToParent" => () => MoveHistoryRelativeAsync(RelativeRevisionCommand.Parent),
        "GoToChild" => () => MoveHistoryRelativeAsync(RelativeRevisionCommand.Child), "ToggleBetweenArtificialAndHeadCommits" => ToggleArtificialRevisionAsync,
        "OpenCommitsWithDifftool" => OpenComparisonToolAsync,
        "EditFile" => () => ExecuteBrowseFileHotkeyAsync(FileMenuCommand.EditWorkingFile),
        "OpenAsTempFile" => () => ExecuteBrowseFileHotkeyAsync(FileMenuCommand.OpenRevisionFile, treeOnly: true),
        "OpenAsTempFileWith" => () => ExecuteBrowseFileHotkeyAsync(FileMenuCommand.OpenRevisionFileWith, treeOnly: true),
        "OpenWithDifftool" => () => ExecuteBrowseFileHotkeyAsync(FileMenuCommand.DiffFirstToSecond),
        "OpenWithDifftoolFirstToLocal" => () => ExecuteBrowseFileHotkeyAsync(FileMenuCommand.DiffFirstToWorkingTree),
        "OpenWithDifftoolSelectedToLocal" => () => ExecuteBrowseFileHotkeyAsync(FileMenuCommand.DiffSecondToWorkingTree),
        "FindFileInSelectedCommit" => FindFileInSelectedCommitHotkeyAsync,
        _ => null
    };

    private Func<Task>? RevisionGridHotkeyAction(string command)
    {
        string canonical = command switch
        { "NavigateBackward_AlternativeHotkey" => "NavigateBackward", "NavigateForward_AlternativeHotkey" => "NavigateForward", _ => command };
        HistoryCommand? history = NavigateHistoryCommands().Concat(ViewHistoryCommands())
            .FirstOrDefault(item => HistoryHotkeyNames.GetValueOrDefault(item.Name) == canonical);
        if (history is not null) { return history.Available?.Invoke() == false ? null : history.Execute; }
        return command switch
        {
            "RenameRef" => () => ExecuteQuickReferenceActionAsync(QuickGitRefAction.Rename),
            "DeleteRef" => () => ExecuteQuickReferenceActionAsync(QuickGitRefAction.Delete),
            "CompareToBase" => CompareToBaseHotkeyAsync,
            "CompareSelectedCommits" => CompareSelectedCommitsAsync, "CompareToBranch" => CompareToBranchAsync,
            "CompareToCurrentBranch" => CompareToCurrentBranchAsync,
            "CompareToWorkingDirectory" => CompareToWorkingDirectoryAsync,
            "SelectAsBaseToCompare" => SelectComparisonBaseAsync,
            "OpenCommitsWithDifftool" => OpenComparisonToolAsync,
            "CreateFixupCommit" => () => OpenTargetedCommitAsync(TargetedCommitKind.Fixup),
            "CreateSquashCommit" => () => OpenTargetedCommitAsync(TargetedCommitKind.Squash),
            "CreateAmendCommit" => () => OpenTargetedCommitAsync(TargetedCommitKind.Amend),
            "ShowFirstParent" => () => ChangeHistoryQueryHotkeyAsync(_historyQuery with { FirstParent = !_historyQuery.FirstParent }),
            "ToggleHideMergeCommits" => () => ChangeHistoryQueryHotkeyAsync(_historyQuery with { HideMergeCommits = !_historyQuery.HideMergeCommits }),
            "ResetRevisionPathFilter" => () => ChangeHistoryQueryHotkeyAsync(_historyQuery with { Path = null }),
            "ResetRevisionFilter" => ResetHistoryFiltersHotkeyAsync,
            _ => null
        };
    }

    private Task ChangeHistoryQueryHotkeyAsync(GitLogQuery query)
    { _historyQuery = query with { Skip = 0 }; return ReloadHistoryAsync(); }

    private Task CompareToBaseHotkeyAsync() => _comparisonBase is not null
        ? CurrentRelativeRevision() is { } selected ? ShowComparisonAsync(_comparisonBase, selected) : Task.CompletedTask
        : Dialogs.ShowErrorAsync(this, _translations.Translate("RevisionGrid", "_baseForCompareNotSelectedError", "Text", "Base commit for compare is not selected."));

    private Task ResetHistoryFiltersHotkeyAsync()
    {
        if (_historyBranchScope == HistoryBranchScope.Filtered) { _historyBranchScope = HistoryBranchScope.All; }
        _navigationRevisionFilter = null;
        _historyQuery = new GitLogQuery(Count: _historyQuery.Count, All: _historyBranchScope != HistoryBranchScope.Current)
        { BranchFilter = _historyBranchScope == HistoryBranchScope.Current ? "HEAD" : null, Reflog = _historyQuery.Reflog };
        _quickHistoryFilter.Text = "";
        UpdateHistoryScopePresentation();
        return ReloadHistoryAsync();
    }

    private async Task FindFileInSelectedCommitHotkeyAsync()
    {
        GitRepository? repository = _repository;
        if (repository is null) { return; }
        GitCommit[] selected = SelectedComparisonCommits();
        bool artificial = CurrentRelativeRevision() is { Kind: not GitCompareRevisionKind.Commit };
        if (selected.Length > 1 || artificial)
        {
            string? target = artificial ? _historyGraph.CurrentHead : selected[0].Hash;
            GitCommit? row = _commits.Items.OfType<GitCommit>().FirstOrDefault(commit => commit.Hash == target);
            if (row is null) { if (target is not null) { await ShowFilteredRevisionErrorAsync(target); } return; }
            await ApplyQuickSearchAsync(QuickSearchArtificialCount + _commits.Items.IndexOf(row));
        }
        FocusDetail("FileTreeTab");
        await LoadVisibleRevisionPanelAsync();
        if (_repository == repository && IsVisible && !_lifetime.IsCancellationRequested
            && (_tabs.SelectedItem as TabItem)?.Name == "FileTreeTab")
        { await ExecuteBrowseFileHotkeyAsync(FileMenuCommand.FindFile, treeOnly: true); }
    }

    private async Task<bool> HandleNavigationHotkeyAsync(KeyEventArgs args)
    {
        if (!_navigation.IsKeyboardFocusWithin && !_navigationSearch.IsKeyboardFocusWithin || IsFocusedTextEditingKey(args)) { return false; }
        HotkeyCommand? command = DesktopHotkeys.Match("LeftPanel", args);
        if (command is null) { return false; }
        if (command.Name == "Search")
        { args.Handled = true; _navigationSearch.Focus(); _navigationSearch.SelectAll(); return true; }
        if (_repository is not { } repository || _navigation.SelectedItem is not TreeViewItem { Tag: GitReference reference }) { return false; }
        if (command.Name is not ("Rename" or "Delete") || command.Name == "Rename" && !reference.IsBranch) { return false; }
        args.Handled = true;
        try
        {
            if (command.Name == "Rename") { await RenameRevisionBranchAsync(repository, reference); }
            else { await DeleteRevisionBranchAsync(repository, reference); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { LastException = error; AppendOutput(error.Message); _state.Text = error.Message; }
        return true;
    }

    private async Task ExecuteBrowseFileHotkeyAsync(FileMenuCommand command, bool treeOnly = false)
    {
        bool tree = (_tabs.SelectedItem as TabItem)?.Name == "FileTreeTab";
        if (treeOnly && !tree || !tree && (_tabs.SelectedItem as TabItem)?.Name != "DiffTab") { return; }
        if (_fileMenuControllers.TryGetValue(tree, out FileStatusListContextMenuController? controller))
        { await controller.ExecuteCommandAsync(command); }
    }

    private async Task<bool> ExecuteConfiguredHotkeyAsync(string context, KeyEventArgs args, Action? beforeExecute = null)
    {
        HotkeyCommand? command = DesktopHotkeys.Match(context, args);
        if (command is null || context != "Browse" && _repository is null) { return false; }
        Func<Task>? action = context == "Browse" ? BrowseHotkeyAction(command.Name) : RevisionGridHotkeyAction(command.Name);
        if (action is null) { return false; }
        args.Handled = true;
        try { beforeExecute?.Invoke(); await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { LastException = error; AppendOutput(error.Message); _state.Text = error.Message; }
        finally { RefreshHistoryMenuStates(); }
        return true;
    }
}
