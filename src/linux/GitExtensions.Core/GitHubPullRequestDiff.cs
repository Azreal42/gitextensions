using System.Text;

namespace GitExtensions.Linux;

public sealed record GitHubPullRequestFileDiff(GitHubPullRequestFile File, string Diff, bool IsBinary);

/// <summary>Uses the shared patch splitter and exact known API path identities, never positional or ambiguous path guessing.</summary>
public static class GitHubPullRequestDiff
{
    public static IReadOnlyList<GitHubPullRequestFileDiff> Project(string fullDiff, IReadOnlyList<GitHubPullRequestFile> files)
    {
        ArgumentNullException.ThrowIfNull(fullDiff); ArgumentNullException.ThrowIfNull(files);
        Dictionary<string, List<GitHubPullRequestFile>> headers = new(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (GitHubPullRequestFile file in files)
        {
            if (!names.Add(file.Filename)) { throw new GitHubHostingException("The pull request contains duplicate file metadata. Refresh it before viewing the diff."); }
            string oldPath = file.PreviousFilename ?? file.Filename;
            foreach (string a in Forms("a/" + oldPath))
            foreach (string b in Forms("b/" + file.Filename))
            {
                string header = "diff --git " + a + " " + b;
                if (!headers.TryGetValue(header, out List<GitHubPullRequestFile>? matches)) { headers.Add(header, matches = []); }
                if (!matches.Contains(file)) { matches.Add(file); }
            }
        }
        Dictionary<string, GitHubPullRequestFileDiff> result = new(StringComparer.Ordinal);
        foreach (GitPatchFile part in GitPatch.Parse(fullDiff))
        {
            int end = part.Header.IndexOf('\n'); string first = (end < 0 ? part.Header : part.Header[..end]).TrimEnd('\r');
            if (!headers.TryGetValue(first, out List<GitHubPullRequestFile>? matches) || matches.Count != 1 || result.ContainsKey(matches[0].Filename))
            { throw new GitHubHostingException("The pull request diff cannot be matched unambiguously to its file metadata. Refresh it or open the pull request explicitly in the browser."); }
            GitHubPullRequestFile file = matches[0]; result.Add(file.Filename, new(file, part.Header + string.Concat(part.Hunks.Select(h => h.Text)), part.IsBinary));
        }
        if (result.Count != files.Count)
        { throw new GitHubHostingException("GitHub returned an incomplete diff or file list. Refresh it or open the pull request explicitly in the browser."); }
        return Array.AsReadOnly(files.Select(file => result[file.Filename]).ToArray());
    }
    private static IEnumerable<string> Forms(string path)
    {
        yield return path;
        yield return Quote(path, octalUnicode: true);
        yield return Quote(path, octalUnicode: false);
    }
    private static string Quote(string path, bool octalUnicode)
    {
        StringBuilder value = new("\"");
        if (octalUnicode)
        {
            foreach (byte b in Encoding.UTF8.GetBytes(path))
            {
                string? escape = Escape((char)b);
                if (escape is not null) { value.Append(escape); }
                else if (b is < 32 or >= 127) { value.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0')); }
                else { value.Append((char)b); }
            }
        }
        else
        {
            foreach (char c in path)
            {
                string? escape = Escape(c);
                if (escape is not null) { value.Append(escape); }
                else if (c < 32 || c == 127) { value.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0')); }
                else { value.Append(c); }
            }
        }
        return value.Append('"').ToString();
    }
    private static string? Escape(char c) => c switch
    { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\a' => "\\a", '\b' => "\\b", '\f' => "\\f", '\v' => "\\v", _ => null };
}
