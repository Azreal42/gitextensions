using System.Globalization;

namespace GitExtensions.Linux.Plugins;

/// <summary>Ports all-ref non-merge impact, author-date weeks, mailmap, copy detection and recursive initialized submodules.</summary>
public sealed class GitImpactPlugin : INativePlugin
{
    public PluginDescriptor Descriptor { get; } = new(new Guid("F1ACFE42-6A5E-4C30-AC10-9A7C4BB8B480"),
        "Impact Graph", "Show weekly commits and added/deleted lines per author across all refs, excluding merges.", "IconGitImpact",
        [new("includeSubmodules", "Including submodules", PluginParameterKind.Boolean, "false"),
         new("respectMailmap", "Respect .mailmap", PluginParameterKind.Boolean, "true")]);

    public GitImpactOptions GetOptions(IReadOnlyDictionary<string, string?> values)
        => new(PluginValues.Boolean(Descriptor, values, "includeSubmodules"), PluginValues.Boolean(Descriptor, values, "respectMailmap"));

    public Task<GitImpactReport> AnalyzeAsync(PluginContext context, IReadOnlyDictionary<string, string?> values,
        IProgress<GitImpactProgress>? progress = null, CancellationToken cancellationToken = default)
        => AnalyzeAsync(context.Repository, GetOptions(values), progress, cancellationToken);

