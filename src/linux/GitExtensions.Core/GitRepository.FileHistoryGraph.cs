using System.Collections.ObjectModel;

namespace GitExtensions.Linux;

public sealed record GitFileHistoryGraphOptions(bool FollowRenames = true, bool ExactRenames = false, bool IsDirectory = false);

/// <summary>Browse's ordinary graph and a separate HEAD-followed pathname sidecar; no selected revision is a traversal bound.</summary>
public sealed record GitFileHistoryGraph(IReadOnlyList<GitCommit> Commits, IReadOnlyList<string> Paths,
    IReadOnlyDictionary<string, string> PathByCommit);

public sealed partial class GitRepository
{
    /// <summary>
    /// Discovers former names from HEAD like Windows Browse, then applies the unchanged graph query to those literal paths.
    /// PathByCommit contains only names returned by discovery, so callers must allow an absent mapping on other branches.
    /// </summary>
    public async Task<GitFileHistoryGraph> GetFileHistoryGraphAsync(GitLogQuery query, GitFileHistoryGraphOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        if (query.Since > query.Until) { throw new ArgumentException("History start date must not follow its end date.", nameof(query)); }
        if (query.Revision is not null && !string.IsNullOrWhiteSpace(query.BranchFilter))
        { throw new ArgumentException("Choose either a single revision or a branch-filter expression.", nameof(query)); }
        string path = ValidatePath(query.Path ?? throw new ArgumentException("File history requires a literal path.", nameof(query)));
        options ??= new();
        bool isDirectory = options.IsDirectory || query.Path!.EndsWith('/') || path == ".";
        List<string> paths = [path];
        HashSet<string> seen = new(StringComparer.Ordinal) { path };
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        string? head = options.FollowRenames && !isDirectory ? await ReadFileHistoryHeadAsync(cancellationToken).ConfigureAwait(false) : null;
        if (head is not null)
        {
            // The leading NUL marks headers unambiguously even when a literal filename is a full object ID.
            List<string> arguments = ["--no-optional-locks", "--literal-pathspecs", "log", "-z", "--format=%x00%H", "--name-only", "--follow", "--no-ext-diff", "--no-textconv",
                options.ExactRenames ? "--find-renames=100%" : "--find-renames",
                options.ExactRenames ? "--find-copies=100%" : "--find-copies", head, "--", path];
            string output = (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput;
            ReadFollowedNames(output, paths, seen, names, cancellationToken);
        }
        IReadOnlyList<GitCommit> commits = await GetLogCoreAsync(query, paths, rewriteParents: true, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(commits, paths.AsReadOnly(), new ReadOnlyDictionary<string, string>(names));
    }

    private async Task<string?> ReadFileHistoryHeadAsync(CancellationToken cancellationToken)
    {
        GitResult result = await ExecuteAsync(["--no-optional-locks", "rev-parse", "--verify", "--quiet", "--end-of-options", "HEAD^{commit}"],
            cancellationToken, allowedExitCodes: [1, 128]).ConfigureAwait(false);
        return result.Success ? ReadOutputLine(result.StandardOutput) : null;
    }

    private static void ReadFollowedNames(string output, List<string> paths, HashSet<string> seen,
        Dictionary<string, string> names, CancellationToken cancellationToken)
    {
        if (output.Length == 0) { return; }
        if (output[^1] != '\0') { throw new FormatException("Incomplete NUL-terminated followed-file history."); }
        string[] fields = output.Split('\0');
        int index = 0;
        while (index < fields.Length - 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fields[index++].Length != 0 || index >= fields.Length - 1 || !IsObjectId(fields[index]))
            { throw new FormatException("Invalid followed-file history commit header."); }
            string hash = fields[index++];
            bool first = true;
            while (index < fields.Length - 1 && fields[index].Length != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = fields[index++];
                if (first)
                {
                    // Git inserts one structural LF before name-only output. Every subsequent byte belongs to the pathname.
                    if (!name.StartsWith('\n')) { throw new FormatException("Missing followed-file history record separator."); }
                    name = name[1..]; first = false;
                }
                if (name.Length == 0) { throw new FormatException("Empty followed-file history pathname."); }
                names.TryAdd(hash, name);
                if (seen.Add(name)) { paths.Add(name); }
            }
        }
    }
}
