using System.Globalization;

namespace GitExtensions.Linux;

// Values 0–2 are stored by existing Linux installations.
public enum GitLogSort { Topology = 0, AuthorDate = 1, Date = 2, GitDefault = 3 }

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
    bool SimplifyMerges = false)
{
    /// <summary>Original Branches expression. Revision remains a strict single-commit selector.</summary>
    public string? BranchFilter { get; init; }
    /// <summary>Original ShowStashes includes the most recent stash before a custom branch filter.</summary>
    public bool IncludeLatestStashInBranchFilter { get; init; } = true;
    public bool IncludeStashes { get; init; } = true;
    public bool IncludeSessionRefs { get; init; } = true;
    public GitLogSort Sort { get; init; } = GitLogSort.GitDefault;
    /// <summary>Windows graph filters which can hide parents; branch/date/reflog/first-parent alone do not.</summary>
    public bool HasRevisionFilter => Author is not null || Committer is not null || Message is not null
        || DiffContent is not null || !string.IsNullOrWhiteSpace(Path) || HideMergeCommits || SimplifyByDecoration;
    internal GitLogQuery ForRevisionGraph() => this with
    {
        FullHistory = HasRevisionFilter && FullHistory,
        SimplifyMerges = HasRevisionFilter && FullHistory && SimplifyMerges
    };
}

public sealed partial class GitRepository
{
    public Task<IReadOnlyList<GitCommit>> GetLogAsync(GitLogQuery query, CancellationToken cancellationToken = default)
        => GetLogCoreAsync(query, paths: null, rewriteParents: false, cancellationToken);

    /// <summary>Browse's Windows graph parent rewriting, distinct from the raw log reader.</summary>
    public Task<IReadOnlyList<GitCommit>> GetRevisionGraphAsync(GitLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetLogCoreAsync(query.ForRevisionGraph(), paths: null, rewriteParents: query.HasRevisionFilter, cancellationToken);
    }

    private async Task<IReadOnlyList<GitCommit>> GetLogCoreAsync(GitLogQuery query, IReadOnlyList<string>? paths,
        bool rewriteParents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        if (query.Since > query.Until)
        {
            throw new ArgumentException("History start date must not follow its end date.", nameof(query));
        }

        if (query.Revision is not null && !string.IsNullOrWhiteSpace(query.BranchFilter))
        {
            throw new ArgumentException("Choose either a single revision or a branch-filter expression.", nameof(query));
        }

        IReadOnlyList<string>? branchFilterArguments = query.BranchFilter is null ? null : BuildBranchFilterArguments(query.BranchFilter);
        bool hasBranchFilter = branchFilterArguments?.Count > 0;
        // Original Filtered can retain its label while an empty expression shows all refs.
        bool emptyBranchFilterShowsAll = query.Revision is null && branchFilterArguments is not null && !hasBranchFilter;

        if (query.Revision is null && !hasBranchFilter && !emptyBranchFilterShowsAll && !query.All && !query.Reflog
            && !(paths is null ? await HasHeadAsync(cancellationToken).ConfigureAwait(false)
                : await ReadFileHistoryHeadAsync(cancellationToken).ConfigureAwait(false) is not null))
        {
            return [];
        }

        List<string> arguments = ["log", "--encoding=UTF-8", "-z", "--date=iso-strict", "--no-ext-diff", "--no-textconv",
            "--format=%H%x00%P%x00%aN%x00%aE%x00%aI%x00%s%x00%b%x00%B", $"--max-count={query.Count.ToString(CultureInfo.InvariantCulture)}"];
        string? sort = query.Sort switch
        {
            GitLogSort.GitDefault => null,
            GitLogSort.Topology => "--topo-order",
            GitLogSort.AuthorDate => "--author-date-order",
            GitLogSort.Date => "--date-order",
            _ => throw new ArgumentOutOfRangeException(nameof(query), "Unknown history sort order.")
        };
        if (sort is not null) { arguments.Add(sort); }
        if (rewriteParents) { arguments.Add("--parents"); }
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
            arguments.Add(paths is null ? await ResolveRevisionAsync(query.Revision, cancellationToken).ConfigureAwait(false)
                : (await ExecuteAsync(["--no-optional-locks", "rev-parse", "--verify", "--end-of-options", ValidateValue(query.Revision) + "^{commit}"],
                    cancellationToken).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n'));
        }
        else if (hasBranchFilter)
        {
            if (query.IncludeStashes && query.IncludeLatestStashInBranchFilter) { arguments.Add("--glob=refs/stas[h]"); }
            arguments.AddRange(branchFilterArguments!);
        }
        else if (query.All || emptyBranchFilterShowsAll)
        {
            if (!query.IncludeStashes) { arguments.Add("--exclude=refs/stash"); }
            if (!query.IncludeSessionRefs) { arguments.Add("--exclude=refs/sessions/*"); }
            arguments.Add("--all");
        }

        arguments.Add("--");
        if (paths is not null)
        {
            arguments.AddRange(paths.Select(ValidatePath));
        }
        else if (query.Path is not null)
        {
            arguments.Add(ValidatePath(query.Path));
        }

        if (paths is not null) { arguments.InsertRange(0, ["--no-optional-locks", "--literal-pathspecs"]); }
        string output = (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput;
        if (hasBranchFilter) { ValidateBranchFilteredLogOutput(output); }
        return GitOutputParser.ParseLog(output);

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
