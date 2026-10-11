using System.Globalization;
using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed record GitBlamePreviousRevision(string Revision, string Path, int Line);

public sealed partial class GitRepository
{
    public async Task<GitBlamePreviousRevision?> GetBlamePreviousRevisionAsync(GitFileBlameLine selected,
        GitFileBlameOptions? options = null, bool useHistogramDiffAlgorithm = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(selected.Line.OriginalLine);
        if (selected.Line.Hash.All(character => character == '0')) { return null; }
        string path = ValidatePath(selected.OriginalPath);
        options ??= new();
        // Read actual ancestry, independently of a history grid's rewritten parents.
        GitRevisionChanges revisions = await GetRevisionChangesAsync(selected.Line.Hash, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (revisions.BaseRevision is not string parent) { return null; }
        GitRevisionFile? file = revisions.Files.FirstOrDefault(file => file.Path == path);
        string? original = file?.OriginalPath ?? (selected.PreviousHash == parent ? selected.PreviousPath : null);
        if (file?.Status == 'A' && original is null) { return null; }
        string previousPath = original is null ? path : ValidatePath(original);
        List<string> arguments = ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-color", "-U0",
            "--diff-algorithm=" + (useHistogramDiffAlgorithm ? "histogram" : "default"), "--find-renames"];
        if (options.DetectCopies) { arguments.Add("--find-copies"); }
        if (options.IgnoreWhitespace) { arguments.Add("--ignore-all-space"); }
        arguments.AddRange([parent, revisions.Revision, "--", previousPath]);
        if (path != previousPath) { arguments.Add(path); }
        string patch = (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput;
        int line = selected.Line.OriginalLine;
        // Original GitBlameParser: use the last zero-context hunk above the
        // selected line, accounting for additions/deletions within that hunk.
        foreach (Match hunk in BlameHunkHeader().Matches(patch).Cast<Match>().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentStart = Number("current"), previousStart = Number("previous");
            if (currentStart > line) { continue; }
            int removed = hunk.Groups["removed"].Success ? Number("removed") : 1;
            int added = hunk.Groups["added"].Success ? Number("added") : 1;
            line = Math.Max(1, Math.Max(previousStart, line - currentStart + previousStart - added + removed));
            break;
            int Number(string group) => int.Parse(hunk.Groups[group].ValueSpan, CultureInfo.InvariantCulture);
        }
        return new(parent, previousPath, line);
    }

    [GeneratedRegex(@"^@@ -(?<previous>\d+)(?:,(?<removed>\d+))? \+(?<current>\d+)(?:,(?<added>\d+))? @@", RegexOptions.Multiline | RegexOptions.ExplicitCapture)]
    private static partial Regex BlameHunkHeader();
}
