using System.Globalization;

namespace GitExtensions.Linux;

public sealed record GitLogQuery(
    int Count = 100,
    int Skip = 0,
    string? Revision = null,
    bool All = true,
    string? Path = null,
    string? Author = null,
    string? Committer = null,
    string? Message = null,
    string? DiffContent = null,
    bool IgnoreCase = false,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    bool FirstParent = false,
    bool HideMergeCommits = false,
    bool Reflog = false,
    bool SimplifyByDecoration = false,
    bool FullHistory = false,
    bool SimplifyMerges = false);

public sealed partial class GitRepository
{
    public async Task<IReadOnlyList<GitCommit>> GetLogAsync(GitLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        if (query.Since > query.Until)
        {
            throw new ArgumentException("History start date must not follow its end date.", nameof(query));
        }

        if (query.Revision is null && !query.All && !query.Reflog && !await HasHeadAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        List<string> arguments = ["log", "--topo-order", "-z", "--date=iso-strict", "--no-ext-diff", "--no-textconv",
            "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%s%x00%b", $"--max-count={query.Count.ToString(CultureInfo.InvariantCulture)}"];
        if (query.Skip > 0)
        {
            arguments.Add($"--skip={query.Skip.ToString(CultureInfo.InvariantCulture)}");
        }

        if (query.Author is not null || query.Committer is not null || query.Message is not null)
        {
            arguments.Add("--fixed-strings");
        }

        AddValue("--author=", query.Author);
        AddValue("--committer=", query.Committer);
        AddValue("--grep=", query.Message);
        // -G has its own POSIX extended-regex syntax; fixed-string header/message filters do not change it.
        AddValue("-G", query.DiffContent);
        AddFlag("--regexp-ignore-case", query.IgnoreCase);
        AddValue("--since=", query.Since?.ToString("O", CultureInfo.InvariantCulture));
        AddValue("--until=", query.Until?.ToString("O", CultureInfo.InvariantCulture));
        AddFlag("--first-parent", query.FirstParent);
        AddFlag("--no-merges", query.HideMergeCommits);
        AddFlag("--reflog", query.Reflog);
        AddFlag("--simplify-by-decoration", query.SimplifyByDecoration);
        AddFlag("--full-history", query.FullHistory);
        AddFlag("--simplify-merges", query.SimplifyMerges);
        if (query.Revision is not null)
        {
            arguments.Add(await ResolveRevisionAsync(query.Revision, cancellationToken).ConfigureAwait(false));
        }
        else if (query.All)
        {
            arguments.Add("--all");
        }

        arguments.Add("--");
        if (query.Path is not null)
        {
            arguments.Add(ValidatePath(query.Path));
        }

        return GitOutputParser.ParseLog((await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput);

        void AddValue(string option, string? value)
        {
            if (value is not null)
            {
                arguments.Add(option + value);
            }
        }

        void AddFlag(string option, bool enabled)
        {
            if (enabled)
            {
                arguments.Add(option);
            }
        }
    }
}
