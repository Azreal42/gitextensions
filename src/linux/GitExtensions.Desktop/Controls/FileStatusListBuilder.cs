using GitExtensions.Extensibility.Git;

namespace GitExtensions.Desktop.Controls;

/// <summary>Pure counterpart of FileStatusList.GetNodes and StatusSorter, with literal leaf identities.</summary>
public static class FileStatusListBuilder
{
    public static IReadOnlyList<FileStatusListNode> Build(IReadOnlyList<FileStatusListModelGroup> groups,
        FileStatusListOptions options, Func<FileStatusListEntry, bool>? matches = null, bool groupByRevision = false)
    {
        ArgumentNullException.ThrowIfNull(groups); ArgumentNullException.ThrowIfNull(options);
        List<FileStatusListNode> roots = [];
        bool showRevisionGroups = groups.Count > 1 || groupByRevision && groups.Any(group => group.Entries.Count > 0);
        foreach (FileStatusListModelGroup group in groups)
        {
            FileStatusListEntry[] entries = group.Entries.Where(entry => group.AlwaysVisible || matches?.Invoke(entry) != false).ToArray();
            IReadOnlyList<FileStatusListNode> children = BuildChildren(group.Key, entries, options);
            if (showRevisionGroups && group.Entries.Count == 0 && !group.AlwaysVisible)
            { children = [new(FileStatusListNodeKind.Placeholder, group.Key, "NoChanges", "- No changes -", new FileStatusListNodeTag(group.Key, FileStatusListNodeKind.Placeholder, "NoChanges"), "FileStatusCopiedSame.png", [])]; }
            if (showRevisionGroups || group.AlwaysVisible)
            {
                roots.Add(new(FileStatusListNodeKind.Revision, group.Key, group.Key, "", group.Tag, "Diff.png", children));
            }
            else { roots.AddRange(children); }
        }
        return roots;
    }

    public static IEnumerable<FileStatusListNode> Descendants(IEnumerable<FileStatusListNode> nodes)
    {
        foreach (FileStatusListNode node in nodes)
        {
            yield return node;
            foreach (FileStatusListNode child in Descendants(node.Children)) { yield return child; }
        }
    }

    public static string IconName(FileStatusListEntry entry)
    {
        string basis = entry.Status switch
        {
            'A' or '?' => "FileStatusAdded", 'D' => "FileStatusRemoved", 'U' => "Unmerged",
            'R' when entry.SimilarityPercent == 100 => "FileStatusRenamed", 'C' => "FileStatusCopied",
            'M' or 'T' or 'R' => "FileStatusModified", _ => "FileStatusUnknown"
        };
        string suffix = basis is "Unmerged" or "FileStatusUnknown" ? "" : entry.DiffStatus switch
        {
            DiffBranchStatus.UnequalChange => "Unequal", DiffBranchStatus.OnlyBChange => "OnlyB",
            DiffBranchStatus.OnlyAChange => "OnlyA", DiffBranchStatus.SameChange => "Same", _ => ""
        };
        return basis + suffix + ".png";
    }

    private static IReadOnlyList<FileStatusListNode> BuildChildren(string groupKey, IReadOnlyList<FileStatusListEntry> entries, FileStatusListOptions options)
    {
        bool flat = options.SortMode is FileStatusListSortMode.FilePathFlat or FileStatusListSortMode.FileExtensionFlat or FileStatusListSortMode.FileStatusFlat;
        if (options.SortMode is FileStatusListSortMode.FilePath or FileStatusListSortMode.FilePathFlat)
        { return BuildPaths(groupKey, entries, flat, options.DenseTree); }
        bool extensions = options.SortMode is FileStatusListSortMode.FileExtension or FileStatusListSortMode.FileExtensionFlat;
        bool showGroupNodes = !flat || options.ShowGroupNodesInFlatList;
        List<FileStatusListNode> nodes = [];
        foreach (IGrouping<string, FileStatusListEntry> group in entries.GroupBy(entry => extensions ? Path.GetExtension(entry.Path) : StatusKey(entry))
            .OrderBy(group => group.Key, StringComparer.InvariantCulture))
        {
            IReadOnlyList<FileStatusListNode> paths = BuildPaths(groupKey, group.ToArray(), flat, options.DenseTree);
            if (!showGroupNodes || paths.Count == 1 && paths[0].Children.Count == 0) { nodes.AddRange(paths); }
            else
            {
                FileStatusListNodeKind kind = extensions ? FileStatusListNodeKind.Extension : FileStatusListNodeKind.Status;
                nodes.Add(new(kind, groupKey, group.Key, extensions ? group.Key : "", new FileStatusListNodeTag(groupKey, kind, group.Key),
                    extensions ? "File.png" : IconName(group.First()), paths));
            }
        }
        // The original removes a redundant sole grouping header before applying the revision header.
        return nodes.Count == 1 && nodes[0].Children.Count > 0 ? nodes[0].Children : nodes;
    }

