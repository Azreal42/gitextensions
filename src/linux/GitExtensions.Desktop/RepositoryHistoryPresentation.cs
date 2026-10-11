namespace GitExtensions.Linux.Desktop;

public sealed record RepositoryHistoryRow(RepositoryHistoryEntry Entry, string Caption, bool IsTop)
{
    public string Path => Entry.Path;
    public bool IsAnchored => Entry.EffectiveAnchor != RepositoryHistoryAnchor.None;
}

public sealed record RepositoryHistorySelection(IReadOnlyList<RepositoryHistoryRow> Top, IReadOnlyList<RepositoryHistoryRow> Recent);

/// <summary>Portable selection and caption rules from upstream RecentRepoSplitter.</summary>
public static class RepositoryHistoryPresentation
{
    public static RepositoryHistorySelection Select(RepositoryHistoryState state, Func<string, double>? measureText = null, string? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        string home = (homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd(Path.DirectorySeparatorChar);
        List<Info> entries = state.Recent.Select(entry => new Info(entry)).ToList();
        int maximum = Math.Min(Math.Max(0, state.MaxTopRepositories), entries.Count);
        int selected = 0;
        foreach (Info info in entries)
        {
            RepositoryHistoryAnchor anchor = info.Entry.EffectiveAnchor;
            info.IsTop = anchor == RepositoryHistoryAnchor.Top || (anchor == RepositoryHistoryAnchor.None && selected < maximum);
            if (info.IsTop) { selected++; }
        }
        // Explicit Top anchors have priority even if they occur after the newest entries.
        for (int index = entries.Count - 1; selected > maximum && index >= 0; index--)
        {
            if (entries[index].IsTop && entries[index].Entry.EffectiveAnchor != RepositoryHistoryAnchor.Top)
            { entries[index].IsTop = false; selected--; }
        }

        if (state.ShorteningStrategy == RepositoryPathShorteningStrategy.MostSignDir)
        {
            SortedList<string, List<Info>> ordered = new(StringComparer.CurrentCulture);
            foreach (Info info in entries) { AddSignificant(ordered, info); }
        }
        else
        {
            foreach (Info info in entries)
            {
                info.Caption = state.ShorteningStrategy == RepositoryPathShorteningStrategy.MiddleDots
                    ? MiddleDots(info.Entry.Path, state.RecentReposComboMinWidth, measureText ?? (text => text.Length * 7d), home)
                    : info.Entry.Path;
            }
        }
        foreach (Info info in entries) { info.SortCaption = info.Caption; info.Caption = DisplayPath(info.Caption, home); }
        IEnumerable<Info> top = entries.Where(info => info.IsTop);
        IEnumerable<Info> recent = entries.Where(info => !state.HideTopRepositoriesFromRecentList || !info.IsTop);
        if (state.SortTopRepos) { top = top.OrderBy(info => info.SortCaption, StringComparer.CurrentCulture); }
        if (state.SortRecent) { recent = recent.OrderBy(info => info.SortCaption, StringComparer.CurrentCulture); }
        static RepositoryHistoryRow Row(Info info) => new(info.Entry, info.Caption, info.IsTop);
        return new(top.Select(Row).ToArray(), recent.Select(Row).ToArray());
    }

    public static string DisplayPath(string path, string? homeDirectory = null)
    {
        string home = (homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd(Path.DirectorySeparatorChar);
        if (home.Length > 0 && (path == home || path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        { return "~" + path[home.Length..].TrimEnd(Path.DirectorySeparatorChar); }
        return path;
    }

    private static void AddSignificant(SortedList<string, List<Info>> ordered, Info info)
    {
        if (info.Parent is not null)
        {
            string suffix = info.ParentPath[info.Parent.FullName.Length..].Trim(Path.DirectorySeparatorChar);
            info.Caption = info.ShortName + (suffix.Length == 0 ? "" : " (" + suffix + ")");
            info.Parent = info.Parent.Parent;
        }
        else { info.Caption = info.Entry.Path; }
        bool exists = ordered.TryGetValue(info.Caption, out List<Info>? sameCaption);
        if (!exists) { ordered.Add(info.Caption, sameCaption = []); }
        List<Info> retry = [];
        if (exists)
        {
            for (int index = sameCaption!.Count - 1; index >= 0; index--)
            {
                Info previous = sameCaption[index];
                if (previous.Parent is not null) { retry.Add(previous); sameCaption.RemoveAt(index); }
            }
        }
        if (info.Parent is null || !exists) { sameCaption!.Add(info); }
        else { retry.Add(info); }
        foreach (Info previous in retry) { AddSignificant(ordered, previous); }
    }

    private static string MiddleDots(string path, int width, Func<string, double> measure, string home)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(path);
            if (!string.Equals(directory.FullName, path, StringComparison.Ordinal)) { return path; }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return path; }
        string? root = null, company = null, repository = null;
        string workingDirectory = directory.Name;
        directory = directory.Parent;
        if (directory is not null) { repository = directory.Name; directory = directory.Parent; }
        bool dots = false;
        bool underHome = home.Length > 0 && (path == home || path.StartsWith(home.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (directory is not null)
        {
            if (directory.FullName != home)
            {
                while (directory.Parent?.Parent is not null && underHome && directory.Parent.FullName != home)
                { directory = directory.Parent; dots = true; }
                company = directory.Name;
            }
            if (underHome) { root = "~" + Path.DirectorySeparatorChar; }
            else if (directory.Parent is not null) { root = directory.Parent.Name; }
        }
        static string Join(string? left, string? right) => left is null ? right ?? "" : right is null ? left : Path.Combine(left, right);
        if (width == 0)
        {
            string? caption = company is null ? root : Join(root, company);
            if (dots) { caption = Join(caption, ".."); }
            return Join(Join(caption, repository), workingDirectory);
        }
        string value = path;
        string? first = root ?? company ?? repository;
        for (int skip = 0; first is not null && path.Length - workingDirectory.Length - first.Length - skip > 0; skip++)
        {
            int middle = (path.Length - workingDirectory.Length + 1) / 2;
            int left = middle - skip / 2;
            int right = middle + skip / 2 + skip % 2;
            if (left != right)
            {
                if (left > 0 && char.IsHighSurrogate(path[left - 1])) { left--; }
                if (right < path.Length && char.IsLowSurrogate(path[right])) { right++; }
            }
            value = left == right ? path : path[..left] + ".." + path[right..];
            if (measure(value) <= width - 10) { break; }
        }
        return value;
    }

    private sealed class Info
    {
        public RepositoryHistoryEntry Entry { get; }
        public string Caption { get; set; }
        public string SortCaption { get; set; } = "";
        public bool IsTop { get; set; }
        public DirectoryInfo? Parent { get; set; }
        public string ParentPath { get; } = "";
        public string ShortName { get; } = "";
        public Info(RepositoryHistoryEntry entry)
        {
            Entry = entry; Caption = entry.Path;
            try
            {
                DirectoryInfo directory = new(entry.Path);
                ShortName = directory.Name; Parent = directory.Parent; ParentPath = Parent?.FullName ?? "";
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { }
        }
    }
}
