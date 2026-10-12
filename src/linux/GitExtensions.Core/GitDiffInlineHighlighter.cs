namespace GitExtensions.Linux;

/// <summary>Portable DiffHighlightService intraline markers. Caller supplies parsed line roles;
/// the source text, including combined/range prefixes and line endings, is never rewritten.</summary>
public static class GitDiffInlineHighlighter
{
    public static IReadOnlyList<GitDiffColorSpan> Highlight(string text, IReadOnlyList<GitDiffLineDescriptor> descriptors,
        GitDiffColorPalette palette, bool dimBackground = true)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(palette); palette.Validate();
        foreach (GitDiffLineDescriptor line in descriptors)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (!Enum.IsDefined(line.Kind) || line.Offset < 0 || line.Offset > text.Length || line.Length < 0
                || line.Length > text.Length - line.Offset || line.ContentOffset < 0
                || line.Kind is GitDiffLineKind.Added or GitDiffLineKind.Removed && line.ContentOffset > line.Length)
            { throw new ArgumentException("Diff descriptors must refer to valid UTF16 segments and content prefixes.", nameof(descriptors)); }
        }
        GitDiffLineDescriptor[] lines = [.. descriptors.OrderBy(line => line.Offset)];
        List<GitDiffColorSpan> spans = [];
        int index = 0;
        while (index < lines.Length)
        {
            var removed = Block(GitDiffLineKind.Removed, false);
            if (removed.Count == 0) { continue; }
            var added = Block(GitDiffLineKind.Added, true);
            if (added.Count == 0) { continue; }
            foreach (var pair in GitDiffLineMatcher.FindPairs(Content, removed, added))
            {
                ReadOnlySpan<char> r = Limit(Content(pair.Removed)), a = Limit(Content(pair.Added));
                int rOffset = pair.Removed.Offset + pair.Removed.ContentOffset, aOffset = pair.Added.Offset + pair.Added.ContentOffset;
                (int prefix, int suffix) = Difference(r, a, rOffset, aOffset);
                if (prefix > 0) { Dim(rOffset, prefix, true); Dim(aOffset, prefix, false); }
                if (suffix > 0) { Dim(rOffset + r.Length - suffix, suffix, true); Dim(aOffset + a.Length - suffix, suffix, false); }
            }
        }
        return spans;

        string Content(GitDiffLineDescriptor line) => text.Substring(line.Offset + line.ContentOffset, line.Length - line.ContentOffset);
        static ReadOnlySpan<char> Limit(string value) => value.AsSpan(0, Math.Min(2000, value.Length));
        List<GitDiffLineDescriptor> Block(GitDiffLineKind kind, bool found)
        {
            List<GitDiffLineDescriptor> result = []; int gap = 0;
            for (; index < lines.Length; index++)
            {
                GitDiffLineDescriptor line = lines[index];
                if (line.Kind != kind)
                {
                    if (!found) { continue; }
                    if (line.Kind == GitDiffLineKind.Context && gap < 5) { gap++; continue; }
                    break;
                }
                gap = 0;
                if (line.IsMoved) { continue; }
                found = true; result.Add(line);
            }
            return result;
        }
        void Dim(int offset, int length, bool removed)
        {
            GitDiffColor foreground = palette.Foregrounds[removed ? 9 : 10];
            GitDiffColor background = palette.EditorBackground;
            if (dimBackground)
            {
                background = GitDiffColorMath.Dim(GitDiffColorMath.Dim(palette.Backgrounds[removed ? 1 : 2], background), background);
                foreground = GitDiffColorMath.TextColor(background, palette.EditorForeground);
            }
            else { foreground = GitDiffColorMath.Dim(foreground, background); }
            spans.Add(new(offset, length, background, foreground));
        }
        (int Prefix, int Suffix) Difference(ReadOnlySpan<char> removed, ReadOnlySpan<char> added, int rOffset, int aOffset)
        {
            int prefix = 0, suffix = 0;
            if (removed.SequenceEqual(added)) { return (removed.Length, 0); }
            (string? word, int rStart, int aStart) = GitDiffLineMatcher.BestWord(removed.ToString(), added.ToString());
            if (word is not null)
            {
                int length = word.Length;
                int rRight = rStart + length, aRight = aStart + length;
                (int rightPrefix, suffix) = Difference(removed[rRight..], added[aRight..], rOffset + rRight, aOffset + aRight);
                length += rightPrefix;
                (prefix, int leftSuffix) = Difference(removed[..rStart], added[..aStart], rOffset, aOffset);
                length += leftSuffix; rStart -= leftSuffix; aStart -= leftSuffix;
                if (rStart == prefix && aStart == prefix) { prefix += length; }
                else if (rStart + length + suffix == removed.Length && aStart + length + suffix == added.Length) { suffix += length; }
                else { Dim(rOffset + rStart, length, true); Dim(aOffset + aStart, length, false); }
            }
            else
            {
                while (prefix < Math.Min(removed.Length, added.Length) && removed[prefix] == added[prefix]) { prefix++; }
                int rEnd = removed.Length, aEnd = added.Length;
                while (rEnd > prefix && aEnd > prefix && removed[rEnd - 1] == added[aEnd - 1]) { rEnd--; aEnd--; suffix++; }
                if (rEnd == prefix && aEnd > prefix) { spans.Add(new(rOffset + prefix, 0, Foreground: palette.Foregrounds[10], IsAnchor: true)); }
                else if (rEnd > prefix && aEnd == prefix) { spans.Add(new(aOffset + prefix, 0, Foreground: palette.Foregrounds[9], IsAnchor: true)); }
            }
            return (prefix, suffix);
        }
    }
}
