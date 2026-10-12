namespace GitExtensions.Linux;

// Adapted from GitUI/Editor/Diff/LinesMatcher.cs: keep its ordered, pairwise matching
// rather than deriving a transitive line identity or allowing crossing matches.
internal static class GitDiffLineMatcher
{
    internal static IEnumerable<(GitDiffLineDescriptor Removed, GitDiffLineDescriptor Added)> FindPairs(
        Func<GitDiffLineDescriptor, string> getText, IReadOnlyList<GitDiffLineDescriptor> removed,
        IReadOnlyList<GitDiffLineDescriptor> added)
    {
        long combinations = (long)removed.Count * added.Count;
        if (combinations == 0) { yield break; }
        if (combinations == 1 || combinations > 10_000)
        {
            for (int index = 0; index < Math.Min(removed.Count, added.Count); index++)
            { yield return (removed[index], added[index]); }
            yield break;
        }
        foreach (var pair in FindPairs([.. removed.Select(line => new LineData(line, getText(line)))],
            [.. added.Select(line => new LineData(line, getText(line)))])) { yield return pair; }
    }

    private static IEnumerable<(GitDiffLineDescriptor Removed, GitDiffLineDescriptor Added)> FindPairs(LineData[] removed, LineData[] added)
    {
        (int r, int a) = BestLine(removed, added);
        if (r > 0 && a > 0)
        { foreach (var pair in FindPairs(removed[..r], added[..a])) { yield return pair; } }
        yield return (removed[r].Line, added[a].Line);
        r++; a++;
        if (r < removed.Length && a < added.Length)
        { foreach (var pair in FindPairs(removed[r..], added[a..])) { yield return pair; } }
    }

    private static (int Removed, int Added) BestLine(LineData[] removed, LineData[] added)
    {
        int longest = -1, bestRemoved = 0, bestAdded = -1;
        for (int r = 0; r < removed.Length; r++)
        {
            int a = Array.FindIndex(added, line => line.Trimmed == removed[r].Trimmed);
            if (a >= 0 && removed[r].Trimmed.Length > longest)
            { longest = removed[r].Trimmed.Length; bestRemoved = r; bestAdded = a; }
        }
        if (bestAdded >= 0) { return (bestRemoved, bestAdded); }
        float max = -1;
        foreach ((int r, int a) in Combinations(removed.Length, added.Length))
        {
            LineData first = removed[r], second = added[a];
            float score = first.Words.Count == 0 || second.Words.Count == 0 ? -1
                : (float)first.Words.Intersect(second.Words).Sum(word => word.Length)
                    / Math.Max(first.WordsLength, second.WordsLength);
            if (max < score)
            {
                max = score; bestRemoved = r; bestAdded = a;
                if (max == 1) { return (r, a); }
            }
        }
        return max <= 0.1f ? (0, 0) : (bestRemoved, bestAdded);
    }

    internal static (string? Word, int Removed, int Added) BestWord(string removed, string added)
    {
        (string Word, int Start)[] first = [.. Words(removed)], second = [.. Words(added)];
        var match = Longest(first, second);
        return match.Word is not null ? match : Longest([.. Subwords(first)], [.. Subwords(second)]);
        static (string? Word, int Removed, int Added) Longest((string Word, int Start)[] first, (string Word, int Start)[] second)
        {
            // IntersectBy keeps the first occurrence in the added line, including ties.
            var common = second.IntersectBy(first.Select(word => word.Word), word => word.Word)
                .Append((Word: "", Start: -1)).MaxBy(word => word.Word.Length);
            return common.Start < 0 ? (null, 0, 0)
                : (common.Word, first.First(word => word.Word == common.Word).Start, common.Start);
        }
    }

    private static IEnumerable<(int First, int Second)> Combinations(int firstEnd, int secondEnd)
    {
        for (int diagonal = 0; diagonal < firstEnd; diagonal++)
        { for (int second = 0; second < Math.Min(diagonal + 1, secondEnd); second++) { yield return (diagonal - second, second); } }
        for (int diagonal = 1; diagonal < secondEnd; diagonal++)
        { for (int second = diagonal; second < Math.Min(firstEnd + diagonal, secondEnd); second++) { yield return (firstEnd - 1 + diagonal - second, second); } }
    }

    private static IEnumerable<(string Word, int Start)> Words(string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && !IsWord(text[start])) { start++; }
            if (start == text.Length) { yield break; }
            int end = start + 1;
            while (end < text.Length && IsWord(text[end])) { end++; }
            yield return (text[start..end], start); start = end + 1;
        }
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    private static IEnumerable<(string Word, int Start)> Subwords(IEnumerable<(string Word, int Start)> words)
    {
        foreach (var word in words)
        {
            string text = word.Word;
            int start = 0; bool previousUpper = char.IsUpper(text[0]);
            for (int index = 0; index < text.Length; index++)
            {
                bool currentUpper = char.IsUpper(text[index]);
                if (previousUpper != currentUpper)
                {
                    previousUpper = currentUpper;
                    if (currentUpper)
                    {
                        if (!(index == 1 && !char.IsLetterOrDigit(text[0])))
                        { yield return (text[start..index], word.Start + start); }
                        start = index;
                    }
                }
                if (index > 0 && !char.IsLetterOrDigit(text[index]))
                {
                    if (start < index && char.IsLetterOrDigit(text[index - 1]))
                    { yield return (text[start..index], word.Start + start); }
                    start = index + 1; previousUpper = true;
                }
            }
            if (start < text.Length && !(text.Length == 1 && !char.IsLetterOrDigit(text[0])))
            { yield return (text[start..], word.Start + start); }
        }
    }

    private sealed class LineData(GitDiffLineDescriptor line, string text)
    {
        internal GitDiffLineDescriptor Line { get; } = line;
        internal string Trimmed { get; } = text.Trim();
        internal IReadOnlySet<string> Words { get; } = GitDiffLineMatcher.Words(text.Trim()).Select(word => word.Word).ToHashSet(StringComparer.Ordinal);
        internal int WordsLength => Words.Sum(word => word.Length);
    }
}
