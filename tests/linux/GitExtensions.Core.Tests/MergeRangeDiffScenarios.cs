using System.Diagnostics;
using GitCommands.Settings;
using GitExtensions.Linux;
using GitExtensions.Extensibility.Git;

internal static class MergeRangeDiffScenarios
{
    private const string Literal = ":[literal]* λ\t\n[1].txt";
    private const string Adjacent = ":[literal]* λ\t\n1.txt";

    public static async Task CombinedPathsAsync()
    {
        using RepositoryFixture fixture = await CreateConflictMergeAsync();
        GitRepository repository = fixture.Repository;
        await repository.SetConfigAsync("color.diff", "always");
        GitCombinedDiff combined = await repository.GetCombinedDiffAsync("HEAD");
        Check(combined.Parents.Count == 2 && combined.Files.Select(file => file.Path).Order().SequenceEqual(new[] { Literal, Adjacent }.Order()), "Combined NUL list retains literal tabs, newlines, brackets and Unicode.");
        GitCombinedDiff filtered = await repository.GetCombinedDiffAsync("HEAD", [Literal]);
        Check(filtered.Files.Single().Path == Literal, "Combined name listing uses literal pathspecs.");
        string output = await repository.GetCombinedFileDiffAsync(combined, combined.Files.Single(file => file.Path == Literal));
        Check(output.Contains("@@@ ", StringComparison.Ordinal) && output.Contains("resolved literal", StringComparison.Ordinal) && !output.Contains("adjacent resolution", StringComparison.Ordinal), "Selected combined patch belongs only to the literal path.");
        Check(!output.Contains('\x1b'), "Combined default disables configured Git colors.");
        string noContext = await repository.GetCombinedFileDiffAsync(combined, filtered.Files.Single(), new(DiffOptions: new(ContextLines: 0), UseHistogram: true));
        Check(!noContext.Contains("unchanged context", StringComparison.Ordinal), "Zero context omits unchanged lines in combined content.");
        string text = await repository.GetCombinedFileDiffAsync(combined, filtered.Files.Single(), new(DiffOptions: new(ShowEntireFile: true, TreatAllFilesAsText: true)));
        Check(text.Contains("unchanged context", StringComparison.Ordinal), "Whole-file combined display includes context.");
        IReadOnlyList<GitRevisionDiffGroup> groups = await repository.GetRevisionDiffGroupsAsync([combined.Revision]);
        Check(groups.Select(group => group.Kind).SequenceEqual(new[] { GitRevisionDiffGroupKind.Parent, GitRevisionDiffGroupKind.Parent, GitRevisionDiffGroupKind.Combined }), "Merge groups preserve every parent before combined.");
        Check(groups.Take(2).Select(group => group.BaseRevision).SequenceEqual(combined.Parents), "Group parents are actual merge parents in Git order.");
        Check(groups.Last().CombinedDiff?.Revision == combined.Revision && groups.Last().Files.Count == 2, "Combined group carries its typed model and files.");
        Check((await repository.GetRevisionDiffGroupsAsync(["HEAD"], false)).Count == 1, "Disabling all parents retains only the first-parent group.");
        Check((await repository.GetStatusAsync()).Changes.Count == 0, "Combined reads leave index and worktree intact.");
    }

