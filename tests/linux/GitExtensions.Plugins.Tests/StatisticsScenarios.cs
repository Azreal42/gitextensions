using System.Globalization;
using System.Text;
using GitExtensions.Linux;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Plugins.Tests;

public static class StatisticsScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("statistics retains line categories, generated regions, test attributes and BOM decoding", CategoriesAsync),
        ("statistics tracks HEAD paths but counts current files, literal paths and selected binary extensions", WorkingTreeAsync),
        ("statistics filters extension case and immediate directory suffixes with both separators", FiltersAsync),
        ("statistics retains all original language comment and designer conventions", LanguagesAsync),
        ("statistics contributors include branches/tags, exclude merges and respect mailmap", ContributorsAsync),
        ("statistics submodule LOC is direct while impact history includes nested initialized modules", SubmodulesAsync),
        ("impact consumes every header and literal rename/binary numstat paths without losing commits", NumstatAsync),
        ("impact retains author dates, locale first-day arithmetic and optional mailmap", DatesAndMailmapAsync),
        ("impact adds zero contributions only between represented weeks and preserves logarithmic geometry", EmptyWeeksAsync),
        ("statistics and impact return finite empty charts for unborn and bare repositories", EmptyAndBareAsync),
        ("statistics and impact cancellation terminates analysis without changing refs or files", CancellationAsync)
    ];

    private static async Task CategoriesAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        fixture.Write("main.cs", "\n// comment\n/* comment\nbody\n*/\nclass C {}\n#region Windows Form Designer generated code\ngenerated\n#endregion\n[Test]\nvoid Test() {}\n");
        fixture.Write("View.Designer.cs", "// generated\n\n");
        fixture.Write("unitTests/spec.ts", "// comment\n\nlet value = 1;\n");
        fixture.Write("schema.xml", "<!-- comment -->\n<root>\n</root>\n");
        fixture.WriteBytes("script.py", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("# comment\n\"\"\"doc\nbody\n\"\"\"\nprint(1)\n")).ToArray());
        await fixture.CommitAsync("Categories");
        StatisticsReport report = await new StatisticsPlugin().AnalyzeAsync(fixture.Repository);
        Check(File(report, "main.cs").Lines == new StatisticsLineTotals(11, 1, 4, 3, 3, 3), "Generated/comment/test counts differ from the original parser.");
        Check(File(report, "View.Designer.cs").Lines == new StatisticsLineTotals(2, 0, 0, 2, 0, 0), "Designer file must classify every line as generated, including blanks/comments.");
        Check(File(report, "unitTests/spec.ts").Lines.TestCode == 1, "Test directory heuristic lost.");
        Check(File(report, "schema.xml").Lines.Comments == 1 && File(report, "schema.xml").Lines.Code == 2, "XML classification lost.");
        Check(File(report, "script.py").Lines.Comments == 4 && File(report, "script.py").Lines.Code == 1, "BOM or Python block classification lost.");
        Check(report.Lines.Total == 24 && report.Lines.Code == 7 && report.Lines.TestCode == 4, "Aggregate line categories differ.");
        Check(report.Charts.Select(item => item.Key).SequenceEqual(new[] { "contributors", "languages", "types", "tests" }), "All four chart models are required.");
        Check(report.Charts[2].Slices.Select(slice => slice.Value).SequenceEqual(new[] { report.Lines.Blank, report.Lines.Comments, report.Lines.Code, report.Lines.Designer }), "Original type pie order differs.");
        Check(Math.Abs(report.Charts[2].Slices.Sum(slice => slice.Fraction) - 1) < 1e-12, "Type pie denominator must be total lines.");
        Check(report.Charts[3].Slices[1].Value == 3 && report.Languages[0].Extension == ".cs", "Production split or language ordering differs.");
        Check(report.Charts[0].Slices[0].Color == "#FF0000", "Original palette was changed.");
    }

    private static async Task WorkingTreeAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        const string literal = "été\nquote'\"tab\t.cs";
        fixture.Write(literal, "one\n");
        fixture.Write("removed.cs", "removed\n");
        fixture.WriteBytes("binary.cs", [0xff, 0, 0x0a, 0x01, 0]);
        await fixture.CommitAsync("HEAD files");
        fixture.Write(literal, "one\ntwo\n");
        fixture.Write("untracked.cs", "untracked\n");
        fixture.Write("staged.cs", "staged\n");
        await fixture.GitAsync("add", "--", "staged.cs");
        System.IO.File.Delete(Path.Combine(fixture.Repository.Path, "removed.cs"));
        StatisticsReport report = await new StatisticsPlugin().AnalyzeAsync(fixture.Repository);
        Check(report.Files.Count == 2 && File(report, literal).Lines.Code == 2, "HEAD selection or working-tree content rule was changed.");
        Check(File(report, "binary.cs").Lines.Total == 2 && File(report, "binary.cs").Lines.Code == 2, "Original statistics parser reads selected extensions even if Git treats their contents as binary.");
        PluginResult result = await new StatisticsPlugin().ExecuteAsync(new(fixture.Repository), Values());
        Check(result.Table!.Rows.Any(row => row.Cells[1] == literal) && !result.RepositoryChanged, "Generic result lost the literal path or claimed mutation.");
    }

    private static async Task FiltersAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        foreach (string path in new[] { "obj/excluded.cs", "obj/deep/included.CS", "Debug/excluded.cs", "lib/excluded.cs", "source/a.cs", "source/a.ts", "myobj/included.cs" }) fixture.Write(path, "code\n");
        await fixture.CommitAsync("Paths");
        StatisticsPlugin plugin = new();
        StatisticsReport report = await plugin.AnalyzeAsync(fixture.Repository);
        Check(report.Files.Select(item => item.Path).ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "obj/deep/included.CS", "source/a.cs", "source/a.ts", "myobj/included.cs" }), "Immediate-directory suffix semantics were not retained.");
        report = await plugin.AnalyzeAsync(fixture.Repository, new("*.CS", "/obj;/Debug;/lib"));
        Check(report.Files.Count == 3 && report.Files.All(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)), "Extension case or POSIX separator normalization failed.");
        report = await plugin.AnalyzeAsync(fixture.Repository, new("*.cs", ""));
        Check(report.Files.Count == 6, "An empty ignore list must not accidentally exclude every directory.");
    }

    private static async Task LanguagesAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        Dictionary<string, string> cases = new()
        {
            ["script.rb"] = "=begin\nbody\n=end\n# line\ncode\n",
            ["script.pl"] = "=begin\nbody\n=end\n# line\ncode\n",
            ["script.lua"] = "--[[\nbody\n]]\n-- line\ncode\n",
            ["source.pas"] = "(* comment\nend *)\n{ comment\nend }\n{$directive}\ncode\n",
            ["source.inc"] = "; line\ncode\n",
            ["source.s"] = "; line\ncode\n",
            ["source.m"] = "%{\nbody\n%}\n% line\ncode\n",
            ["view.cshtml"] = "@* inline *@\n<!-- block\nend -->\ncode\n",
            ["view.aspx"] = "<%-- comment\nend --%>\ncode\n",
            ["resource.resx"] = "<!-- comment -->\ncode\n",
            ["Web References/Reference.cs"] = "code\n// comment\n",
            ["source.vb"] = "' comment\ncode\n"
        };
        foreach ((string path, string content) in cases) fixture.Write(path, content);
        await fixture.CommitAsync("Language conventions");
        StatisticsReport report = await new StatisticsPlugin().AnalyzeAsync(fixture.Repository,
            new("*.rb;*.pl;*.lua;*.pas;*.inc;*.s;*.m;*.cshtml;*.aspx;*.resx;*.cs;*.vb", ""));
        foreach (string path in new[] { "script.rb", "script.pl", "script.lua", "source.m" })
            Check(File(report, path).Lines.Comments == 4 && File(report, path).Lines.Code == 1, "Block comment convention differs for " + path);
        Check(File(report, "source.pas").Lines.Comments == 4 && File(report, "source.pas").Lines.Code == 2, "Pascal directives must remain code.");
        Check(File(report, "view.cshtml").Lines.Comments == 3 && File(report, "view.aspx").Lines.Comments == 2, "Razor/ASP comments differ.");
        Check(File(report, "Web References/Reference.cs").Lines.Designer == 2, "Web-reference generated classification differs.");
        foreach (string path in new[] { "source.inc", "source.s", "resource.resx", "source.vb" }) Check(File(report, path).Lines.Comments == 1, "Line-comment convention differs for " + path);
    }

    private static async Task ContributorsAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        fixture.Write("base.cs", "code\n");
        await fixture.CommitAsync("Base", author: "Alias", email: "alias@example.invalid");
        await fixture.Repository.CreateBranchAsync("topic", checkout: true);
        fixture.Write("topic.cs", "topic\n");
        await fixture.CommitAsync("Topic", author: "Bob");
        await fixture.Repository.CheckoutAsync("main");
        fixture.Write("main.cs", "main\n");
        await fixture.CommitAsync("Main", author: "Alice", email: "alice@example.invalid");
        await fixture.GitAsync("merge", "--no-ff", "-m", "Merge", "topic");
        await fixture.GitAsync("checkout", "--orphan", "tag-only");
        await fixture.GitAsync("rm", "-rf", ".");
        fixture.Write("tag.cs", "tag\n");
        await fixture.CommitAsync("Tag only", author: "Tagged");
        await fixture.GitAsync("tag", "archive");
        await fixture.Repository.CheckoutAsync("main");
        await fixture.Repository.DeleteBranchAsync("tag-only", force: true);
        fixture.Write(".mailmap", "Alice <alice@example.invalid> Alias <alias@example.invalid>\n");
        StatisticsReport report = await new StatisticsPlugin().AnalyzeAsync(fixture.Repository);
        Check(report.TotalCommits == 4 && report.Contributors.Single(author => author.Author == "Alice").Commits == 2, "All-ref non-merge/mailmap shortlog semantics differ.");
        Check(report.Contributors.Any(author => author.Author == "Tagged") && report.Contributors.All(author => author.Author != "Statistics User"), "Tag-only commits or merge exclusion lost.");
        GitImpactReport impact = await new GitImpactPlugin().AnalyzeAsync(fixture.Repository);
        Check(impact.Total.Commits == 4 && impact.Authors.Any(author => author.Name == "Tagged"), "Impact must include all refs and exclude merges too.");
    }

    private static async Task SubmodulesAsync()
    {
        using StatisticsFixture root = await StatisticsFixture.CreateAsync();
        using StatisticsFixture child = await StatisticsFixture.CreateAsync();
        using StatisticsFixture nested = await StatisticsFixture.CreateAsync();
        nested.Write("nested.cs", "one\ntwo\nthree\n");
        await nested.CommitAsync("Nested");
        child.Write("child.cs", "one\ntwo\n");
        await child.CommitAsync("Child");
        await child.Repository.AddSubmoduleAsync(nested.Repository.Path, "nested ü");
        await child.CommitAsync("Nested pointer");
        root.Write("root.cs", "one\n");
        await root.CommitAsync("Root");
        await root.Repository.AddSubmoduleAsync(child.Repository.Path, "child ü");
        await root.CommitAsync("Child pointer");
        await root.Repository.UpdateSubmodulesAsync(recursive: true);
        StatisticsReport report = await new StatisticsPlugin().AnalyzeAsync(root.Repository);
        Check(report.Lines.Code == 1 && report.Modules.Count == 1, "Submodules should be ignored by default.");
        report = await new StatisticsPlugin().AnalyzeAsync(root.Repository, new(IgnoreSubmodules: false));
        Check(report.Lines.Code == 3 && report.Modules.Count == 2 && report.TotalCommits == 2, "Statistics LOC should include direct submodules only, and contributors should remain the main module's.");
        GitImpactReport impact = await new GitImpactPlugin().AnalyzeAsync(root.Repository, new(IncludeSubmodules: true));
        Check(impact.Modules.Count == 3 && impact.Total.Commits == 5, "Impact must include recursively initialized submodule histories.");
        await root.GitAsync("submodule", "deinit", "--force", "--all");
        report = await new StatisticsPlugin().AnalyzeAsync(root.Repository, new(IgnoreSubmodules: false));
        impact = await new GitImpactPlugin().AnalyzeAsync(root.Repository, new(IncludeSubmodules: true));
        Check(report.Modules.Count == 1 && impact.Modules.Count == 1, "Uninitialized submodules must be skipped without fetching or initializing them.");
    }

    private static async Task NumstatAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        const string original = "été\n'\t.txt", renamed = "renamed\n\tété.txt";
        fixture.Write(original, "one\ntwo\nthree\n");
        await fixture.CommitAsync("First", "2026-01-05T12:00:00+02:00", "A");
        fixture.Write(original, "one\nchanged\nthree\nextra\n");
        await fixture.CommitAsync("Second", "2026-01-06T12:00:00+02:00", "B");
        fixture.WriteBytes("binary\nü.bin", [0, 1, 2, 0xff, 0x0a]);
        await fixture.CommitAsync("Binary", "2026-01-07T12:00:00+02:00", "C");
        await fixture.GitAsync("mv", "--", original, renamed);
        await fixture.CommitAsync("Rename", "2026-01-08T12:00:00+02:00", "A");
        const string copied = "copy\n\tété.txt";
        fixture.Write(copied, "one\nchanged\nthree\nextra\n");
        fixture.Write(renamed, "one\nchanged\nthree\nextra\nchanged again\n");
        await fixture.CommitAsync("Copy a modified source", "2026-01-09T12:00:00+02:00", "A");
        GitImpactPlugin plugin = new();
        GitImpactReport report = await plugin.AnalyzeAsync(fixture.Repository, new(FirstDayOfWeek: DayOfWeek.Monday));
        Check(report.Total == new GitImpactData(5, 6, 1) && report.Commits.Select(item => item.Hash).Distinct().Count() == 5, "Impact skipped headers or miscounted numstat/copy detection.");
        GitImpactFileChange rename = report.Commits.SelectMany(item => item.Files).Single(file => file.PreviousPath == original);
        Check(rename.Path == renamed && rename.PreviousPath == original && rename.AddedLines == 0, "NUL-delimited rename paths were corrupted.");
        Check(report.Commits.SelectMany(item => item.Files).Single(file => file.IsBinary).Path == "binary\nü.bin", "Binary path or numstat classification lost.");
        GitImpactFileChange copy = report.Commits.SelectMany(item => item.Files).Single(file => file.Path == copied);
        Check(copy.PreviousPath == renamed && copy.AddedLines == 0 && copy.DeletedLines == 0, "Original --find-copies behavior or copy-path delimiters were lost.");
        PluginResult result = await plugin.ExecuteAsync(new(fixture.Repository), Values());
        Check(!result.RepositoryChanged && result.Table!.Rows.Sum(row => long.Parse(row.Cells[2], CultureInfo.InvariantCulture)) == 5, "Generic impact result differs from typed analysis.");
    }

    private static async Task DatesAndMailmapAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        fixture.Write("a.cs", "code\n");
        await fixture.CommitAsync("Sunday author date", "2026-01-04T23:50:00-11:00", "Alias", "alias@example.invalid", "2026-03-01T12:00:00+00:00");
        fixture.Write(".mailmap", "Alice <alice@example.invalid> Alias <alias@example.invalid>\n");
        GitImpactPlugin plugin = new();
        GitImpactReport report = await plugin.AnalyzeAsync(fixture.Repository, new(FirstDayOfWeek: DayOfWeek.Monday));
        Check(report.Commits[0].AuthoredDate == new DateOnly(2026, 1, 4) && report.Weeks[0].Date == new DateOnly(2026, 1, 5), "Original author-date/first-day arithmetic changed (Sunday belongs to the following Monday in the source).");
        Check(report.Authors.Single().Name == "Alice", "Default mailmap behavior lost.");
        report = await plugin.AnalyzeAsync(fixture.Repository, new(RespectMailmap: false, FirstDayOfWeek: DayOfWeek.Sunday));
        Check(report.Authors.Single().Name == "Alias" && report.Weeks[0].Date == new DateOnly(2026, 1, 4), "Optional raw author or Sunday-first culture lost.");
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            report = await plugin.AnalyzeAsync(fixture.Repository);
            Check(report.FirstDayOfWeek == DayOfWeek.Monday, "Locale first day was not used.");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static async Task EmptyWeeksAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        fixture.Write("a.cs", string.Concat(Enumerable.Repeat("line\n", 200)));
        await fixture.CommitAsync("A first", "2026-01-05T12:00:00+00:00", "A");
        await fixture.CommitAsync("B middle", "2026-01-19T12:00:00+00:00", "B");
        fixture.Write("a.cs", "one\n");
        await fixture.CommitAsync("A last", "2026-02-02T12:00:00+00:00", "A");
        GitImpactReport report = await new GitImpactPlugin().AnalyzeAsync(fixture.Repository, new(FirstDayOfWeek: DayOfWeek.Monday));
        Check(report.Weeks.Count == 3 && report.Weeks[1].Date == new DateOnly(2026, 1, 19), "Globally absent calendar weeks should remain absent.");
        GitImpactWeekAuthor empty = report.Weeks[1].Authors.Single(item => item.Author == "A");
        Check(empty.IsIntermediateEmptyWeek && empty.Data == new GitImpactData() && report.Weeks[0].Authors.All(item => item.Author != "B"), "Author gap filling differs from source.");
        Check(report.DrawOrder.SequenceEqual(new[] { "B", "A" }), "Original reverse first-encounter author stack lost.");
        GitImpactLayout layout = report.CreateLayout(400);
        Check(layout.Width == 280 && layout.Areas.Single(area => area.Author == "A").Blocks.Count == 3, "Original 60/50 block spacing lost.");
        Check(layout.Areas.Single(area => area.Author == "A").Segments.Count(segment => segment.Control1 is not null) == 4, "Original top/bottom Bezier transitions lost.");
        GitImpactBlock tiny = layout.Areas.Single(area => area.Author == "A").Blocks[1];
        Check(tiny.Bounds.Height >= 1 && !tiny.ShowChangedLinesLabel, "Zero contribution should remain a minimal ribbon without a changed-lines label.");
        Check(layout.WeekLabels.Count == 3 && layout.Areas.All(area => area.Color.Length == 7), "Graph label or original hash-color models missing.");
    }

    private static async Task EmptyAndBareAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        StatisticsReport stats = await new StatisticsPlugin().AnalyzeAsync(fixture.Repository);
        GitImpactReport impact = await new GitImpactPlugin().AnalyzeAsync(fixture.Repository);
        Check(stats.TotalCommits == 0 && stats.Lines.Total == 0 && stats.Charts.SelectMany(chart => chart.Slices).All(slice => double.IsFinite(slice.Fraction)), "Empty charts contain non-finite percentages.");
        Check(impact.Total.Commits == 0 && impact.CreateLayout(300).Width == 0, "Unborn impact should be empty.");
        fixture.Write("a.cs", "code\n");
        await fixture.CommitAsync("Bare contents");
        string bare = Path.Combine(fixture.Root, "bare.git");
        await fixture.Runner.RunAsync(fixture.Root, ["clone", "--bare", fixture.Repository.Path, bare]);
        GitRepository repository = new(bare);
        stats = await new StatisticsPlugin().AnalyzeAsync(repository);
        impact = await new GitImpactPlugin().AnalyzeAsync(repository);
        Check(stats.TotalCommits == 1 && stats.Lines.Total == 0 && impact.Total.Commits == 1, "Bare history must work without pretending to have working-tree LOC.");
    }

    private static async Task CancellationAsync()
    {
        using StatisticsFixture fixture = await StatisticsFixture.CreateAsync();
        fixture.Write("a.cs", "code\n");
        await fixture.CommitAsync("Cancel fixture");
        string refs = (await fixture.Repository.RunAsync(["show-ref"])).StandardOutput;
        byte[] original = System.IO.File.ReadAllBytes(Path.Combine(fixture.Repository.Path, "a.cs"));
        using CancellationTokenSource pre = new();
        pre.Cancel();
        await ThrowsCancelledAsync(() => new StatisticsPlugin().AnalyzeAsync(fixture.Repository, cancellationToken: pre.Token));
        await ThrowsCancelledAsync(() => new GitImpactPlugin().AnalyzeAsync(fixture.Repository, cancellationToken: pre.Token));
        using CancellationTokenSource mid = new();
        await ThrowsCancelledAsync(() => new GitImpactPlugin().AnalyzeAsync(fixture.Repository,
            progress: new InlineProgress<GitImpactProgress>(_ => mid.Cancel()), cancellationToken: mid.Token));
        using CancellationTokenSource lines = new();
        await ThrowsCancelledAsync(() => new StatisticsPlugin().AnalyzeAsync(fixture.Repository,
            progress: new InlineProgress<StatisticsProgress>(_ => lines.Cancel()), cancellationToken: lines.Token));
        using CancellationTokenSource parser = new();
        parser.Cancel();
        try { StatisticsCodeFile.Parse(new FileInfo(Path.Combine(fixture.Repository.Path, "a.cs")), parser.Token); throw new InvalidOperationException("Parser ignored cancellation."); }
        catch (OperationCanceledException) { }
        Check(refs == (await fixture.Repository.RunAsync(["show-ref"])).StandardOutput && original.SequenceEqual(System.IO.File.ReadAllBytes(Path.Combine(fixture.Repository.Path, "a.cs"))), "Cancelled analysis changed the repository.");
    }

    private static StatisticsFile File(StatisticsReport report, string path) => report.Files.Single(file => file.Path == path);
    private static Dictionary<string, string?> Values() => new(StringComparer.Ordinal);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task ThrowsCancelledAsync(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation.");
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}

