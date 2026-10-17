using System.Collections.ObjectModel;
using Avalonia.Input;

namespace GitExtensions.Linux.Desktop;

internal sealed record HotkeyCommand(string Context, int Code, string Name, KeyGesture? DefaultGesture);

/// <summary>Stable Windows context/enum identities; presentation order is independent of numeric IDs.</summary>
internal static class HotkeyCatalog
{
    public static IReadOnlyList<string> Contexts { get; } = Array.AsReadOnly<string>(
        ["Commit", "Browse", "LeftPanel", "RevisionGrid", "FileViewer", "FormMergeConflicts", "BrowseDiff", "Stash", "Scripts"]);

    // Original: GitUI/Hotkey/HotkeySettingsManager.cs, CreateDefaultSettings (193-396).
    // Script identities come from configuration; no Windows examples are imported or enabled here.
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<HotkeyCommand>> Commands =
        new ReadOnlyDictionary<string, IReadOnlyList<HotkeyCommand>>(
            new Dictionary<string, IReadOnlyList<HotkeyCommand>>(StringComparer.Ordinal)
            {
                // GitUI/CommandsDialogs/FormCommit.cs:647-676
                ["Commit"] = Array.AsReadOnly<HotkeyCommand>(
                [
                    new("Commit", 16, "AddSelectionToCommitMessage", new(Key.C)),
                    new("Commit", 25, "ConventionalCommit_PrefixMessage", new(Key.T, KeyModifiers.Control)),
                    new("Commit", 26, "ConventionalCommit_PrefixMessageWithScope", new(Key.T, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("Commit", 17, "CreateBranch", new(Key.B, KeyModifiers.Control)),
                    new("Commit", 2, "FocusUnstagedFiles", new(Key.D1, KeyModifiers.Control)),
                    new("Commit", 3, "FocusSelectedDiff", new(Key.D2, KeyModifiers.Control)),
                    new("Commit", 4, "FocusStagedFiles", new(Key.D3, KeyModifiers.Control)),
                    new("Commit", 5, "FocusCommitMessage", new(Key.D4, KeyModifiers.Control)),
                    new("Commit", 12, "OpenWithDifftool", new(Key.F3)),
                    new("Commit", 18, "Refresh", new(Key.F5)),
                    new("Commit", 19, "SelectNext", new(Key.N, KeyModifiers.Control)),
                    new("Commit", 20, "SelectNext_AlternativeHotkey1", new(Key.Down, KeyModifiers.Alt)),
                    new("Commit", 21, "SelectNext_AlternativeHotkey2", new(Key.Right, KeyModifiers.Alt)),
                    new("Commit", 22, "SelectPrevious", new(Key.P, KeyModifiers.Control)),
                    new("Commit", 23, "SelectPrevious_AlternativeHotkey1", new(Key.Up, KeyModifiers.Alt)),
                    new("Commit", 24, "SelectPrevious_AlternativeHotkey2", new(Key.Left, KeyModifiers.Alt)),
                    new("Commit", 11, "StageAll", new(Key.S, KeyModifiers.Control)),
                    new("Commit", 10, "ToggleSelectionFilter", new(Key.F, KeyModifiers.Control)),
                ]),
                // GitUI/CommandsDialogs/FormBrowse.cs:1941-2017
                ["Browse"] = Array.AsReadOnly<HotkeyCommand>(
                [
                    new("Browse", 8, "AddNotes", new(Key.N, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("Browse", 10, "CheckoutBranch", new(Key.OemPeriod, KeyModifiers.Control)),
                    new("Browse", 15, "CloseRepository", new(Key.W, KeyModifiers.Control)),
                    new("Browse", 7, "Commit", new(Key.Space, KeyModifiers.Control)),
                    new("Browse", 41, "CreateBranch", new(Key.B, KeyModifiers.Control)),
                    new("Browse", 43, "CreateTag", new(Key.T, KeyModifiers.Control)),
                    new("Browse", 22, "EditFile", new(Key.F4)),
                    new("Browse", 9, "FindFileInSelectedCommit", new(Key.F, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("Browse", 25, "FocusLeftPanel", new(Key.D0, KeyModifiers.Control)),
                    new("Browse", 3, "FocusRevisionGrid", new(Key.D1, KeyModifiers.Control)),
                    new("Browse", 4, "FocusCommitInfo", new(Key.D2, KeyModifiers.Control)),
                    new("Browse", 5, "FocusDiff", new(Key.D3, KeyModifiers.Control)),
                    new("Browse", 6, "FocusFileTree", new(Key.D4, KeyModifiers.Control)),
                    new("Browse", 26, "FocusGpgInfo", new(Key.D5, KeyModifiers.Control)),
                    new("Browse", 29, "FocusGitConsole", new(Key.D6, KeyModifiers.Control)),
                    new("Browse", 30, "FocusBuildServerStatus", new(Key.D7, KeyModifiers.Control)),
                    new("Browse", 47, "FocusOutputHistoryAndToggleIfPanel", new(Key.D9, KeyModifiers.Control)),
                    new("Browse", 31, "FocusNextTab", new(Key.Tab, KeyModifiers.Control)),
                    new("Browse", 32, "FocusPrevTab", new(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("Browse", 18, "FocusFilter", new(Key.E, KeyModifiers.Control)),
                    new("Browse", 0, "GitBash", new(Key.G, KeyModifiers.Control)),
                    new("Browse", 1, "GitGui", null),
                    new("Browse", 2, "GitGitK", null),
                    new("Browse", 37, "GoToChild", new(Key.N, KeyModifiers.Control)),
                    new("Browse", 38, "GoToParent", new(Key.P, KeyModifiers.Control)),
                    new("Browse", 28, "GoToSubmodule", null),
                    new("Browse", 27, "GoToSuperproject", null),
                    new("Browse", 49, "ManageWorkTrees", new(Key.W, KeyModifiers.Control | KeyModifiers.Alt)),
                    new("Browse", 42, "MergeBranches", new(Key.M, KeyModifiers.Control)),
                    new("Browse", 23, "OpenAsTempFile", new(Key.F3, KeyModifiers.Control)),
                    new("Browse", 24, "OpenAsTempFileWith", new(Key.F3, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("Browse", 35, "OpenCommitsWithDifftool", null),
                    new("Browse", 45, "OpenRepo", new(Key.O, KeyModifiers.Control)),
                    new("Browse", 20, "OpenSettings", new(Key.OemComma, KeyModifiers.Control)),
                    new("Browse", 19, "OpenWithDifftool", new(Key.F3)),
                    new("Browse", 33, "OpenWithDifftoolFirstToLocal", new(Key.F3, KeyModifiers.Alt)),
                    new("Browse", 34, "OpenWithDifftoolSelectedToLocal", new(Key.F3, KeyModifiers.Shift | KeyModifiers.Alt)),
                    new("Browse", 39, "PullOrFetch", new(Key.Down, KeyModifiers.Control)),
                    new("Browse", 40, "Push", new(Key.Up, KeyModifiers.Control)),
                    new("Browse", 11, "QuickFetch", new(Key.Down, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("Browse", 12, "QuickPull", new(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("Browse", 48, "QuickPullOrFetch", new(Key.F8)),
                    new("Browse", 13, "QuickPush", new(Key.Up, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("Browse", 44, "Rebase", new(Key.E, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("Browse", 16, "Stash", new(Key.Up, KeyModifiers.Control | KeyModifiers.Alt)),
                    new("Browse", 17, "StashPop", new(Key.Down, KeyModifiers.Control | KeyModifiers.Alt)),
                    new("Browse", 46, "StashStaged", new(Key.Up, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)),
                    new("Browse", 36, "ToggleBetweenArtificialAndHeadCommits", new(Key.OemBackslash, KeyModifiers.Control)),
                    new("Browse", 21, "ToggleLeftPanel", new(Key.C, KeyModifiers.Control | KeyModifiers.Alt)),
                ]),
                // GitUI/LeftPanel/RepoObjectsTree.Command.cs:5-12
                ["LeftPanel"] = Array.AsReadOnly<HotkeyCommand>(
                [
                    new("LeftPanel", 0, "Delete", new(Key.Delete)),
                    new("LeftPanel", 3, "MultiSelect", new(Key.Space, KeyModifiers.Control)),
                    new("LeftPanel", 4, "MultiSelectWithChildren", new(Key.Space, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("LeftPanel", 1, "Rename", new(Key.F2)),
                    new("LeftPanel", 2, "Search", new(Key.F3)),
                ]),
                // GitUI/UserControls/RevisionGrid/RevisionGridControl.Command.cs:5-55
                ["RevisionGrid"] = Array.AsReadOnly<HotkeyCommand>(
                [
                    new("RevisionGrid", 30, "CompareSelectedCommits", null),
                    new("RevisionGrid", 24, "CompareToBase", new(Key.R, KeyModifiers.Control)),
                    new("RevisionGrid", 29, "CompareToBranch", null),
                    new("RevisionGrid", 28, "CompareToCurrentBranch", null),
                    new("RevisionGrid", 27, "CompareToWorkingDirectory", new(Key.D, KeyModifiers.Control)),
                    new("RevisionGrid", 44, "CreateAmendCommit", null),
                    new("RevisionGrid", 25, "CreateFixupCommit", new(Key.X, KeyModifiers.Control)),
                    new("RevisionGrid", 43, "CreateSquashCommit", new(Key.X, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 41, "DeleteRef", new(Key.Delete)),
                    new("RevisionGrid", 15, "GoToChild", new(Key.N, KeyModifiers.Control)),
                    new("RevisionGrid", 20, "GoToCommit", new(Key.G, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 45, "GoToFirstParent", new(Key.Left, KeyModifiers.Control)),
                    new("RevisionGrid", 46, "GoToLastParent", new(Key.Right, KeyModifiers.Control)),
                    new("RevisionGrid", 31, "GoToMergeBase", new(Key.K, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 14, "GoToParent", new(Key.P, KeyModifiers.Control)),
                    new("RevisionGrid", 21, "NavigateBackward", new(Key.Left, KeyModifiers.Alt)),
                    new("RevisionGrid", 39, "NavigateBackward_AlternativeHotkey", new(Key.BrowserBack)),
                    new("RevisionGrid", 22, "NavigateForward", new(Key.Right, KeyModifiers.Alt)),
                    new("RevisionGrid", 40, "NavigateForward_AlternativeHotkey", new(Key.BrowserForward)),
                    new("RevisionGrid", 17, "NextQuickSearch", new(Key.Down, KeyModifiers.Alt)),
                    new("RevisionGrid", 32, "OpenCommitsWithDifftool", null),
                    new("RevisionGrid", 18, "PrevQuickSearch", new(Key.Up, KeyModifiers.Alt)),
                    new("RevisionGrid", 42, "RenameRef", new(Key.F2)),
                    new("RevisionGrid", 37, "ResetRevisionPathFilter", new(Key.H, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 36, "ResetRevisionFilter", new(Key.I, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 1, "RevisionFilter", new(Key.I, KeyModifiers.Control)),
                    new("RevisionGrid", 23, "SelectAsBaseToCompare", new(Key.L, KeyModifiers.Control)),
                    new("RevisionGrid", 19, "SelectCurrentRevision", new(Key.C, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 38, "SelectNextForkPointAsDiffBase", new(Key.K, KeyModifiers.Control)),
                    new("RevisionGrid", 9, "ShowAllBranches", new(Key.A, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 10, "ShowCurrentBranchOnly", new(Key.U, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 11, "ShowFilteredBranches", new(Key.T, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 13, "ShowFirstParent", new(Key.S, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 34, "ShowReflogReferences", new(Key.L, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 12, "ShowRemoteBranches", new(Key.R, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 2, "ToggleAuthorDateCommitDate", null),
                    new("RevisionGrid", 33, "ToggleBetweenArtificialAndHeadCommits", new(Key.OemBackslash, KeyModifiers.Control)),
                    new("RevisionGrid", 5, "ToggleDrawNonRelativesGray", null),
                    new("RevisionGrid", 16, "ToggleHighlightSelectedBranch", new(Key.B, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 3, "ToggleOrderRevisionsByDate", null),
                    new("RevisionGrid", 0, "ToggleRevisionGraph", null),
                    new("RevisionGrid", 6, "ToggleShowGitNotes", null),
                    new("RevisionGrid", 47, "ToggleShowGitNotesColumn", null),
                    new("RevisionGrid", 8, "ToggleHideMergeCommits", new(Key.M, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("RevisionGrid", 4, "ToggleShowRelativeDate", null),
                    new("RevisionGrid", 26, "ToggleShowTags", new(Key.T, KeyModifiers.Control | KeyModifiers.Alt)),
                ]),
                // GitUI/Editor/FileViewer.cs:2102-2124
                ["FileViewer"] = Array.AsReadOnly<HotkeyCommand>(
                [
                    new("FileViewer", 0, "Find", new(Key.F, KeyModifiers.Control)),
                    new("FileViewer", 16, "Replace", new(Key.H, KeyModifiers.Control)),
                    new("FileViewer", 8, "FindNextOrOpenWithDifftool", new(Key.F3)),
                    new("FileViewer", 9, "FindPrevious", new(Key.F3, KeyModifiers.Shift)),
                    new("FileViewer", 1, "GoToLine", new(Key.G, KeyModifiers.Control)),
                    new("FileViewer", 2, "IncreaseNumberOfVisibleLines", new(Key.OemPlus, KeyModifiers.Control)),
                    new("FileViewer", 3, "DecreaseNumberOfVisibleLines", new(Key.OemMinus, KeyModifiers.Control)),
                    new("FileViewer", 6, "NextChange", new(Key.Down, KeyModifiers.Alt)),
                    new("FileViewer", 7, "PreviousChange", new(Key.Up, KeyModifiers.Alt)),
                    new("FileViewer", 4, "ShowEntireFile", new(Key.E, KeyModifiers.Control)),
                    new("FileViewer", 17, "ShowSyntaxHighlighting", new(Key.X)),
                    new("FileViewer", 18, "ShowGitWordColoring", new(Key.D, KeyModifiers.Control)),
                    new("FileViewer", 19, "ShowDifftastic", new(Key.T, KeyModifiers.Control)),
                    new("FileViewer", 5, "TreatFileAsText", null),
                    new("FileViewer", 10, "NextOccurrence", new(Key.Right, KeyModifiers.Alt)),
                    new("FileViewer", 11, "PreviousOccurrence", new(Key.Left, KeyModifiers.Alt)),
                    new("FileViewer", 12, "StageLines", new(Key.S)),
                    new("FileViewer", 13, "UnstageLines", new(Key.U)),
                    new("FileViewer", 14, "ResetLines", new(Key.R)),
                    new("FileViewer", 15, "IgnoreAllWhitespace", new(Key.W, KeyModifiers.Control | KeyModifiers.Shift)),
                ]),
                // GitUI/CommandsDialogs/FormResolveConflicts.cs:1547-1554
                ["FormMergeConflicts"] = Array.AsReadOnly<HotkeyCommand>(
                [
                    new("FormMergeConflicts", 4, "ChooseBase", new(Key.B)),
                    new("FormMergeConflicts", 3, "ChooseLocal", new(Key.L)),
                    new("FormMergeConflicts", 2, "ChooseRemote", new(Key.R)),
                    new("FormMergeConflicts", 0, "Merge", new(Key.M)),
                    new("FormMergeConflicts", 1, "Rescan", new(Key.F5)),
                ]),
                // GitUI/CommandsDialogs/RevisionDiffControl.cs:91-118
                ["BrowseDiff"] = Array.AsReadOnly<HotkeyCommand>(
                [
                    new("BrowseDiff", 22, "AddFileToGitIgnore", null),
                    new("BrowseDiff", 2, "Blame", new(Key.B)),
                    new("BrowseDiff", 0, "DeleteSelectedFiles", new(Key.Delete)),
                    new("BrowseDiff", 4, "EditFile", new(Key.F4)),
                    new("BrowseDiff", 13, "FilterFileInGrid", new(Key.F)),
                    new("BrowseDiff", 15, "FindFile", null),
                    new("BrowseDiff", 17, "FindInCommitFilesUsingGitGrep_DiffTab", new(Key.F, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("BrowseDiff", 24, "FindInCommitFilesUsingGitGrep_FileTreeTab", null),
                    new("BrowseDiff", 18, "GoToFirstParent", new(Key.Left, KeyModifiers.Control)),
                    new("BrowseDiff", 19, "GoToLastParent", new(Key.Right, KeyModifiers.Control)),
                    new("BrowseDiff", 5, "OpenAsTempFile", new(Key.F3, KeyModifiers.Control)),
                    new("BrowseDiff", 6, "OpenAsTempFileWith", new(Key.F3, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("BrowseDiff", 21, "OpenInVisualStudio", new(Key.S, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("BrowseDiff", 3, "OpenWithDifftool", new(Key.F3)),
                    new("BrowseDiff", 7, "OpenWithDifftoolFirstToLocal", new(Key.F3, KeyModifiers.Alt)),
                    new("BrowseDiff", 8, "OpenWithDifftoolSelectedToLocal", new(Key.F3, KeyModifiers.Shift | KeyModifiers.Alt)),
                    new("BrowseDiff", 20, "OpenWorkingDirectoryFile", new(Key.F4, KeyModifiers.Shift)),
                    new("BrowseDiff", 16, "OpenWorkingDirectoryFileWith", new(Key.F4, KeyModifiers.Control | KeyModifiers.Shift)),
                    new("BrowseDiff", 23, "RenameMove", new(Key.F2)),
                    new("BrowseDiff", 9, "ResetSelectedFiles", new(Key.R)),
                    new("BrowseDiff", 14, "SelectFirstGroupChanges", new(Key.A, KeyModifiers.Control)),
                    new("BrowseDiff", 12, "ShowFileTree", new(Key.T)),
                    new("BrowseDiff", 1, "ShowHistory", new(Key.H)),
                    new("BrowseDiff", 10, "StageSelectedFile", new(Key.S)),
                    new("BrowseDiff", 11, "UnStageSelectedFile", new(Key.U)),
                ]),
                // GitUI/CommandsDialogs/FormStash.cs:212-217
                ["Stash"] = Array.AsReadOnly<HotkeyCommand>(
                [
                    new("Stash", 0, "NextStash", new(Key.N, KeyModifiers.Control)),
                    new("Stash", 1, "PreviousStash", new(Key.P, KeyModifiers.Control)),
                    new("Stash", 2, "Refresh", new(Key.F5)),
                ]),
                // GitUI/Hotkey/HotkeySettingsManager.cs:381-396
                ["Scripts"] = Array.Empty<HotkeyCommand>(),
            });

    public static IReadOnlyList<HotkeyCommand> For(string context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Commands.TryGetValue(context, out IReadOnlyList<HotkeyCommand>? commands) ? commands : Array.Empty<HotkeyCommand>();
    }

    public static HotkeyCommand? Find(string context, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return For(context).FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.Ordinal));
    }

    public static HotkeyCommand? Find(string context, int code) => For(context).FirstOrDefault(command => command.Code == code);
}