    public static async Task OmittedHunksAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        GitRepository repository = fixture.Repository;
        string[] original = Enumerable.Range(1, 45).Select(index => "line " + index).ToArray();
        fixture.Write("choice.txt", string.Join('\n', original) + "\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Common base");
        await repository.RunAsync(["checkout", "-b", "feature"]);
        string[] feature = [.. original]; feature[1] = "feature first"; feature[35] = "feature second";
        fixture.Write("choice.txt", string.Join('\n', feature) + "\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Feature choices");
        await repository.CheckoutAsync("main");
        string[] main = [.. original]; main[1] = "main first"; main[35] = "main second";
        fixture.Write("choice.txt", string.Join('\n', main) + "\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Main choices");
        await ExpectGitFailureAsync(() => repository.RunAsync(["merge", "--no-ff", "feature"]));
        string[] resolved = [.. original]; resolved[1] = main[1]; resolved[35] = feature[35];
        fixture.Write("choice.txt", string.Join('\n', resolved) + "\n");
        await repository.StageAllAsync(); await repository.CommitAsync("Pick one parent per hunk");
        GitCombinedDiff combined = await repository.GetCombinedDiffAsync("HEAD");
        Check(combined.Files.Single().Path == "choice.txt", "The --cc name listing retains a file changed against both parents.");
        string full = await repository.GetCombinedFileDiffAsync(combined, combined.Files.Single());
        string dense = await repository.GetCombinedFileDiffAsync(combined, combined.Files.Single(), new(OmitUninterestingDiff: true));
        Check(full.Contains("@@@ ", StringComparison.Ordinal) && full.Contains("main first", StringComparison.Ordinal) && full.Contains("feature second", StringComparison.Ordinal), "Default -c -p preserves picked-parent hunks.");
        Check(dense.Length == 0, "Explicit --cc omits a merge whose hunks each choose one complete parent version.");
    }

    public static async Task CleanAndRootGroupsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository repository = fixture.Repository;
        string root = await repository.ResolveRevisionAsync("HEAD");
        IReadOnlyList<GitRevisionDiffGroup> initial = await repository.GetRevisionDiffGroupsAsync([root]);
        Check(initial.Single().BaseRevision is null && initial.Single().Files.Single().Status == 'A', "Root commits expose additions without an invented parent.");
        Check((await repository.GetCombinedDiffAsync(root)).Files.Count == 0, "A non-merge commit has no combined group.");
        await repository.RunAsync(["checkout", "-b", "feature"]);
        fixture.Write("feature.txt", "feature\n"); await repository.StageAllAsync(); await repository.CommitAsync("Feature");
        await repository.CheckoutAsync("main");
        fixture.Write("main.txt", "main\n"); await repository.StageAllAsync(); await repository.CommitAsync("Main");
        await repository.RunAsync(["merge", "--no-ff", "feature", "-m", "Clean merge"]);
        IReadOnlyList<GitRevisionDiffGroup> merge = await repository.GetRevisionDiffGroupsAsync(["HEAD"]);
        Check(merge.Count == 2 && merge.All(group => group.Kind == GitRevisionDiffGroupKind.Parent), "Clean merges retain both parent comparisons and omit empty combined groups.");
        Check(merge[0].Files.Single().Path == "feature.txt" && merge[1].Files.Single().Path == "main.txt", "Each parent comparison has its own files.");
    }

    public static async Task ThreeParentCombinedAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository repository = fixture.Repository;
        string root = await repository.ResolveRevisionAsync("HEAD");
        List<string> parents = [];
        for (int index = 0; index < 3; index++)
        {
            await repository.RunAsync(["checkout", "-b", "parent" + index, root]);
            fixture.Write("seed.txt", "parent " + index + "\n"); await repository.StageAllAsync(); await repository.CommitAsync("Parent " + index);
            parents.Add(await repository.ResolveRevisionAsync("HEAD"));
        }
        fixture.Write("seed.txt", "three-parent resolution\n"); await repository.StageAllAsync();
        string tree = (await repository.RunAsync(["write-tree"])).StandardOutput.Trim();
        GitResult commit = await new GitRunner().RunAsync(repository.Path, ["commit-tree", tree, "-p", parents[0], "-p", parents[1], "-p", parents[2]], standardInput: "Three-parent merge\n");
        Check(commit.Success, commit.StandardError);
        string merge = commit.StandardOutput.Trim(); await repository.RunAsync(["update-ref", "HEAD", merge]);
        GitCombinedDiff combined = await repository.GetCombinedDiffAsync(merge);
        Check(combined.Parents.SequenceEqual(parents), "All three commit-tree parents retain order.");
        string diff = await repository.GetCombinedFileDiffAsync(combined, combined.Files.Single());
        Check(diff.Contains("@@@@ ", StringComparison.Ordinal) && diff.Contains("+++three-parent resolution", StringComparison.Ordinal), "Three-parent combined output has three prefixes and four @ delimiters.");
        Check((await repository.GetRevisionDiffGroupsAsync([merge])).Count == 4, "Three ordinary parents precede one combined group.");
    }

    public static async Task RangeSelectionsAsync()
    {
        using RepositoryFixture fixture = await CreateSeriesAsync();
        GitRepository repository = fixture.Repository;
        string root = await repository.ResolveRevisionAsync("base-root"), common = await repository.ResolveRevisionAsync("common"), first = await repository.ResolveRevisionAsync("series-a"), second = await repository.ResolveRevisionAsync("series-b");
        GitRangeDiffComparison two = (await repository.GetRangeDiffComparisonAsync([second, first]))!;
        Check(two.CommonAncestor == common && two.First.BaseRevision == common && two.Second.BaseRevision == common && two.First.CommitCount == 2 && two.Second.CommitCount == 2, "Two divergent tips derive their closest common base and actual counts.");
        GitRangeDiffComparison three = (await repository.GetRangeDiffComparisonAsync([second, root, first]))!;
        Check(three.CommonAncestor == root && three.First.BaseRevision == root && three.Second.BaseRevision == root && three.First.CommitCount == 3, "Three selections honor a selected common ancestor earlier than the actual merge base.");
        string baseA = await repository.ResolveRevisionAsync("a-first"), baseB = await repository.ResolveRevisionAsync("b-first");
        GitRangeDiffComparison four = (await repository.GetRangeDiffComparisonAsync([second, baseB, first, baseA]))!;
        Check(four.First.BaseRevision == baseA && four.Second.BaseRevision == baseB && four.First.CommitCount == 1 && four.Second.CommitCount == 1, "Four selections preserve both separate range bounds.");
        string actual = await repository.GetRangeDiffAsync(four);
        GitResult direct = await repository.RunAsync(["range-diff", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3", baseA + ".." + first, baseB + ".." + second]);
        Check(actual == direct.StandardOutput, "Four-selection output equals real Git with the two explicit ranges.");
        string lostBounds = (await repository.RunAsync(["range-diff", "--no-color", first + "..." + second])).StandardOutput;
        Check(actual != lostBounds && !actual.Contains("First patch", StringComparison.Ordinal), "The upstream four-selection lost-bases bug is not reproduced.");
        IReadOnlyList<GitRevisionDiffGroup> groups = await repository.GetRevisionDiffGroupsAsync([second, baseB, first, baseA]);
        Check(groups.Select(group => group.Kind).SequenceEqual(new[] { GitRevisionDiffGroupKind.Comparison, GitRevisionDiffGroupKind.RangeDiff, GitRevisionDiffGroupKind.BaseToSecond, GitRevisionDiffGroupKind.BaseToFirst }), "Multi-selection groups retain original A→B, range, BASE→B, BASE→A order.");
        Check(groups[1].RangeDiff == four && groups[0].BaseRevision == first && groups[0].Revision == second, "The typed range and ordinary comparison refer to the same ordered tips.");
    }

    public static async Task RangeOptionsAndPathsAsync()
    {
        using RepositoryFixture fixture = await CreateSeriesAsync();
        GitRepository repository = fixture.Repository;
        GitRangeDiffComparison comparison = await repository.CreateRangeDiffComparisonAsync("series-a", "series-b");
        await repository.SetConfigAsync("color.diff", "always");
        string output = await repository.GetRangeDiffAsync(comparison);
        Check(output.Contains("@@", StringComparison.Ordinal) && !output.Contains('\x1b'), "Default context 3 includes patch changes and explicitly disables configured ANSI colors.");
        string summary = await repository.GetRangeDiffAsync(comparison, new(ContextLines: 0));
        Check(summary.Contains("First patch", StringComparison.Ordinal) && !summary.Contains("@@", StringComparison.Ordinal), "Zero range context is summary-only, as in original FileViewer.");
        GitRangeDiffOptions options = new(ContextLines: 5, IgnoreWhitespace: IgnoreWhitespaceKind.AllSpace, TreatAllFilesAsText: true, UseHistogram: true, Paths: [Literal], CreationFactor: 80);
        string filtered = await repository.GetRangeDiffAsync(comparison, options);
        Check(!filtered.Contains("Adjacent-only patch", StringComparison.Ordinal) && filtered.Contains("First patch", StringComparison.Ordinal), "A literal NUL-safe path filter includes only its series commits, despite matching metacharacters.");
        GitResult direct = await repository.RunAsync(["--literal-pathspecs", "range-diff", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", "--no-color", "--histogram", "--ignore-all-space", "--unified=5", "--text", "--creation-factor=80", comparison.First.BaseRevision + ".." + comparison.First.TipRevision, comparison.Second.BaseRevision + ".." + comparison.Second.TipRevision, "--", Literal]);
        Check(filtered == direct.StandardOutput, "Typed options equal real Git's historical flags and exact literal path argument.");
        string entire = await repository.GetRangeDiffAsync(comparison, new(ShowEntireFile: true));
        Check(entire.Contains("@@", StringComparison.Ordinal), "Whole-file range-diff remains a patch comparison.");
        Check((await repository.GetStatusAsync()).Changes.Count == 0, "Range-diff has no worktree or index side effects.");
    }

    public static async Task InvalidRangeSelectionsAsync()
    {
        using RepositoryFixture fixture = await CreateSeriesAsync();
        GitRepository repository = fixture.Repository;
        Check(await repository.GetRangeDiffComparisonAsync(["series-a", "a-first"]) is null, "Two ancestor tips are ordinary comparison, not a range-diff.");
        Check(await repository.GetRangeDiffComparisonAsync(["series-b", "a-first", "series-a"]) is null, "Three selections require a common ancestor base.");
        Check(await repository.GetRangeDiffComparisonAsync(["series-b", "a-first", "series-a", "b-first"]) is null, "Four selections reject reversed non-ancestor bases.");
        Check(await repository.GetRangeDiffComparisonAsync(["series-b", "series-b"]) is null, "Aliases of the same commit cannot invent a second series.");
        IReadOnlyList<GitRevisionDiffGroup> ordinary = await repository.GetRevisionDiffGroupsAsync(["series-b", "a-first", "series-a"]);
        Check(ordinary.Count == 2 && ordinary.All(group => group.Kind == GitRevisionDiffGroupKind.Comparison), "Invalid series preserve ordinary comparisons for every selected revision.");
        Check((await repository.GetRevisionDiffGroupsAsync(["series-b", "b-first", "series-a", "a-first", "common"])).Count == 1, "More than four selections compare extremes only.");
        await ExpectArgumentAsync(() => repository.CreateRangeDiffComparisonAsync("series-a", "series-b", "common"));
        await ExpectArgumentAsync(() => repository.CreateRangeDiffComparisonAsync("series-a", "series-b", "b-first", "a-first"));
        GitRangeDiffComparison valid = await repository.CreateRangeDiffComparisonAsync("series-a", "series-b");
        await ExpectArgumentAsync(() => repository.GetRangeDiffAsync(valid, new(Paths: ["../outside"])));
        await ExpectArgumentAsync(() => repository.GetRangeDiffAsync(valid, new(Paths: ["path\0suffix"])));
        await ExpectArgumentAsync(() => repository.GetRangeDiffAsync(valid, new(ContextLines: -1)));
        await ExpectArgumentAsync(() => repository.GetRangeDiffComparisonAsync(["--output=" + fixture.File("injection"), "series-b"]));
        await ExpectArgumentAsync(() => repository.GetCombinedDiffAsync("--output=" + fixture.File("injection")));
        foreach (string artificial in new[] { ObjectId.WorkTreeId.ToString(), ObjectId.IndexId.ToString(), ObjectId.CombinedDiffId.ToString(), new string('0', 40) })
        {
            await ExpectArgumentAsync(() => repository.GetRangeDiffComparisonAsync([artificial, "series-b"]));
            await ExpectArgumentAsync(() => repository.CreateRangeDiffComparisonAsync(artificial, "series-b"));
            await ExpectArgumentAsync(() => repository.CreateRangeDiffComparisonAsync("series-a", artificial));
            await ExpectArgumentAsync(() => repository.CreateRangeDiffComparisonAsync("series-a", "series-b", artificial, "common"));
            await ExpectArgumentAsync(() => repository.CreateRangeDiffComparisonAsync("series-a", "series-b", "common", artificial));
        }
        string homonym = ObjectId.WorkTreeId.ToString();
        await repository.CreateBranchAsync(homonym, "series-a", checkout: false);
        await ExpectArgumentAsync(() => repository.GetRangeDiffComparisonAsync(["series-b", homonym]));
        Check((await repository.GetRangeDiffComparisonAsync(["series-b", "refs/heads/" + homonym]))?.First.TipRevision == await repository.ResolveRevisionAsync("series-a"), "Explicit ref names remain valid even when their short name resembles an artificial ID.");
        Check(!File.Exists(fixture.File("injection")), "Revision option injection cannot create files.");
        string literalRevision = "branch$(touch${IFS}injected)";
        await repository.CreateBranchAsync(literalRevision, "series-a", checkout: false);
        Check((await repository.GetRangeDiffComparisonAsync(["series-b", literalRevision]))?.First.TipRevision == await repository.ResolveRevisionAsync("series-a"), "Shell-looking revision names remain ordinary Git arguments.");
        Check(!File.Exists(fixture.File("injected")), "No shell expansion occurred.");
    }

    public static async Task BareDiffsAsync()
    {
        using RepositoryFixture merge = await CreateConflictMergeAsync();
        string barePath = Path.Combine(merge.Root, "bare merge.git");
        GitRepository bare = await GitRepository.CloneAsync(merge.Repository.Path, barePath, new GitCloneOptions(Bare: true));
        GitCombinedDiff combined = await bare.GetCombinedDiffAsync("HEAD", [Literal]);
        Check(bare.IsBare && combined.Files.Single().Path == Literal, "Bare repositories list combined literal paths.");
        Check((await bare.GetCombinedFileDiffAsync(combined, combined.Files.Single())).Contains("@@@ ", StringComparison.Ordinal), "Bare combined patch needs no worktree.");
        Check((await bare.GetRevisionDiffGroupsAsync(["HEAD"])).Count == 3, "Bare merges expose every group.");
        using RepositoryFixture series = await CreateSeriesAsync();
        GitRepository bareSeries = await GitRepository.CloneAsync(series.Repository.Path, Path.Combine(series.Root, "series.git"), new GitCloneOptions(Bare: true));
        GitRangeDiffComparison range = (await bareSeries.GetRangeDiffComparisonAsync(["series-b", "series-a"]))!;
        Check((await bareSeries.GetRangeDiffAsync(range, new(Paths: [Literal]))).Contains("First patch", StringComparison.Ordinal), "Bare range-diff uses resolved refs and literal paths.");
        Check((await bareSeries.GetStatusAsync()).Changes.Count == 0, "Bare read operations remain read-only.");
    }

    public static async Task OrdinaryDiffOptionsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository repository = fixture.Repository;
        fixture.Write("seed.txt", "seed\nnew line\n"); await repository.StageAllAsync(); await repository.CommitAsync("Addition");
        await repository.SetConfigAsync("color.diff", "always");
        GitRevisionChanges changes = await repository.GetRevisionChangesAsync("HEAD");
        string standard = await repository.GetRevisionFileDiffAsync(changes, changes.Files.Single());
        string zero = await repository.GetRevisionFileDiffAsync(changes, changes.Files.Single(), new GitComparisonDiffOptions(ContextLines: 0));
        Check(standard.Split('\n').Contains(" seed", StringComparer.Ordinal) && !zero.Split('\n').Contains(" seed", StringComparer.Ordinal), "Ordinary overload applies requested context to patch body lines without confusing the @@ function header with context.");
        Check(!standard.Contains('\x1b') && !zero.Contains('\x1b'), "Both ordinary overloads force plain text even with color.diff=always.");
        string full = await repository.GetRevisionFileDiffAsync(changes, changes.Files.Single(), new GitComparisonDiffOptions(ShowEntireFile: true, TreatAllFilesAsText: true));
        Check(full.Split('\n').Contains(" seed", StringComparer.Ordinal), "Ordinary whole-file/as-text flags are applied.");
        await ExpectArgumentAsync(() => repository.GetRevisionFileDiffAsync(changes, changes.Files.Single(), new GitComparisonDiffOptions(ContextLines: -1)));
    }

    public static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using RepositoryFixture fixture = await CreateConflictMergeAsync();
        GitCombinedDiff combined = await fixture.Repository.GetCombinedDiffAsync("HEAD");
        using RepositoryFixture series = await CreateSeriesAsync();
        GitRangeDiffComparison range = await series.Repository.CreateRangeDiffComparisonAsync("series-a", "series-b");
        foreach ((string command, GitRepository original, Func<GitRepository, CancellationToken, Task<string>> operation) in new[]
        {
            ("diff-tree", fixture.Repository, (Func<GitRepository, CancellationToken, Task<string>>)((repository, token) => repository.GetCombinedFileDiffAsync(combined, combined.Files.First(), cancellationToken: token))),
            ("range-diff", series.Repository, (Func<GitRepository, CancellationToken, Task<string>>)((repository, token) => repository.GetRangeDiffAsync(range, cancellationToken: token)))
        })
        {
            string pidFile = Path.Combine(fixture.Root, command + ".pid"), childFile = Path.Combine(fixture.Root, command + ".child"), executable = Path.Combine(fixture.Root, command + ".git");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = '" + command + "' ]; then\n  printf '%s' \"$$\" > '" + pidFile + "'\n  sleep 60 &\n  printf '%s' \"$!\" > '" + childFile + "'\n  wait\n  exit 1\n fi\ndone\nexec git \"$@\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GitRepository repository = new(original.Path, new GitRunner(executable));
            using CancellationTokenSource cancellation = new();
            Task<string> pending = operation(repository, cancellation.Token);
            Stopwatch waiting = Stopwatch.StartNew();
            while ((!File.Exists(childFile) || new FileInfo(childFile).Length == 0) && waiting.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
            Check(File.Exists(childFile), "The real read command reached its blocked process.");
            int parent = int.Parse(await File.ReadAllTextAsync(pidFile)), child = int.Parse(await File.ReadAllTextAsync(childFile));
            cancellation.Cancel();
            bool cancelled = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancellation propagates from " + command);
            waiting.Restart();
            while ((ProcessRunning(parent) || ProcessRunning(child)) && waiting.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(10); }
            Check(!ProcessRunning(parent) && !ProcessRunning(child), "Cancellation terminates the Git read and its descendant.");
        }
    }

    private static async Task<RepositoryFixture> CreateConflictMergeAsync()
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        try
        {
            GitRepository repository = fixture.Repository;
            fixture.Write(Literal, "base literal\nunchanged context\n"); fixture.Write(Adjacent, "base adjacent\n");
            await repository.StageAllAsync(); await repository.CommitAsync("Common literal base");
            await repository.RunAsync(["checkout", "-b", "feature"]);
            fixture.Write(Literal, "feature literal\nunchanged context\n"); fixture.Write(Adjacent, "feature adjacent\n");
            await repository.StageAllAsync(); await repository.CommitAsync("Feature conflicts");
            await repository.CheckoutAsync("main");
            fixture.Write(Literal, "main literal\nunchanged context\n"); fixture.Write(Adjacent, "main adjacent\n");
            await repository.StageAllAsync(); await repository.CommitAsync("Main conflicts");
            await ExpectGitFailureAsync(() => repository.RunAsync(["merge", "--no-ff", "feature"]));
            fixture.Write(Literal, "resolved literal\nunchanged context\n"); fixture.Write(Adjacent, "adjacent resolution\n");
            await repository.StageAllAsync(); await repository.CommitAsync("Merge resolutions");
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static async Task<RepositoryFixture> CreateSeriesAsync()
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        try
        {
            GitRepository repository = fixture.Repository;
            string[] literalBase = Enumerable.Range(1, 40).Select(index => "literal common " + index.ToString("D2")).ToArray();
            string[] adjacentBase = Enumerable.Range(1, 40).Select(index => "adjacent common " + index.ToString("D2")).ToArray();
            fixture.Write(Literal, string.Join('\n', literalBase) + "\n"); fixture.Write(Adjacent, string.Join('\n', adjacentBase) + "\n");
            await repository.StageAllAsync(); await repository.CommitAsync("Root"); await repository.CreateBranchAsync("base-root", checkout: false);
            fixture.Write("shared.txt", "shared ancestor\n"); await repository.StageAllAsync(); await repository.CommitAsync("Shared change"); await repository.CreateBranchAsync("common", checkout: false);
            await repository.RunAsync(["checkout", "-b", "series-a"]);
            string[] literalA = [.. literalBase]; literalA[15] = "literal edited old 1"; literalA[16] = "literal edited old 2";
            fixture.Write(Literal, string.Join('\n', literalA) + "\n"); await repository.StageAllAsync(); await repository.CommitAsync("First patch old"); await repository.CreateBranchAsync("a-first", checkout: false);
            string[] adjacentA = [.. adjacentBase]; adjacentA[20] = "adjacent edited old 1"; adjacentA[21] = "adjacent edited old 2";
            fixture.Write(Adjacent, string.Join('\n', adjacentA) + "\n"); await repository.StageAllAsync(); await repository.CommitAsync("Adjacent-only patch old");
            await repository.RunAsync(["checkout", "-b", "series-b", "common"]);
            string[] literalB = [.. literalBase]; literalB[15] = "literal edited new 1"; literalB[16] = "literal edited new 2";
            fixture.Write(Literal, string.Join('\n', literalB) + "\n"); await repository.StageAllAsync(); await repository.CommitAsync("First patch new"); await repository.CreateBranchAsync("b-first", checkout: false);
            string[] adjacentB = [.. adjacentBase]; adjacentB[20] = "adjacent edited new 1"; adjacentB[21] = "adjacent edited new 2";
            fixture.Write(Adjacent, string.Join('\n', adjacentB) + "\n"); await repository.StageAllAsync(); await repository.CommitAsync("Adjacent-only patch new");
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static bool ProcessRunning(int pid)
    {
        string path = "/proc/" + pid + "/stat";
        if (!File.Exists(path)) { return false; }
        try { return File.ReadAllText(path).Split(')')[1].TrimStart()[0] != 'Z'; } catch (IOException) { return false; }
    }
    private static async Task ExpectGitFailureAsync(Func<Task> action)
    {
        try { await action(); } catch (GitCommandException) { return; }
        throw new InvalidOperationException("Expected a real Git command failure.");
    }
    private static async Task ExpectArgumentAsync(Func<Task> action)
    {
        try { await action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected validation to reject invalid range options or paths.");
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