internal sealed class StatisticsFixture : IDisposable
{
    private StatisticsFixture(string root, GitRepository repository) { Root = root; Repository = repository; }
    internal string Root { get; }
    internal GitRepository Repository { get; }
    internal GitRunner Runner { get; } = new();
    internal static async Task<StatisticsFixture> CreateAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ge-stat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        GitRepository repository = await GitRepository.InitAsync(Path.Combine(root, "repo"));
        await repository.SetConfigAsync("user.name", "Statistics User");
        await repository.SetConfigAsync("user.email", "statistics@example.invalid");
        await repository.SetConfigAsync("commit.gpgsign", "false");
        await repository.SetConfigAsync("core.hooksPath", PluginTestProfile.EmptyHooks(repository));
        await repository.SetConfigAsync("core.autocrlf", "false");
        return new(root, repository);
    }
    internal void Write(string path, string content) => WriteBytes(path, Encoding.UTF8.GetBytes(content));
    internal void WriteBytes(string path, byte[] content)
    {
        string full = Path.Combine(Repository.Path, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
    }
    internal async Task GitAsync(params string[] arguments)
    {
        GitResult result = await Runner.RunAsync(Repository.Path, arguments);
        if (!result.Success) throw new GitCommandException(arguments, result);
    }
    internal async Task CommitAsync(string message, string date = "2026-01-05T12:00:00+00:00", string author = "Statistics User",
        string email = "statistics@example.invalid", string? committerDate = null)
    {
        await Repository.StageAllAsync();
        string[] arguments = ["commit", "--allow-empty", "-m", message];
        GitResult result = await Runner.RunAsync(Repository.Path, arguments, environment: new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_NAME"] = author, ["GIT_AUTHOR_EMAIL"] = email, ["GIT_AUTHOR_DATE"] = date,
            ["GIT_COMMITTER_DATE"] = committerDate ?? date
        });
        if (!result.Success) throw new GitCommandException(arguments, result);
    }
    public void Dispose() => PluginTestFiles.DeleteDirectory(Root);
}
