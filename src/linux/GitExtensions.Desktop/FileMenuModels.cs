using Avalonia.Controls;
using Avalonia.Input;

namespace GitExtensions.Linux.Desktop;

internal enum FileMenuStage { None, Unknown, Index, WorkingTree }
internal enum FileMenuCommand
{
    OpenSubmodule, UpdateSubmodule, ResetSubmodule, StashSubmodule, CommitSubmodule,
    Stage, Unstage, ResetToSecond, ResetToFirst, ResetChunk, InteractiveAdd, CherryPick,
    DiffFirstToSecond, DiffSecondToWorkingTree, DiffFirstToWorkingTree,
    DiffTwoSelected, DiffWithRemembered, RememberSecond, RememberFirst,
    OpenWorkingFile, OpenWorkingFileWith, OpenRevisionFile, OpenRevisionFileWith,
    EditWorkingFile, SaveAs, RenameMove, DeleteFile,
    CopyRelativePosix, CopyRelativeNative, CopyFullNative, ShowInFolder,
    ShowInFileTree, FilterInGrid, FileHistory, Blame, FindFile, FindInCommitFiles, ToggleFindInCommitFiles,
    AddToIgnore, AddToLocalExclude, ToggleSkipWorktree, ToggleAssumeUnchanged, StopTracking,
    SelectAll, CollapseAll, ExpandAll, CollapseRootFolders, DisableCustomDiffTools
}

/// <summary>Captured endpoints and native file facts belong to this leaf, including when paths repeat in different groups.</summary>
internal sealed record FileMenuLeaf(GitRevisionFile File, GitCompareRevision? First, GitCompareRevision Second,
    GitComparison? Comparison = null, FileMenuStage Stage = FileMenuStage.None, bool IsTracked = true,
    bool IsSubmodule = false, bool IsSkipWorktree = false, bool IsAssumeUnchanged = false, bool FirstIsParent = false);

internal sealed record FileMenuSelection(GitRepository Repository, IReadOnlyList<FileMenuLeaf> Leaves,
    IReadOnlyList<string> CopyPaths, string? Folder = null, bool IsFileTree = false, bool IsDisplayOnly = false,
    object? HostIdentity = null, FileMenuLeaf? FocusedLeaf = null)
{
    internal string? CustomDiffTool { get; init; }
    internal GitRevisionFile[] Files => Leaves.Select(leaf => leaf.File).ToArray();
    internal string[] Paths => Leaves.Select(leaf => leaf.File.Path).Distinct(StringComparer.Ordinal).ToArray();
    internal string[] ResetPaths => Leaves.SelectMany(leaf => leaf.File.Status == 'R' && leaf.File.OriginalPath is not null
        ? new[] { leaf.File.Path, leaf.File.OriginalPath } : [leaf.File.Path]).Distinct(StringComparer.Ordinal).ToArray();
}

internal sealed record FileMenuCapabilities(bool ShowInFileTree = false, bool FilterInGrid = false,
    bool Grep = false, bool SupportLinePatching = false, bool CherryPick = false,
    bool VisualStudio = false, bool Scripts = false);

internal delegate MenuItem FileMenuActionFactory(string category, string name, string label, string? icon,
    Func<Task> action, KeyGesture? gesture);

/// <summary>Hosts retain their operation/error/focus scopes. The controller never owns a repository or starts Git.</summary>
internal sealed class FileMenuHost
{
    internal required Func<FileMenuSelection?> CaptureSelection { get; init; }
    internal required Func<FileMenuSelection, bool> IsCurrent { get; init; }
    internal required Func<FileMenuCommand, FileMenuSelection, Task> ExecuteAsync { get; init; }
    internal required FileMenuActionFactory CreateAction { get; init; }
    internal required Func<bool> IsBusy { get; init; }
    internal FileMenuCapabilities Capabilities { get; init; } = new();
    internal Func<bool> GrepVisible { get; init; } = () => false;
    internal Func<FileMenuLeaf?> GetRemembered { get; init; } = () => null;
    internal Func<IReadOnlyList<string>> GetCustomDiffTools { get; init; } = () => [];
}
