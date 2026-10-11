using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GitExtensions.Linux.Plugins;

/// <summary>Ports GitStatistics: all-ref non-merge contributors and HEAD-tracked working-tree line classification.</summary>
public sealed class StatisticsPlugin : INativePlugin
{
    public const string DefaultCodeFiles =
        "*.c;*.cpp;*.cc;*.cxx;*.h;*.hpp;*.hxx;*.inl;*.idl;*.asm;*.inc;*.cs;*.xsd;*.wsdl;*.xml;*.htm;*.html;*.css;" +
        "*.vbs;*.vb;*.fs;*.fsx;*.sql;*.aspx;*.asp;*.php;*.nav;*.pas;*.py;*.rb;*.js;*.jsm;*.ts;*.mk;*.java;*.os;*.bsl";
    public const string DefaultDirectoriesToIgnore = @"\Debug;\Release;\obj;\bin;\lib";

    public PluginDescriptor Descriptor { get; } = new(new Guid("17D1507D-C00D-4A10-AB75-DECB2EA5FCBF"),
        "Statistics", "Count non-merge commits across all refs and classify the current contents of HEAD-tracked code files.",
        "IconGitStatistics",
        [new("codeFiles", "Code files", PluginParameterKind.Text, DefaultCodeFiles,
            Hint: "Semicolon-separated extensions, as in the original plugin; *.cs selects .cs files."),
         new("directoriesToIgnore", "Directories to ignore (EndsWith)", PluginParameterKind.Text, DefaultDirectoriesToIgnore,
            Hint: "Semicolon-separated directory suffixes; / and \\ are accepted. Only the file's immediate directory is tested."),
         new("ignoreSubmodules", "Ignore submodules", PluginParameterKind.Boolean, "true")]);

    public StatisticsOptions GetOptions(IReadOnlyDictionary<string, string?> values)
        => new(PluginValues.Text(Descriptor, values, "codeFiles"),
            PluginValues.Text(Descriptor, values, "directoriesToIgnore"), PluginValues.Boolean(Descriptor, values, "ignoreSubmodules"));

    public Task<StatisticsReport> AnalyzeAsync(PluginContext context, IReadOnlyDictionary<string, string?> values,
        IProgress<StatisticsProgress>? progress = null, CancellationToken cancellationToken = default)
        => AnalyzeAsync(context.Repository, GetOptions(values), progress, cancellationToken);

    public async Task<StatisticsReport> AnalyzeAsync(GitRepository repository, StatisticsOptions? options = null,
        IProgress<StatisticsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("contributors", 0, null, StatisticsLineTotals.Empty));
        string contributorsOutput = (await repository.RunAsync(
            ["shortlog", "--all", "-s", "-n", "--no-merges"], cancellationToken).ConfigureAwait(false)).StandardOutput;
        Dictionary<string, long> contributorCounts = new(StringComparer.Ordinal);
        foreach (string line in contributorsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tab = line.IndexOf('\t');
            if (tab <= 0 || !long.TryParse(line.AsSpan(0, tab).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long count))
                throw new InvalidDataException("Unexpected Git shortlog output.");
            string author = line[(tab + 1)..].TrimEnd('\r');
            contributorCounts[author] = contributorCounts.GetValueOrDefault(author) + count;
        }

