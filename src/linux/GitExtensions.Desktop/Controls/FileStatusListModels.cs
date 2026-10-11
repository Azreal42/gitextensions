using GitExtensions.Extensibility.Git;

namespace GitExtensions.Desktop.Controls;

// The six modes and their order are the original DiffListSortType contract.
public enum FileStatusListSortMode { FilePath, FilePathFlat, FileExtension, FileExtensionFlat, FileStatus, FileStatusFlat }

public sealed record FileStatusListEntry(object Tag, string Path, string? OriginalPath = null, char Status = 'M',
    int? SimilarityPercent = null, DiffBranchStatus DiffStatus = DiffBranchStatus.Unknown)
{
    // This presentation-only value is supplied to the header factory. Path and Tag remain literal.
    public string? DisplayPath { get; init; }
}

public sealed record FileStatusListOptions(FileStatusListSortMode SortMode = FileStatusListSortMode.FilePath,
    bool DenseTree = false, bool ShowGroupNodesInFlatList = false, bool FilterFileNameOnly = false)
{
    public IReadOnlyList<string> HiddenToolbarItems { get; init; } = [];
}

public sealed record FileStatusListModelGroup(object Tag, string Key, IReadOnlyList<FileStatusListEntry> Entries,
    bool AlwaysVisible = false, bool IsBaseComparison = false);

public enum FileStatusListNodeKind { Revision, Folder, Extension, Status, File, Placeholder }
public sealed record FileStatusListNodeTag(string GroupKey, FileStatusListNodeKind Kind, string Key);
public sealed record FileStatusListNode(FileStatusListNodeKind Kind, string GroupKey, string Key, string Label,
    object Tag, string Icon, IReadOnlyList<FileStatusListNode> Children, FileStatusListEntry? Entry = null);

public sealed record FileStatusListDiffFilter(bool Unequal = true, bool OnlyB = true, bool OnlyA = true, bool Same = true)
{
    public bool Matches(DiffBranchStatus status) => status switch
    {
        DiffBranchStatus.UnequalChange => Unequal, DiffBranchStatus.OnlyBChange => OnlyB,
        DiffBranchStatus.OnlyAChange => OnlyA, DiffBranchStatus.SameChange => Same, _ => true
    };
}
