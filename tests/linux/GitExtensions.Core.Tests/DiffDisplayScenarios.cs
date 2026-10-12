using System.Globalization;
using GitCommands.Settings;
using GitExtensions.Linux;

internal static class DiffDisplayScenarios
{
    private const string Literal = ":[literal]* λ\t\n[1].txt";
    private const string Adjacent = ":[literal]* λ\t\n1.txt";
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("display canonical worktree and staging invariant", WorktreeCanonicalAsync),
        ("display reverse defaults and custom colors", CustomColorsAsync),
        ("display real moved blocks and configured colors", MovedColorsAsync),
        ("display word diff unicode and custom regex", WordDiffAsync),
        ("display revision rename literal paths", RenameAsync),
        ("display root commit and bare repository", RootAndBareAsync),
        ("display comparison index worktree and untracked", ComparisonsAsync),
        ("display combined three parents read-only", CombinedAsync),
        ("display range series paths colors read-only", RangeAsync),
        ("difftastic configured original tool and process environment", DifftasticAsync),
        ("difftastic root revision and untracked presentation", DifftasticEndpointsAsync),
        ("difftastic absent tool fallback and tool errors", DifftasticFallbackAsync),
        ("display configured external helpers never affect canonical patch", ExternalInvariantAsync),
        ("display cancellation terminates actual difftool process", CancellationAsync),
        ("display validation and empty comparison", ValidationAsync)
    ];

    public static async Task WorktreeCanonicalAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository;
        await r.SetConfigAsync("color.diff", "always");
        f.Write(Literal, "literal new λ 🦊\n"); f.Write(Adjacent, "adjacent forbidden\n");
        byte[] config = File.ReadAllBytes(f.File(".git/config")), index = File.ReadAllBytes(f.File(".git/index"));
        string canonical = await r.GetDiffAsync(Literal);
        GitDiffDocument document = await r.GetDiffDocumentAsync(Literal);
        Check(document.Patch == canonical && !canonical.Contains('\x1b'), "Canonical worktree patch explicitly overrides color.diff=always.");
        Check(document.UseGitColoring && document.Display.Contains('\x1b') && GitAnsiColorParser.StripAnsi(document.Display) == canonical && document.CanStageEquivalent, "Default colored patch preserves exactly the staging buffer.");
        Check(!canonical.Contains("adjacent forbidden", StringComparison.Ordinal), "Worktree path is a literal argument, not a pathspec pattern.");
        Unchanged(f, config, index);
        await r.ApplyPatchAsync(document.Patch);
        Check((await r.GetStatusAsync()).Changes.Single(change => change.Path == Literal).IsStaged, "The canonical document is actually accepted by git apply --cached.");
        Check((await r.GetStatusAsync()).Changes.Single(change => change.Path == Adjacent).IsStaged == false, "Canonical staging leaves an adjacent metacharacter path alone.");
        GitDiffDocument plain = await r.GetDiffDocumentAsync(Literal, staged: true, displayOptions: new(UseGitColoring: false));
        Check(!plain.UseGitColoring && plain.Display == plain.Patch && plain.CanStageEquivalent, "Turning Git colors off avoids presentation changes.");
        await ConcurrentWorktreeAsync();
    }

    private static async Task ConcurrentWorktreeAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync();
        f.Write(Literal, "first read λ\n");
        string wrapper = Path.Combine(f.Root, "changing-git");
        string pythonPath = System.Text.Json.JsonSerializer.Serialize(f.File(Literal));
        await WriteExecutableAsync(wrapper, "#!/usr/bin/python3\nimport os,sys\nif '--color=always' in sys.argv[1:]:\n    with open(" + pythonPath + ", 'w', encoding='utf-8') as changed: changed.write('second read λ\\n')\nos.execvp('git', ['git'] + sys.argv[1:])\n");
        GitRepository racing = new(f.Repository.Path, new GitRunner(wrapper));
        GitDiffDocument document = await racing.GetDiffDocumentAsync(Literal);
        Check(document.Patch.Contains("first read λ", StringComparison.Ordinal) && document.Display.Contains("second read λ", StringComparison.Ordinal), "A controlled concurrent writer changes the worktree between the two real Git reads.");
        Check(!document.CanStageEquivalent, "A presentation race cannot claim canonical selection offsets or staging equivalence.");
    }

    public static async Task CustomColorsAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository;
        f.Write(Literal, "literal new λ 🦊\n");
        GitDiffDocument reverse = await r.GetDiffDocumentAsync(Literal);
        GitDiffDocument normal = await r.GetDiffDocumentAsync(Literal, displayOptions: new(ReverseGitColoring: false));
        Check(reverse.Display.Contains("\x1b[7;31m", StringComparison.Ordinal) && reverse.Display.Contains("\x1b[7;32m", StringComparison.Ordinal), "Original reverse defaults color removed and added backgrounds.");
        Check(normal.Display.Contains("\x1b[31m", StringComparison.Ordinal) && normal.Display.Contains("\x1b[32m", StringComparison.Ordinal), "Reverse disabled retains normal red/green foregrounds.");
        await r.SetConfigAsync("color.diff.old", "#112233 bold"); await r.SetConfigAsync("color.diff.new", "yellow blue");
        await r.SetConfigAsync("color.diff.meta", "cyan"); await r.SetConfigAsync("diff.colormoved", "no");
        await r.SetConfigAsync("diff.noprefix", "true");
        byte[] config = File.ReadAllBytes(f.File(".git/config")), index = File.ReadAllBytes(f.File(".git/index"));
        GitDiffDocument custom = await r.GetDiffDocumentAsync(Literal);
        Check(custom.Display.Contains("38;2;17;34;51", StringComparison.Ordinal) && custom.Display.Contains("\x1b[33;44m", StringComparison.Ordinal), "Custom truecolor and explicit background are never replaced by reverse defaults.");
        Check(!custom.Patch.Contains("--- a/", StringComparison.Ordinal) && custom.CanStageEquivalent, "A custom noprefix setting survives both canonical and display commands.");
        Unchanged(f, config, index);
        await r.SetConfigAsync("color.diff.old", ""); await r.SetConfigAsync("color.diff.new", "");
        GitDiffDocument emptyColors = await r.GetDiffDocumentAsync(Literal);
        Check(emptyColors.UseGitColoring && emptyColors.CanStageEquivalent && !emptyColors.Display.Contains("38;2;17;34;51", StringComparison.Ordinal)
            && !emptyColors.Display.Contains("\x1b[7;32m", StringComparison.Ordinal), "Explicit empty colors stay empty and still distinguish Git coloring from application highlighting.");
    }

    public static async Task MovedColorsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        string[] a = Enumerable.Range(0, 8).Select(i => "alpha_unique_long_identifier_" + i + "();").ToArray();
        string[] b = Enumerable.Range(0, 8).Select(i => "bravo_unique_long_identifier_" + i + "();").ToArray();
        f.Write("moved.txt", string.Join('\n', a.Concat(["separator_one", "separator_two"]).Concat(b)) + "\n");
        await r.StageAllAsync(); await r.CommitAsync("Original blocks");
        f.Write("moved.txt", string.Join('\n', b.Concat(["separator_one", "separator_two"]).Concat(a)) + "\n");
        GitDiffDocument moved = await r.GetDiffDocumentAsync("moved.txt", options: new(ContextLines: 0));
        Check(moved.Display.Contains("104m", StringComparison.Ordinal) || moved.Display.Contains("105m", StringComparison.Ordinal), "A real moved block receives the original bright moved background.");
        Check(moved.CanStageEquivalent, "Moved coloring changes no canonical text or offsets.");
        await r.SetConfigAsync("diff.colormoved", "plain"); await r.SetConfigAsync("diff.colormovedws", "ignore-all-space");
        await r.SetConfigAsync("color.diff.oldmoved", "cyan"); await r.SetConfigAsync("color.diff.newmoved", "yellow");
        GitDiffDocument custom = await r.GetDiffDocumentAsync("moved.txt", options: new(ContextLines: 0));
        Check(custom.Display.Contains("\x1b[36m", StringComparison.Ordinal) && custom.Display.Contains("\x1b[33m", StringComparison.Ordinal), "Configured moved mode and colors remain effective.");
        Check(custom.Patch == moved.Patch, "Changing display colors never changes the patch.");
    }

    public static async Task WordDiffAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository;
        f.Write(Literal, "literal newer λ 🐧\n");
        GitDiffDocument document = await r.GetDiffDocumentAsync(Literal, displayOptions: new(UseGitColoring: false, Appearance: GitDiffAppearance.GitWordDiff));
        Check(document.EffectiveAppearance == GitDiffAppearance.GitWordDiff && !document.CanStageEquivalent && document.Display.Contains('\x1b'), "Word mode is explicitly colored and read-only even with patch coloring disabled.");
        Check(document.Patch == await r.GetDiffAsync(Literal) && GitAnsiColorParser.StripAnsi(document.Display) != document.Patch, "Word rendering leaves the canonical unified patch separate.");
        await r.SetConfigAsync("diff.wordregex", ".");
        GitDiffDocument character = await r.GetDiffDocumentAsync(Literal, displayOptions: new(ReverseGitColoring: false, Appearance: GitDiffAppearance.GitWordDiff));
        GitResult direct = await new GitRunner().RunAsync(r.Path, ["--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", "-c", "color.diff.old=red", "-c", "color.diff.new=green", "--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--color=always", "--word-diff=color", "--", Literal]);
        Check(direct.Success && character.Display == direct.StandardOutput, "Custom regex is used exactly as configured by real Git, without shell quoting artifacts.");
        Check(character.Display.Contains("λ", StringComparison.Ordinal) && character.Display.Contains("🐧", StringComparison.Ordinal), "UTF8 Unicode survives word presentation.");
    }

    public static async Task RenameAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository;
        const string renamed = "new [name]* λ\t\n.txt";
        File.Move(f.File(Literal), f.File(renamed)); f.Write(renamed, "literal old λ 🦊\nmore content\n");
        await r.StageAllAsync(); await r.CommitAsync("Literal rename");
        GitRevisionChanges changes = await r.GetRevisionChangesAsync("HEAD");
        GitRevisionFile file = changes.Files.Single(file => file.Path == renamed);
        Check(file.OriginalPath == Literal, "The real rename list preserves the raw previous name.");
        GitDiffDocument document = await r.GetRevisionFileDiffDocumentAsync(changes, file);
        Check(document.Patch == await r.GetRevisionFileDiffAsync(changes, file) && document.CanStageEquivalent, "Revision rename reads both literal names and stays canonically identical.");
        Check(document.Patch.Contains("rename from", StringComparison.Ordinal) && !document.Patch.Contains("adjacent old", StringComparison.Ordinal), "Rename output is complete and excludes adjacent matching names.");
        f.Write(renamed, "literal old λ 🦊\nmore content\nΔ\n");
        GitDiffDocument worktree = await r.GetDiffDocumentAsync(renamed, revision: "HEAD^", originalPath: Literal);
        Check(worktree.Patch.Contains("rename from", StringComparison.Ordinal) && worktree.CanStageEquivalent, "Worktree documents retain both raw rename paths.");
    }

    public static async Task RootAndBareAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository;
        GitRevisionChanges root = await r.GetRevisionChangesAsync("HEAD"); GitRevisionFile file = root.Files.Single(file => file.Path == Literal);
        GitDiffDocument original = await r.GetRevisionFileDiffDocumentAsync(root, file);
        Check(root.BaseRevision is null && original.Patch.Contains("new file mode", StringComparison.Ordinal) && original.CanStageEquivalent, "Root commit documents compare against the real empty tree.");
        GitRepository bare = await GitRepository.CloneAsync(r.Path, Path.Combine(f.Root, "bare.git"), new GitCloneOptions(Bare: true));
        GitRevisionChanges bareRoot = await bare.GetRevisionChangesAsync("HEAD");
        GitDiffDocument document = await bare.GetRevisionFileDiffDocumentAsync(bareRoot, file);
        Check(document.Patch == original.Patch && document.CanStageEquivalent && bare.IsBare, "Bare commit display requires no worktree.");
        await ExpectAsync<InvalidOperationException>(() => bare.GetDiffDocumentAsync(Literal));
    }

    public static async Task ComparisonsAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository;
        f.Write(Literal, "literal indexed λ\n"); await r.StageAsync([Literal]); f.Write(Literal, "literal working λ\n");
        foreach ((GitCompareRevision from, GitCompareRevision to) in new[]
        {
            (new GitCompareRevision("HEAD"), new GitCompareRevision(Kind: GitCompareRevisionKind.Index)),
            (new GitCompareRevision(Kind: GitCompareRevisionKind.Index), new GitCompareRevision(Kind: GitCompareRevisionKind.WorkingTree)),
            (new GitCompareRevision(Kind: GitCompareRevisionKind.WorkingTree), new GitCompareRevision("HEAD"))
        })
        {
            GitComparison comparison = await r.GetComparisonAsync(from, to); GitRevisionFile file = comparison.Files.Single(file => file.Path == Literal);
            GitComparisonDiffOptions options = new(IgnoreWhitespaceKind.AllSpace, 0, TreatAllFilesAsText: true);
            GitDiffDocument document = await r.GetComparisonFileDiffDocumentAsync(comparison, file, options);
            Check(document.Patch == await r.GetComparisonFileDiffAsync(comparison, file, options) && document.CanStageEquivalent, "Index/worktree and reverse endpoints share the exact canonical command and options.");
        }
        const string untracked = ":[untracked]* λ\t\n.txt"; f.Write(untracked, "new local λ\n");
        GitComparison untrackedComparison = await r.GetComparisonAsync(new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree));
        GitDiffDocument addition = await r.GetComparisonFileDiffDocumentAsync(untrackedComparison, untrackedComparison.Files.Single(file => file.Path == untracked));
        Check(addition.Patch.Contains("new local λ", StringComparison.Ordinal) && addition.CanStageEquivalent, "No-index output accepts Git exit 1 and preserves literal Unicode paths.");
    }

    public static async Task CombinedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        string root = await r.ResolveRevisionAsync("HEAD"); List<string> parents = [];
        for (int i = 0; i < 3; i++)
        {
            await r.RunAsync(["checkout", "-b", "side" + i, root]); f.Write("seed.txt", "parent " + i + "\n");
            await r.StageAllAsync(); await r.CommitAsync("Parent " + i); parents.Add(await r.ResolveRevisionAsync("HEAD"));
        }
        f.Write("seed.txt", "all-parent resolution λ\n"); await r.StageAllAsync();
        string tree = (await r.RunAsync(["write-tree"])).StandardOutput.Trim();
        GitResult created = await new GitRunner().RunAsync(r.Path, ["commit-tree", tree, "-p", parents[0], "-p", parents[1], "-p", parents[2]], standardInput: "Three parents\n"); Check(created.Success, created.StandardError);
        await r.RunAsync(["update-ref", "HEAD", created.StandardOutput.Trim()]);
        GitCombinedDiff combined = await r.GetCombinedDiffAsync("HEAD");
        GitDiffDocument document = await r.GetCombinedFileDiffDocumentAsync(combined, combined.Files.Single(), new(UseGitColoring: true), new(Appearance: GitDiffAppearance.GitWordDiff));
        Check(document.EffectiveAppearance == GitDiffAppearance.Patch && !document.CanStageEquivalent && !document.Patch.Contains('\x1b'), "Combined rejects word mode while retaining a separate uncolored canonical document.");
        Check(document.Patch.Contains("@@@@ ", StringComparison.Ordinal) && document.Patch == await r.GetCombinedFileDiffAsync(combined, combined.Files.Single()), "All three parent columns are preserved unchanged.");
        Check(document.Display.Contains('\x1b') && GitAnsiColorParser.StripAnsi(document.Display) == document.Patch, "Combined uses its own readonly colored patch presentation.");
    }

    public static async Task RangeAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository; string root = await r.ResolveRevisionAsync("HEAD");
        await r.RunAsync(["checkout", "-b", "series-a"]); f.Write(Literal, "old range value λ\n"); await r.StageAllAsync(); await r.CommitAsync("Patch old");
        await r.RunAsync(["checkout", "-b", "series-b", root]); f.Write(Literal, "new range value λ\n"); await r.StageAllAsync(); await r.CommitAsync("Patch new");
        GitRangeDiffComparison range = await r.CreateRangeDiffComparisonAsync("series-a", "series-b");
        GitRangeDiffOptions options = new(Paths: [Literal], UseGitColoring: true);
        GitDiffDocument document = await r.GetRangeDiffDocumentAsync(range, options, new(Appearance: GitDiffAppearance.Difftastic));
        Check(document.EffectiveAppearance == GitDiffAppearance.Patch && !document.CanStageEquivalent && document.Display.Contains('\x1b'), "Range ignores incompatible appearance requests and remains readonly.");
        Check(document.Patch == await r.GetRangeDiffAsync(range, options with { UseGitColoring = false }) && !document.Patch.Contains('\x1b'), "Range canonical output preserves the explicit bases and raw literal path.");
        Check(document.Display.Contains("\x1b[", StringComparison.Ordinal) && document.Patch.Contains("Patch", StringComparison.Ordinal), "The actual changed series is displayed with original range coloring.");
        GitDiffDocument summary = await r.GetRangeDiffDocumentAsync(range, new(ContextLines: 0), new(UseGitColoring: false));
        Check(!summary.Patch.Contains("@@", StringComparison.Ordinal) && summary.UseGitColoring && summary.Display.Contains('\x1b')
            && GitAnsiColorParser.StripAnsi(summary.Display) == summary.Patch && !summary.CanStageEquivalent,
            "Range coloring is always enabled as in ViewRangeDiffAsync, even for a zero-context summary and disabled global patch colors.");
        GitRepository bare = await GitRepository.CloneAsync(r.Path, Path.Combine(f.Root, "range.git"), new GitCloneOptions(Bare: true));
        GitDiffDocument bareDocument = await bare.GetRangeDiffDocumentAsync(range, options);
        Check(bareDocument.Patch == document.Patch, "Bare range display uses real object IDs without checkout.");
    }

    public static async Task DifftasticAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository;
        await ConfigureDifftasticAsync(f); f.Write(Literal, "literal new λ 🦊\n");
        byte[] config = File.ReadAllBytes(f.File(".git/config")), index = File.ReadAllBytes(f.File(".git/index"));
        GitDiffDocument document = await r.GetDiffDocumentAsync(Literal, options: new(IgnoreWhitespaceKind.AllSpace, ShowEntireFile: true), displayOptions: new(Appearance: GitDiffAppearance.Difftastic, SyntaxHighlighting: false, ViewerWidth: 3000));
        Check(await r.IsDifftasticAvailableAsync() && document.EffectiveAppearance == GitDiffAppearance.Difftastic && !document.CanStageEquivalent, "Only the original difftool.difftastic.cmd configuration enables Difftastic.");
        Check(document.Display.Contains("FAKE-DIFFTASTIC", StringComparison.Ordinal) && document.Display.Contains("literal new λ", StringComparison.Ordinal), "The configured real helper reads Git's temporary old/new files.");
        Check(document.DifftasticWidth == 200 && document.Display.Contains("DFT=always|light|off|9000|on|200", StringComparison.Ordinal), "Original syntax/context/stripCR/width env is scoped to this Git process.");
        Check(document.Patch.Contains("literal new λ", StringComparison.Ordinal) && !document.Patch.Contains("FAKE", StringComparison.Ordinal), "Structural presentation never replaces the patch.");
        Unchanged(f, config, index);
        string? previous = Environment.GetEnvironmentVariable("DFT_BACKGROUND"), previousWidth = Environment.GetEnvironmentVariable("DFT_WIDTH");
        try
        {
            Environment.SetEnvironmentVariable("DFT_BACKGROUND", "dark");
            GitDiffDocument custom = await r.GetDiffDocumentAsync(Literal, displayOptions: new(Appearance: GitDiffAppearance.Difftastic, ViewerWidth: 1));
            Check(custom.DifftasticWidth == 88 && custom.Display.Contains("DFT=always|dark|on|3|off|88", StringComparison.Ordinal), "Explicit user env survives and original minimum width is 88 columns.");
            Environment.SetEnvironmentVariable("DFT_WIDTH", "177");
            GitDiffDocument overrideWidth = await r.GetDiffDocumentAsync(Literal, displayOptions: new(Appearance: GitDiffAppearance.Difftastic));
            Check(overrideWidth.UseGitColoring && overrideWidth.DifftasticWidth == 177 && overrideWidth.Display.Contains("|177", StringComparison.Ordinal), "The document captures the exact valid user width rather than recalculating it later.");
        }
        finally { Environment.SetEnvironmentVariable("DFT_BACKGROUND", previous); Environment.SetEnvironmentVariable("DFT_WIDTH", previousWidth); }
        Check(Environment.GetEnvironmentVariable("DFT_CONTEXT") is null && Environment.GetEnvironmentVariable("DFT_WIDTH") is null, "DFT defaults were not written to the application environment.");
    }

    public static async Task DifftasticEndpointsAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository; await ConfigureDifftasticAsync(f);
        GitRevisionChanges root = await r.GetRevisionChangesAsync("HEAD"); GitRevisionFile file = root.Files.Single(file => file.Path == Literal);
        GitDiffDocument document = await r.GetRevisionFileDiffDocumentAsync(root, file, displayOptions: new(Appearance: GitDiffAppearance.Difftastic));
        Check(document.EffectiveAppearance == GitDiffAppearance.Difftastic && document.Display.Contains("literal old λ", StringComparison.Ordinal), "Root Difftastic compares with the actual empty tree, not the current index.");
        f.Write("new λ.txt", "untracked structural λ\n");
        GitComparison comparison = await r.GetComparisonAsync(new(Kind: GitCompareRevisionKind.Index), new(Kind: GitCompareRevisionKind.WorkingTree));
        GitDiffDocument untracked = await r.GetComparisonFileDiffDocumentAsync(comparison, comparison.Files.Single(file => file.Path == "new λ.txt"), displayOptions: new(Appearance: GitDiffAppearance.Difftastic));
        Check(untracked.EffectiveAppearance == GitDiffAppearance.Difftastic && untracked.Display.Contains("untracked structural λ", StringComparison.Ordinal), "No-index Difftastic handles untracked files and Git's difference exit code.");
    }

    public static async Task DifftasticFallbackAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository; f.Write(Literal, "fallback λ\n");
        GitDiffDocument absent = await r.GetDiffDocumentAsync(Literal, displayOptions: new(Appearance: GitDiffAppearance.Difftastic));
        Check(!await r.IsDifftasticAvailableAsync() && absent.EffectiveAppearance == GitDiffAppearance.Patch && absent.CanStageEquivalent, "Unavailable Difftastic falls back to the real patch.");
        await r.SetConfigAsync("difftool.difftastic.cmd", "gitextensions-definitely-missing-tool-42 \"$LOCAL\" \"$REMOTE\"");
        GitDiffDocument missing = await r.GetDiffDocumentAsync(Literal, displayOptions: new(Appearance: GitDiffAppearance.Difftastic));
        Check(missing.EffectiveAppearance == GitDiffAppearance.Patch && missing.Patch == absent.Patch && missing.CanStageEquivalent, "A stale configured executable falls back without replacing the canonical patch.");
        await r.SetConfigAsync("difftool.difftastic.cmd", "printf 'business item not found\\n' >&2; exit 7");
        await ExpectAsync<GitCommandException>(() => r.GetDiffDocumentAsync(Literal, displayOptions: new(Appearance: GitDiffAppearance.Difftastic)));
    }

    public static async Task ExternalInvariantAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository; f.Write(Literal, "external bypass λ\n");
        string marker = Path.Combine(f.Root, "external-called"); string helper = Path.Combine(f.Root, "external-tool");
        await WriteExecutableAsync(helper, "#!/bin/sh\nprintf 'called' > " + Quote(marker) + "\nprintf 'BAD-EXTERNAL'\n");
        await r.SetConfigAsync("diff.external", Quote(helper));
        foreach (GitDiffAppearance appearance in new[] { GitDiffAppearance.Patch, GitDiffAppearance.GitWordDiff, GitDiffAppearance.Difftastic })
        {
            GitDiffDocument document = await r.GetDiffDocumentAsync(Literal, displayOptions: new(Appearance: appearance));
            Check(!File.Exists(marker) && document.Patch.Contains("external bypass λ", StringComparison.Ordinal), "External diff config is never executed by canonical or ordinary presentation commands.");
        }
    }

    public static async Task CancellationAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository; f.Write(Literal, "slow diff\n");
        string helper = Path.Combine(f.Root, "slow-difftastic"), pidFile = Path.Combine(f.Root, "tool.pid");
        await WriteExecutableAsync(helper, "#!/bin/sh\nprintf '%s' \"$$\" > " + Quote(pidFile) + "\nexec sleep 60\n");
        await r.SetConfigAsync("difftool.difftastic.cmd", Quote(helper));
        using CancellationTokenSource cancel = new();
        Task<GitDiffDocument> reading = r.GetDiffDocumentAsync(Literal, displayOptions: new(Appearance: GitDiffAppearance.Difftastic), cancellationToken: cancel.Token);
        for (int i = 0; i < 200 && !File.Exists(pidFile) && !reading.IsCompleted; i++) { await Task.Delay(10); }
        Check(File.Exists(pidFile), "The actual configured tool started before cancellation.");
        int pid = int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture); cancel.Cancel();
        await ExpectAsync<OperationCanceledException>(() => reading);
        for (int i = 0; i < 200 && Running(pid); i++) { await Task.Delay(10); }
        Check(!Running(pid), "Cancellation terminates the actual tool descendant, accepting reaped or zombie states.");
        using CancellationTokenSource preCanceled = new(); preCanceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => r.GetDiffDocumentAsync(Literal, cancellationToken: preCanceled.Token));
    }

    public static async Task ValidationAsync()
    {
        using RepositoryFixture f = await LiteralFixtureAsync(); GitRepository r = f.Repository;
        await ExpectAsync<ArgumentException>(() => r.GetDiffDocumentAsync("../outside"));
        await ExpectAsync<ArgumentException>(() => r.GetDiffDocumentAsync(Literal, originalPath: "bad\0path"));
        await ExpectAsync<ArgumentOutOfRangeException>(() => r.GetDiffDocumentAsync(Literal, options: new(ContextLines: -1)));
        await ExpectAsync<ArgumentOutOfRangeException>(() => r.GetDiffDocumentAsync(Literal, displayOptions: new(Appearance: (GitDiffAppearance)42)));
        GitComparison same = await r.GetComparisonAsync(new("HEAD"), new("HEAD"));
        GitDiffDocument document = await r.GetComparisonFileDiffDocumentAsync(same, new(Literal, 'M'));
        Check(document.Patch.Length == 0 && document.Display.Length == 0 && document.EffectiveAppearance == GitDiffAppearance.Patch, "Same real endpoint cannot invent a patch or launch another viewer.");
        await LiteralOptionNamesAsync();
    }

    private static async Task LiteralOptionNamesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false); GitRepository r = f.Repository;
        string[] paths = ["--root", "--", "--no-color", "--no-ext-diff", "range-diff", "diff-tree"];
        foreach (string path in paths) { f.Write(path, "original literal " + path + "\n"); }
        await r.StageAllAsync(); await r.CommitAsync("Literal option-looking filenames");
        await ConfigureDifftasticAsync(f);
        string wrapper = Path.Combine(f.Root, "recording-git"), argvLog = Path.Combine(f.Root, "git-argv.jsonl");
        string pythonLog = System.Text.Json.JsonSerializer.Serialize(argvLog);
        await WriteExecutableAsync(wrapper, "#!/usr/bin/python3\nimport os,sys,json\nwith open(" + pythonLog + ", 'a', encoding='utf-8') as log: log.write(json.dumps(sys.argv[1:]) + '\\n')\nos.execvp('git', ['git'] + sys.argv[1:])\n");
        r = new GitRepository(r.Path, new GitRunner(wrapper));
        foreach (string path in paths)
        {
            f.Write(path, "changed literal " + path + "\n");
            GitDiffDocument colored = await r.GetDiffDocumentAsync(path);
            Check(colored.CanStageEquivalent && colored.Patch.Contains("changed literal " + path, StringComparison.Ordinal), "A command/option-looking filename stays a literal path after --.");
            GitDiffDocument structural = await r.GetDiffDocumentAsync(path, displayOptions: new(Appearance: GitDiffAppearance.Difftastic));
            Check(structural.EffectiveAppearance == GitDiffAppearance.Difftastic && structural.Display.Contains("FAKE-DIFFTASTIC " + path + "\n", StringComparison.Ordinal)
                && structural.Display.Contains("original literal " + path, StringComparison.Ordinal) && structural.Display.Contains("changed literal " + path, StringComparison.Ordinal),
                "Difftastic identifies command options only before -- and passes the literal filename to Git's existing tool protocol.");
            string[] toolArguments = File.ReadAllLines(argvLog).Select(line => System.Text.Json.JsonSerializer.Deserialize<string[]>(line)!)
                .Last(arguments => arguments.Contains("difftool"));
            int terminator = Array.IndexOf(toolArguments, "--");
            Check(terminator > 0 && toolArguments[(terminator + 1)..].SequenceEqual([path]) && !toolArguments[..terminator].Contains("--root"),
                "The actual ProcessStartInfo argv independently confirms the unchanged raw pathname follows Git's literal separator, without an invented root option.");
        }
    }

    private static async Task<RepositoryFixture> LiteralFixtureAsync()
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
        try
        {
            f.Write(Literal, "literal old λ 🦊\n"); f.Write(Adjacent, "adjacent old\n");
            await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Literal base"); return f;
        }
        catch { f.Dispose(); throw; }
    }
    private static async Task ConfigureDifftasticAsync(RepositoryFixture f)
    {
        string helper = Path.Combine(f.Root, "fake difftastic λ");
        await WriteExecutableAsync(helper, "#!/bin/sh\nprintf 'FAKE-DIFFTASTIC %s\\n' \"$1\"\nprintf 'DFT=%s|%s|%s|%s|%s|%s\\n' \"$DFT_COLOR\" \"$DFT_BACKGROUND\" \"$DFT_SYNTAX_HIGHLIGHT\" \"$DFT_CONTEXT\" \"$DFT_STRIP_CR\" \"$DFT_WIDTH\"\nprintf 'OLD\\n'\ncat < \"$2\"\nprintf 'NEW\\n'\ncat < \"$3\"\n");
        // Git owns parsing of the user's existing difftool command. Production code invokes only Git's argument list.
        await f.Repository.SetConfigAsync("difftool.difftastic.cmd", Quote(helper) + " \"$MERGED\" \"$LOCAL\" \"$REMOTE\"");
    }
    private static async Task WriteExecutableAsync(string path, string text)
    {
        await File.WriteAllTextAsync(path, text);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Unchanged(RepositoryFixture f, byte[] config, byte[] index)
    {
        Check(File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config), "Reading display options never writes repository config.");
        Check(File.ReadAllBytes(f.File(".git/index")).SequenceEqual(index), "Reading presentation never modifies index bytes.");
    }
    private static bool Running(int pid)
    {
        string file = "/proc/" + pid + "/stat";
        if (!File.Exists(file)) { return false; }
        try { string stat = File.ReadAllText(file); return stat[(stat.LastIndexOf(')') + 2)] != 'Z'; }
        catch (IOException) when (!File.Exists(file)) { return false; }
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
