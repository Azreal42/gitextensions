using GitExtensions.Desktop.Controls;
using GitExtensions.Extensibility.Git;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public class FileStatusListBuilderTests
{
    [TestCase(FileStatusListSortMode.FilePath)]
    [TestCase(FileStatusListSortMode.FilePathFlat)]
    [TestCase(FileStatusListSortMode.FileExtension)]
    [TestCase(FileStatusListSortMode.FileExtensionFlat)]
    [TestCase(FileStatusListSortMode.FileStatus)]
    [TestCase(FileStatusListSortMode.FileStatusFlat)]
    public void SixModesActuallyGroupAndSortFoldersExtensionsOrStatusesWhileKeepingLiteralTags(FileStatusListSortMode mode)
    {
        FileStatusListEntry[] entries = [Entry("src/other.cs", 'M'), Entry("doc/n2.txt", 'A'), Entry("src/done.cs", 'M'), Entry("doc/n1.txt", 'A')];
        IReadOnlyList<FileStatusListNode> nodes = Build(entries, new(mode, ShowGroupNodesInFlatList: true));
        FileStatusListNode[] all = FileStatusListBuilder.Descendants(nodes).ToArray();
        string[] paths = all.Where(node => node.Entry is not null).Select(node => node.Entry!.Path).ToArray();
        Assert.That(paths, Is.EqualTo(mode is FileStatusListSortMode.FilePath or FileStatusListSortMode.FilePathFlat
            ? new[] { "doc/n1.txt", "doc/n2.txt", "src/done.cs", "src/other.cs" }
            : new[] { "src/done.cs", "src/other.cs", "doc/n1.txt", "doc/n2.txt" }));
        foreach (FileStatusListNode node in all.Where(node => node.Entry is not null))
        { Assert.That(node.Tag, Is.SameAs(entries.Single(entry => entry.Path == node.Entry!.Path).Tag)); }
        bool flat = mode is FileStatusListSortMode.FilePathFlat or FileStatusListSortMode.FileExtensionFlat or FileStatusListSortMode.FileStatusFlat;
        Assert.That(all.Any(node => node.Kind == FileStatusListNodeKind.Folder), Is.EqualTo(!flat));
        Assert.That(all.Where(node => node.Entry is not null).Select(node => node.Label), Is.EqualTo(flat ? paths : paths.Select(Path.GetFileName)));
        if (mode is FileStatusListSortMode.FileExtension or FileStatusListSortMode.FileExtensionFlat)
        { Assert.That(nodes.Select(node => node.Label), Is.EqualTo(new[] { ".cs", ".txt" })); }
        if (mode is FileStatusListSortMode.FileStatus or FileStatusListSortMode.FileStatusFlat)
        { Assert.That(nodes.Select(node => node.Icon), Is.EqualTo(new[] { "FileStatusModified.png", "FileStatusAdded.png" })); }
    }

    [Test]
    public void CommonPathsDenseTreeAndSubmoduleNamesFollowHistoricalSorterCases()
    {
        FileStatusListEntry[] entries = [Entry("core/c.1"), Entry("core.dot/cd.3"), Entry("core/api/c_a.0"), Entry("core/c.2"), Entry("root_file"), Entry("ext/s/"), Entry(@"literal\backslash.txt")];
        IReadOnlyList<FileStatusListNode> tree = Build(entries, new());
        Assert.That(tree.Take(3).Select(node => node.Label), Is.EqualTo(new[] { "core", "core.dot", "ext" }));
        Assert.That(tree[0].Children.Select(node => node.Label), Is.EqualTo(new[] { "api", "c.1", "c.2" }));
        Assert.That(tree[2].Children.Single().Label, Is.EqualTo("s/"));
        Assert.That(tree.Last().Kind, Is.EqualTo(FileStatusListNodeKind.File));
        FileStatusListNode backslash = FileStatusListBuilder.Descendants(tree).Single(node => node.Entry?.Path == @"literal\backslash.txt");
        Assert.That(backslash.Label, Is.EqualTo(@"literal\backslash.txt"));
        FileStatusListNode[] dense = Build([Entry("1/2/file12"), Entry("1/3/file13"), Entry("1/4/file14")], new(DenseTree: true)).ToArray();
        Assert.That(dense.Single().Label, Is.EqualTo("1"));
        Assert.That(dense.Single().Children.Select(node => node.Label), Is.EqualTo(new[] { "2/file12", "3/file13", "4/file14" }));
        Assert.That(dense.Single().Children.All(node => node.Entry is not null), Is.True);
        Assert.That(Build([Entry("root_file")], new(DenseTree: true)).Single().Label, Is.EqualTo("root_file"));
    }

    [Test]
    public void FlatGroupingOptionsAndSingleGroupPromotionMatchOriginal()
    {
        FileStatusListEntry[] entries = [Entry("src/a.cs"), Entry("src/b.cs"), Entry("doc/a.txt"), Entry("doc/b.txt")];
        Assert.That(Build(entries, new(FileStatusListSortMode.FileExtensionFlat)).All(node => node.Kind == FileStatusListNodeKind.File), Is.True);
        Assert.That(Build(entries, new(FileStatusListSortMode.FileExtensionFlat, ShowGroupNodesInFlatList: true)).Select(node => node.Label), Is.EqualTo(new[] { ".cs", ".txt" }));
        Assert.That(Build(entries.Take(2).ToArray(), new(FileStatusListSortMode.FileExtension)).Single().Kind, Is.EqualTo(FileStatusListNodeKind.Folder), "A redundant sole extension heading is removed, retaining its folder tree.");
        Assert.That(Build([Entry("src/a.cs"), Entry("doc/a.txt")], new(FileStatusListSortMode.FileExtensionFlat, ShowGroupNodesInFlatList: true)).All(node => node.Entry is not null), Is.True, "Single-file groups are represented by their file.");
    }

    [Test]
    public void StatusPriorityAndRenameSimilarityUseHistoricalIconsInsteadOfAlphabeticalGitLetters()
    {
        FileStatusListEntry[] entries = [Entry("removed", 'D'), Entry("added", 'A'), Entry("rename-exact", 'R', similarity: 100), Entry("copied", 'C', similarity: 72), Entry("rename-edited", 'R', similarity: 99), Entry("modified", 'M'), Entry("unmerged", 'U')];
        Assert.That(Build(entries, new(FileStatusListSortMode.FileStatusFlat)).Select(node => node.Entry!.Path),
            Is.EqualTo(new[] { "unmerged", "modified", "rename-edited", "copied", "rename-exact", "added", "removed" }));
        Assert.That(FileStatusListBuilder.IconName(entries.Single(entry => entry.Path == "rename-edited")), Is.EqualTo("FileStatusModified.png"));
        Assert.That(FileStatusListBuilder.IconName(entries.Single(entry => entry.Path == "rename-exact")), Is.EqualTo("FileStatusRenamed.png"));
        FileStatusListEntry[] branchEntries = [Entry("same", diff: DiffBranchStatus.SameChange), Entry("A", diff: DiffBranchStatus.OnlyAChange), Entry("B", diff: DiffBranchStatus.OnlyBChange), Entry("different", diff: DiffBranchStatus.UnequalChange)];
        Assert.That(Build(branchEntries, new(FileStatusListSortMode.FileStatusFlat)).Select(node => node.Icon), Is.EqualTo(new[] { "FileStatusModifiedUnequal.png", "FileStatusModifiedOnlyB.png", "FileStatusModifiedOnlyA.png", "FileStatusModifiedSame.png" }));
        Assert.That(new FileStatusListDiffFilter(false, false, false, false).Matches(DiffBranchStatus.Unknown), Is.True);
    }

    [Test]
    public void RevisionHeadersRangeAndNoChangesPlaceholdersStaySeparateFromMutationLeaves()
    {
        object revision = new(), range = new();
        IReadOnlyList<FileStatusListNode> nodes = FileStatusListBuilder.Build([
            new(revision, "empty", []), new(new object(), "hidden", [Entry("hidden.txt")]), new(range, "range", [], AlwaysVisible: true)], new(), _ => false);
        Assert.That(nodes.Count, Is.EqualTo(3)); Assert.That(nodes[0].Tag, Is.SameAs(revision)); Assert.That(nodes[2].Tag, Is.SameAs(range));
        Assert.That(nodes[0].Children.Single().Kind, Is.EqualTo(FileStatusListNodeKind.Placeholder));
        Assert.That(nodes[1].Children, Is.Empty, "A filtered-out real group has zero shown files, rather than a fake no-changes file.");
        Assert.That(FileStatusListBuilder.Descendants(nodes).Where(node => node.Entry is not null), Is.Empty);
    }

    private static FileStatusListEntry Entry(string path, char status = 'M', int? similarity = null, DiffBranchStatus diff = DiffBranchStatus.Unknown)
        => new(new object(), path, Status: status, SimilarityPercent: similarity, DiffStatus: diff);
    private static IReadOnlyList<FileStatusListNode> Build(FileStatusListEntry[] entries, FileStatusListOptions options)
        => FileStatusListBuilder.Build([new(new object(), "files", entries)], options);
}
