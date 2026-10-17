namespace GitExtensions.Linux.Desktop;

/// <summary>Selection predicates from Windows RevisionDiffController and FileStatusList.ContextMenu.</summary>
internal sealed class FileMenuPolicy(FileMenuSelection selection)
{
    internal FileMenuSelection Selection { get; } = selection;
    internal bool Any => Selection.Leaves.Count > 0;
    internal bool DisplayOnly => Selection.IsDisplayOnly;
    internal bool Bare => Selection.Repository.IsBare;
    internal bool Single => Selection.Leaves.Count == 1 && Selection.Folder is null;
    internal bool Tracked => Selection.Leaves.Any(leaf => leaf.IsTracked);
    internal bool Submodule => Selection.Leaves.Any(leaf => leaf.IsSubmodule);
    internal bool Index => Selection.Leaves.Any(leaf => leaf.Stage == FileMenuStage.Index);
    internal bool WorkTree => Selection.Leaves.Any(leaf => leaf.Stage == FileMenuStage.WorkingTree);
    internal bool Deleted => Selection.Leaves.Any(leaf => leaf.File.IsDeleted);
    internal bool AllFilesExist => Any && Selection.Paths.All(path => File.Exists(Path.Combine(Selection.Repository.Path, path)));
    internal bool AnyLocalFileExists => Selection.Paths.Any(path => File.Exists(Path.Combine(Selection.Repository.Path, path)));
    internal bool AllDirectoriesExist => Any && Selection.Paths.All(path => Directory.Exists(Path.Combine(Selection.Repository.Path, path)));
    internal bool AllFilesOrUntrackedDirectoriesExist => Any && Selection.Leaves.All(leaf => File.Exists(Path.Combine(Selection.Repository.Path, leaf.File.Path))
        || !leaf.IsTracked && Directory.Exists(Path.Combine(Selection.Repository.Path, leaf.File.Path)));
    internal GitCompareRevision? Second => Selection.Leaves.Select(leaf => leaf.Second).Distinct().Take(2).ToArray() is [var second] ? second : null;
    internal GitCompareRevision[] First => Selection.Leaves.Select(leaf => leaf.First).OfType<GitCompareRevision>().Distinct().ToArray();
    internal bool Artificial => Second?.Kind is GitCompareRevisionKind.Index or GitCompareRevisionKind.WorkingTree;
    internal bool CanNativeDiff => !Bare || Any && Selection.Leaves.All(leaf => leaf.First?.Kind == GitCompareRevisionKind.Commit && leaf.Second.Kind == GitCompareRevisionKind.Commit);
    internal bool CanResetSecond => Any && Selection.Leaves.Any(leaf => leaf.Second.Kind == GitCompareRevisionKind.Commit);
    internal bool CanResetFirst => First.Any(first => first.Kind == GitCompareRevisionKind.Commit)
        || First.Any(first => first.Kind == GitCompareRevisionKind.Index) && Selection.Leaves.All(leaf => leaf.Second.Kind == GitCompareRevisionKind.WorkingTree);
    internal bool CanReset => Any && !Bare && Tracked && !DisplayOnly && !(Submodule && Single && WorkTree) && (CanResetFirst || CanResetSecond);
    internal bool CanOpenRevision => Single && !Submodule && !DisplayOnly && !Artificial && !Deleted;
    internal bool CanSave => Any && !Submodule && Second is { Kind: GitCompareRevisionKind.Commit } && !DisplayOnly && !Deleted;
    internal bool CanHistory => (Single || !string.IsNullOrEmpty(Selection.Folder)) && Tracked;
    internal bool CanBlame => CanHistory && !Submodule && Selection.Folder is null;
    internal bool CanMove => Single && Tracked && !Submodule || Selection.Folder is not null;
    internal bool CanShowInTree => Single && Tracked && !Deleted || Selection.Folder is not null;
    internal bool CanSubmodule => Submodule && Second?.Kind == GitCompareRevisionKind.WorkingTree && AllDirectoriesExist;
    internal bool HideLocalDiffs => Second?.Kind == GitCompareRevisionKind.WorkingTree && First.All(first => first.Kind == GitCompareRevisionKind.Index)
        || Second?.Kind == GitCompareRevisionKind.Index && First.All(first => first.Kind == GitCompareRevisionKind.WorkingTree);
    internal bool CanDiffSecondLocal => Second is not null && AnyLocalFileExists && !Selection.Leaves.All(leaf => leaf.File.IsDeleted) && Second.Kind != GitCompareRevisionKind.WorkingTree;
    internal bool CanDiffFirstLocal => Second is not null && AnyLocalFileExists && (!Selection.Leaves.All(leaf => leaf.FirstIsParent) || !Selection.Leaves.All(leaf => leaf.File.Status == 'A'))
        && !First.Any(first => first.Kind == GitCompareRevisionKind.WorkingTree);
}
