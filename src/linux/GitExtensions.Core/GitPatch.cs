using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed record GitPatchHunk(int Index, int OldStart, int OldCount, int NewStart, int NewCount, string Header, string Text);

public sealed record GitPatchFile(string Header, IReadOnlyList<GitPatchHunk> Hunks)
{
    public bool IsBinary => Header.Contains("GIT binary patch", StringComparison.Ordinal) || Header.Contains("Binary files ", StringComparison.Ordinal);

    public string SelectHunks(IEnumerable<int> indices)
    {
        HashSet<int> selected = indices.ToHashSet();
        if (selected.Count == 0 || selected.Any(index => index < 0 || index >= Hunks.Count))
        {
            throw new ArgumentException("Select at least one valid hunk index.", nameof(indices));
        }

        return Header + string.Concat(Hunks.Where(hunk => selected.Contains(hunk.Index)).Select(hunk => hunk.Text));
    }
}

public static partial class GitPatch
{
    public static string? SelectLines(string diff, int selectionPosition, int selectionLength, bool staged = false,
        bool reverse = false, bool isNewFile = false, bool isRenamed = false)
    {
        ValidateSelection(diff, selectionPosition, selectionLength);
        byte[]? patch = GitCommands.Patches.PatchManager.GetSelectedLinesAsPatch(diff, selectionPosition, GitPatchSelection.InclusiveLength(selectionLength),
            staged, Encoding.UTF8, reverse, isNewFile, isRenamed);
        return patch is null ? null : Encoding.UTF8.GetString(patch);
    }

    public static string? SelectResetLines(string diff, int selectionPosition, int selectionLength)
    {
        ValidateSelection(diff, selectionPosition, selectionLength);
        byte[]? patch = GitCommands.Patches.PatchManager.GetResetWorkTreeLinesAsPatch(diff, selectionPosition, GitPatchSelection.InclusiveLength(selectionLength), Encoding.UTF8);
        return patch is null ? null : Encoding.UTF8.GetString(patch);
    }

    private static void ValidateSelection(string diff, int position, int length)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (position > diff.Length || length > diff.Length - position)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (Parse(diff).Count > 1)
        {
            throw new ArgumentException("Line selection requires a single file diff.", nameof(diff));
        }
    }

    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,(?<oldcount>\d+))? \+(?<new>\d+)(?:,(?<newcount>\d+))? @@", RegexOptions.ExplicitCapture)]
    private static partial Regex HunkHeaderRegex();

    /// <summary>Splits unified diffs without rewriting quoting, context lines, metadata or newline markers.</summary>
    public static IReadOnlyList<GitPatchFile> Parse(string patch)
    {
        List<GitPatchFile> files = [];
        StringBuilder header = new();
        StringBuilder hunkText = new();
        List<GitPatchHunk> hunks = [];
        Match? hunkMatch = null;
        string hunkHeader = "";
        int start = 0;
        while (start < patch.Length)
        {
            int newline = patch.IndexOf('\n', start);
            int end = newline < 0 ? patch.Length : newline + 1;
            string line = patch[start..end];
            start = end;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && (header.Length > 0 || hunks.Count > 0 || hunkMatch is not null))
            {
                FinishFile();
            }

            Match match = HunkHeaderRegex().Match(line);
            if (match.Success)
            {
                FinishHunk();
                hunkMatch = match;
                hunkHeader = line.TrimEnd('\r', '\n');
                hunkText.Append(line);
            }
            else if (hunkMatch is not null)
            {
                hunkText.Append(line);
            }
            else
            {
                header.Append(line);
            }
        }

        FinishFile();
        return files;

        void FinishHunk()
        {
            if (hunkMatch is null)
            {
                return;
            }

            hunks.Add(new GitPatchHunk(hunks.Count, Number(hunkMatch, "old"), Count(hunkMatch, "oldcount"), Number(hunkMatch, "new"), Count(hunkMatch, "newcount"), hunkHeader, hunkText.ToString()));
            hunkText.Clear();
            hunkMatch = null;
        }

        void FinishFile()
        {
            FinishHunk();
            if (header.Length > 0 || hunks.Count > 0)
            {
                files.Add(new GitPatchFile(header.ToString(), hunks.ToArray()));
            }

            header.Clear();
            hunks.Clear();
        }
    }

    private static int Number(Match match, string group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
    private static int Count(Match match, string group) => match.Groups[group].Success ? Number(match, group) : 1;
}