    private static string StatusKey(FileStatusListEntry entry)
    {
        // Image-list order in FileStatusList.cs: Unknown, Unmerged, Modified, Copied, Renamed, Added, Removed.
        int rank = entry.Status switch
        {
            'U' => 2, 'M' or 'T' => 3, 'R' when entry.SimilarityPercent != 100 => 3,
            'C' => 8, 'R' => 13, 'A' or '?' => 18, 'D' => 23, _ => 1
        };
        int variant = entry.DiffStatus switch
        {
            DiffBranchStatus.UnequalChange => 0, DiffBranchStatus.OnlyBChange => 1,
            DiffBranchStatus.OnlyAChange => 2, DiffBranchStatus.SameChange => 3, _ => 4
        };
        if (rank > 2) { rank += variant; }
        return $"{(char)('Z' - (int)entry.DiffStatus)}{rank:D02}";
    }

    private static IReadOnlyList<FileStatusListNode> BuildPaths(string groupKey, IEnumerable<FileStatusListEntry> entries, bool flat, bool dense)
    {
        FileStatusListEntry[] sorted = entries.OrderBy(entry => entry, EntryPathComparer.Instance).ToArray();
        if (flat) { return sorted.Select(entry => Leaf(groupKey, entry, entry.Path)).ToArray(); }
        Folder root = new("");
        foreach (FileStatusListEntry entry in sorted)
        {
            string directory = ParentPath(entry.Path);
            Folder parent = root;
            if (directory.Length > 0)
            {
                string path = "";
                foreach (string segment in directory.Split('/'))
                {
                    path = path.Length == 0 ? segment : path + "/" + segment;
                    if (!parent.Folders.TryGetValue(segment, out Folder? next)) { parent.Folders[segment] = next = new(path); }
                    parent = next;
                }
            }
            parent.Entries.Add(entry);
        }
        return Freeze(root);

        IReadOnlyList<FileStatusListNode> Freeze(Folder folder)
        {
            List<FileStatusListNode> result = [];
            foreach (Folder child in folder.Folders.Values.OrderBy(child => child.Path, StringComparer.InvariantCulture))
            {
                Folder compressed = child;
                while (compressed.Entries.Count == 0 && compressed.Folders.Count == 1) { compressed = compressed.Folders.Values.Single(); }
                if (dense && compressed.Folders.Count == 0 && compressed.Entries.Count == 1)
                { result.Add(Leaf(groupKey, compressed.Entries[0], Relative(compressed.Entries[0].Path, folder.Path))); }
                else
                {
                    result.Add(new(FileStatusListNodeKind.Folder, groupKey, compressed.Path, Relative(compressed.Path, folder.Path),
                        new FileStatusListNodeTag(groupKey, FileStatusListNodeKind.Folder, compressed.Path), "FolderClosed.png", Freeze(compressed)));
                }
            }
            result.AddRange(folder.Entries.OrderBy(entry => entry.Path, StringComparer.InvariantCulture).Select(entry => Leaf(groupKey, entry, Relative(entry.Path, folder.Path))));
            return result;
        }
    }

    private static FileStatusListNode Leaf(string group, FileStatusListEntry entry, string label)
        => new(FileStatusListNodeKind.File, group, entry.Path, label, entry.Tag, IconName(entry), [], entry);
    private static string Relative(string path, string parent)
        => parent.Length == 0 ? path : path.StartsWith(parent + "/", StringComparison.Ordinal) ? path[(parent.Length + 1)..] : path;
    private static string ParentPath(string path)
    {
        int end = path.Length - 1;
        if (end >= 0 && path[end] == '/') { end--; }
        int separator = end >= 0 ? path.LastIndexOf('/', end) : -1;
        return separator > 0 ? path[..separator] : "";
    }
    private sealed class Folder(string path)
    {
        internal string Path { get; } = path;
        internal Dictionary<string, Folder> Folders { get; } = new(StringComparer.Ordinal);
        internal List<FileStatusListEntry> Entries { get; } = [];
    }
    private sealed class EntryPathComparer : IComparer<FileStatusListEntry>
    {
        internal static EntryPathComparer Instance { get; } = new();
        public int Compare(FileStatusListEntry? left, FileStatusListEntry? right)
        {
            if (left is null || right is null) { return left is null ? right is null ? 0 : 1 : -1; }
            string l = ParentPath(left.Path), r = ParentPath(right.Path);
            int result = l.Length == 0 || r.Length == 0 ? l.Length == r.Length ? 0 : l.Length == 0 ? 1 : -1 : CompareDirectory(l, r);
            if (result < 0 && r.StartsWith(l + "/", StringComparison.Ordinal)) { return 1; }
            if (result > 0 && l.StartsWith(r + "/", StringComparison.Ordinal)) { return -1; }
            return result == 0 ? StringComparer.InvariantCulture.Compare(left.Path, right.Path) : result;
        }
        private static int CompareDirectory(string left, string right)
        {
            string[] l = left.Split('/'), r = right.Split('/');
            for (int i = 0; i < Math.Min(l.Length, r.Length); i++)
            {
                int result = StringComparer.InvariantCulture.Compare(l[i], r[i]);
                if (result != 0) { return Math.Sign(result); }
            }
            return Math.Sign(l.Length.CompareTo(r.Length));
        }
    }
}