    public async Task<GitImpactReport> AnalyzeAsync(GitRepository repository, GitImpactOptions? options = null,
        IProgress<GitImpactProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        DayOfWeek firstDay = options.FirstDayOfWeek ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        if (!Enum.IsDefined(firstDay)) throw new ArgumentOutOfRangeException(nameof(options));
        List<GitRepository> modules = [repository];
        if (options.IncludeSubmodules)
            modules.AddRange(await StatisticsRepositoryModules.GetAsync(repository, recursive: true, cancellationToken).ConfigureAwait(false));
        List<GitImpactCommit> commits = [];
        int loaded = 0;
        foreach (GitRepository module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string authorFormat = options.RespectMailmap ? "%aN" : "%an";
            string output = (await module.RunAsync(["log", "--all", "--no-merges", "--no-show-signature", "--no-decorate", "--encoding=UTF-8",
                $"--format=%x00%H%x00%ad%x00{authorFormat}%x00", "--date=short", "--numstat", "-z", "--find-copies", "--no-ext-diff", "--no-textconv", "--"],
                cancellationToken).ConfigureAwait(false)).StandardOutput;
            commits.AddRange(ParseCommits(module.Path, output, firstDay, cancellationToken));
            progress?.Report(new(module.Path, ++loaded, commits.Count));
        }
        Dictionary<string, GitImpactData> authors = new(StringComparer.Ordinal);
        List<string> drawOrder = [];
        SortedDictionary<DateOnly, Dictionary<string, GitImpactWeekAuthor>> weeks = [];
        foreach (GitImpactCommit commit in commits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!authors.ContainsKey(commit.Author)) drawOrder.Insert(0, commit.Author);
            authors[commit.Author] = authors.GetValueOrDefault(commit.Author) + commit.Data;
            if (!weeks.TryGetValue(commit.Week, out var week)) weeks.Add(commit.Week, week = new(StringComparer.Ordinal));
            GitImpactData data = week.TryGetValue(commit.Author, out var value) ? value.Data : new();
            week[commit.Author] = new(commit.Author, data + commit.Data, false);
        }
        // Retain the original rule: add an author's zero only to already-present weeks between that author's first and last week.
        // Globally absent calendar weeks remain absent, so the horizontal spacing is by represented week rather than elapsed days.
        foreach (string author in authors.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateOnly[] active = weeks.Where(item => item.Value.ContainsKey(author)).Select(item => item.Key).ToArray();
            if (active.Length < 2) continue;
            foreach ((DateOnly date, var week) in weeks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (date > active[0] && date < active[^1] && !week.ContainsKey(author)) week.Add(author, new(author, new(), true));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(authors.Select(item => new GitImpactAuthor(item.Key, item.Value, GitImpactReport.OriginalColor(item.Key))).ToArray(),
            weeks.Select(item => new GitImpactWeek(item.Key, item.Value.Values.ToArray())).ToArray(), commits.ToArray(),
            modules.Select(module => module.Path).ToArray(), drawOrder.ToArray(), firstDay);
    }

    public async Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default)
    {
        GitImpactReport report = await AnalyzeAsync(context, values, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new($"{report.Total.Commits:N0} non-merge commits, {report.Total.ChangedLines:N0} changed lines in {report.Weeks.Count:N0} represented weeks.",
            new(["Week", "Author", "Commits", "Added lines", "Deleted lines", "Changed lines", "Intermediate empty week"],
                report.Weeks.SelectMany(week => week.Authors.Select(author => new PluginRow(week.Date.ToString("O") + "\0" + author.Author,
                    [week.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), author.Author, Number(author.Data.Commits),
                     Number(author.Data.AddedLines), Number(author.Data.DeletedLines), Number(author.Data.ChangedLines),
                     author.IsIntermediateEmptyWeek ? "true" : "false"]))).ToArray()));
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyList<GitImpactCommit> ParseCommits(string modulePath, string output, DayOfWeek firstDay, CancellationToken cancellationToken)
    {
        // NUL is the sole path separator. A rename has an empty path field followed by old-path NUL new-path NUL.
        // Unlike the original line enumerator, a new header is never consumed as the end of the previous commit.
        string[] fields = output.Split('\0');
        List<GitImpactCommit> commits = [];
        string? hash = null;
        string author = "";
        DateOnly date = default;
        List<GitImpactFileChange> files = [];
        for (int i = 0; i < fields.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string field = fields[i].TrimStart('\n');
            if (field.Length == 0) continue;
            if (IsHash(field))
            {
                CompleteCommit();
                if (i + 2 >= fields.Length) throw new InvalidDataException("Incomplete Git impact commit header.");
                hash = field;
                date = DateOnly.ParseExact(fields[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                author = fields[++i];
                files = [];
                continue;
            }
            if (hash is null) throw new InvalidDataException("Impact file statistics preceded their commit.");
            int firstTab = field.IndexOf('\t'), secondTab = firstTab < 0 ? -1 : field.IndexOf('\t', firstTab + 1);
            if (firstTab < 1 || secondTab < 0) throw new InvalidDataException("Unexpected Git numstat output.");
            string addedText = field[..firstTab], deletedText = field[(firstTab + 1)..secondTab];
            bool binary = addedText == "-" || deletedText == "-";
            long added = addedText == "-" ? 0 : long.Parse(addedText, NumberStyles.None, CultureInfo.InvariantCulture);
            long deleted = deletedText == "-" ? 0 : long.Parse(deletedText, NumberStyles.None, CultureInfo.InvariantCulture);
            string path = field[(secondTab + 1)..], previousPath = "";
            if (path.Length == 0)
            {
                if (i + 2 >= fields.Length) throw new InvalidDataException("Incomplete renamed Git path.");
                previousPath = fields[++i];
                path = fields[++i];
            }
            files.Add(new(path, previousPath.Length == 0 ? null : previousPath, added, deleted, binary));
        }
        CompleteCommit();
        return commits;

        void CompleteCommit()
        {
            if (hash is null) return;
            // Preserve the original first-day arithmetic, including its treatment of Sunday in Monday-first cultures.
            DateOnly week = date.AddDays((int)firstDay - (int)date.DayOfWeek);
            commits.Add(new(modulePath, hash, date, week, author,
                new(1, files.Sum(file => file.AddedLines), files.Sum(file => file.DeletedLines)), files.ToArray()));
        }
    }

    private static bool IsHash(string value)
        => value.Length is 40 or 64 && value.All(character => char.IsAsciiHexDigit(character));
}