        List<GitRepository> modules = [repository];
        if (!options.IgnoreSubmodules)
            modules.AddRange(await StatisticsRepositoryModules.GetAsync(repository, recursive: false, cancellationToken).ConfigureAwait(false));
        HashSet<string> extensions = options.CodeFiles.Replace("*", "", StringComparison.Ordinal).Split(';')
            .ToHashSet(StringComparer.InvariantCultureIgnoreCase);
        string[] ignored = options.DirectoriesToIgnore.Replace('\\', '/').Split(';', StringSplitOptions.RemoveEmptyEntries);
        List<StatisticsFile> files = [];
        Dictionary<string, long> languages = new(StringComparer.Ordinal);
        StatisticsLineTotals totals = StatisticsLineTotals.Empty;
        Stopwatch updateTimer = Stopwatch.StartNew();
        foreach (GitRepository module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((await module.GetInfoAsync(cancellationToken).ConfigureAwait(false)).IsBare
                || (await module.GetStatusAsync(cancellationToken).ConfigureAwait(false)).IsUnborn) continue;
            IReadOnlyList<GitTreeEntry> tree = await module.GetTreeAsync("HEAD", cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                foreach (GitTreeEntry entry in tree)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsSubmodule || !extensions.Contains(Path.GetExtension(entry.Path))) continue;
                    string fullPath = Path.Combine(module.Path, entry.Path);
                    FileInfo file = new(fullPath);
                    string directory = file.Directory!.FullName.Replace('\\', '/');
                    if (!file.Exists || ignored.Any(suffix => directory.EndsWith(suffix, StringComparison.InvariantCultureIgnoreCase))) continue;
                    StatisticsCodeFile parsed = StatisticsCodeFile.Parse(file, cancellationToken);
                    bool isTest = parsed.IsTestFile || directory.Contains("test", StringComparison.OrdinalIgnoreCase);
                    StatisticsLineTotals lines = new(parsed.TotalLineCount, parsed.BlankLineCount, parsed.CommentLineCount,
                        parsed.DesignerLineCount, parsed.CodeLineCount, isTest ? parsed.CodeLineCount : 0);
                    files.Add(new(module.Path, entry.Path, lines, isTest));
                    totals += lines;
                    string extension = file.Extension.ToLower(CultureInfo.CurrentCulture);
                    languages[extension] = languages.GetValueOrDefault(extension) + parsed.CodeLineCount;
                    if (updateTimer.ElapsedMilliseconds >= 500)
                    {
                        progress?.Report(new("files", files.Count, entry.Path, totals));
                        updateTimer.Restart();
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        StatisticsContributor[] contributors = contributorCounts.Select(item => new StatisticsContributor(item.Key, item.Value)).ToArray();
        StatisticsLanguage[] languageRows = languages.OrderByDescending(item => item.Value)
            .Select(item => new StatisticsLanguage(item.Key, item.Value)).ToArray();
        long totalCommits = contributors.Sum(item => item.Commits);
        StatisticsPieChart[] charts =
        [Chart("contributors", "Commits per contributor", contributors.Select(item => (item.Author, item.Author, item.Commits)), totalCommits),
         Chart("languages", "Lines of code per language", languageRows.Select(item => (item.Extension, item.Extension, item.CodeLines)), totals.Code),
         Chart("types", "Lines of code per type", [("blank", "Blank lines", totals.Blank), ("comments", "Comment lines", totals.Comments),
             ("code", "Lines of code", totals.Code), ("designer", "Lines in designer files", totals.Designer)], totals.Total),
         Chart("tests", "Lines of test code", [("tests", "Lines of test code", totals.TestCode), ("production", "Lines of production code", totals.ProductionCode)], totals.Code)];
        progress?.Report(new("complete", files.Count, null, totals));
        return new(totalCommits, contributors, totals, languageRows, files.ToArray(), modules.Select(item => item.Path).ToArray(), charts);
    }

    public async Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default)
    {
        StatisticsReport report = await AnalyzeAsync(context, values, cancellationToken: cancellationToken).ConfigureAwait(false);
        StringBuilder text = new($"{report.TotalCommits:N0} Commits\n{report.Lines.Code:N0} Lines of code\n{report.Lines.TestCode:N0} Lines of test code\n");
        foreach (StatisticsPieChart chart in report.Charts)
        {
            text.Append('\n').Append(chart.Title).Append('\n');
            foreach (StatisticsPieSlice slice in chart.Slices) text.AppendFormat(CultureInfo.CurrentCulture,
                "{0:N0} {1} ({2:P1})\n", slice.Value, slice.Label, slice.Fraction);
        }
        return new(text.ToString(), new(["Module", "Path", "Total", "Blank", "Comments", "Designer", "Code", "Test code"],
            report.Files.Select(file => new PluginRow(file.ModulePath + "\0" + file.Path,
                [file.ModulePath, file.Path, Number(file.Lines.Total), Number(file.Lines.Blank), Number(file.Lines.Comments),
                 Number(file.Lines.Designer), Number(file.Lines.Code), Number(file.Lines.TestCode)])).ToArray()));
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static StatisticsPieChart Chart(string key, string title, IEnumerable<(string Key, string Label, long Value)> values, long denominator)
        => new(key, title, values.Select((item, index) => new StatisticsPieSlice(item.Key, item.Label, item.Value,
            denominator == 0 ? 0 : (double)item.Value / denominator,
            StatisticsReport.OriginalPieColors[index % StatisticsReport.OriginalPieColors.Count])).ToArray());
}

internal static class StatisticsRepositoryModules
{
    internal static async Task<IReadOnlyList<GitRepository>> GetAsync(GitRepository repository, bool recursive, CancellationToken cancellationToken)
    {
        List<GitRepository> result = [];
        HashSet<string> visited = new(StringComparer.Ordinal) { repository.Path };
        await VisitAsync(repository).ConfigureAwait(false);
        return result;

        async Task VisitAsync(GitRepository parent)
        {
            foreach (GitSubmodule module in await parent.GetSubmodulesAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!module.IsInitialized) continue;
                GitRepository child = new(Path.Combine(parent.Path, module.Path));
                if (!visited.Add(child.Path) || !await child.IsRepositoryAsync(cancellationToken).ConfigureAwait(false)) continue;
                result.Add(child);
                if (recursive) await VisitAsync(child).ConfigureAwait(false);
            }
        }
    }
}
