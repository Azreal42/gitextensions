using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;

internal static class ABClassificationScenarios
{
    private const string Same = ":[same]*? λ\t\n.txt";
    private const string Different = "different.txt";
    private const string AddedA = "added-only-a.txt";
    private const string AddedB = "added-only-b.txt";
    private const string DeletedA = "deleted-only-a.txt";
    private const string DeletedB = "deleted-only-b.txt";
    private const string DeletedBoth = "deleted-both.txt";
    private const string AddedBoth = "added-both.txt";

    public static async Task ProvenanceAsync()
    {
        using RepositoryFixture fixture = await CreateProvenanceAsync();
        GitRepository repository = fixture.Repository;
        GitRevisionChanges direct = await repository.GetRevisionChangesAsync("side-b", "side-a");
        string before = await repository.GetRevisionFileDiffAsync(direct, direct.Files.Single(file => file.Path == Different));
        IReadOnlyList<GitRevisionDiffGroup> groups = await repository.GetRevisionDiffGroupsAsync(["side-b", "side-a"]);
        GitRevisionDiffGroup aToB = Group(groups, GitRevisionDiffGroupKind.Comparison);
        GitRevisionDiffGroup a = Group(groups, GitRevisionDiffGroupKind.BaseToFirst);
        GitRevisionDiffGroup b = Group(groups, GitRevisionDiffGroupKind.BaseToSecond);
        Check(groups.Count == 4 && groups[1].Kind == GitRevisionDiffGroupKind.RangeDiff, "Classification retains the original four-group order.");
        Check(aToB.Files.Single(file => file.Path == AddedA) is { Status: 'D', DiffStatus: DiffBranchStatus.OnlyAChange }, "An A-only addition appears removed in A→B but retains A provenance.");
        Check(aToB.Files.Single(file => file.Path == DeletedA) is { Status: 'A', DiffStatus: DiffBranchStatus.OnlyAChange }, "An A-only deletion appears added in A→B but retains A provenance.");
        Check(aToB.Files.Single(file => file.Path == AddedB).DiffStatus == DiffBranchStatus.OnlyBChange
            && aToB.Files.Single(file => file.Path == DeletedB).DiffStatus == DiffBranchStatus.OnlyBChange, "B-only additions and removals retain B provenance.");
        foreach (GitRevisionDiffGroup group in new[] { aToB, a, b })
        {
            Check(group.Files.Single(file => file.Path == Different).DiffStatus == DiffBranchStatus.UnequalChange, "Different changes of one file are unequal in all three groups.");
            Check(group.Files.All(file => file.DiffStatus != DiffBranchStatus.Unknown), "Every ordinary file in eligible A/B groups is classified.");
        }
        foreach (GitRevisionDiffGroup group in new[] { a, b })
        {
            foreach (string path in new[] { Same, AddedBoth, DeletedBoth })
            { Check(group.Files.Single(file => file.Path == path).DiffStatus == DiffBranchStatus.SameChange, "Identical modifications, additions and removals are equal since BASE."); }
        }
        Check(!aToB.Files.Any(file => file.Path is Same or AddedBoth or DeletedBoth), "Identical final trees have no A→B file delta for their equal changes.");
        Check(aToB.Files.Single(file => file.Path == "case.txt").DiffStatus == DiffBranchStatus.OnlyAChange
            && aToB.Files.Single(file => file.Path == "Case.txt").DiffStatus == DiffBranchStatus.OnlyBChange, "Case-distinct Linux paths do not collapse into one file.");
        Check(direct.Files.All(file => file.DiffStatus == DiffBranchStatus.Unknown), "Classifying groups does not mutate earlier unclassified snapshots.");
        GitRevisionFile classified = aToB.Files.Single(file => file.Path == Different);
        string after = await repository.GetRevisionFileDiffAsync(new(aToB.Revision, aToB.BaseRevision, aToB.Files), classified);
        Check(before == after, "Classification does not alter the selected file patch.");
        (string pathName, char status, string? oldPath) = classified;
        Check(pathName == Different && status == 'M' && oldPath is null, "The original three-field record deconstruction is preserved.");
        Check((await repository.GetStatusAsync()).Changes.Count == 0, "A/B classification leaves index and worktree untouched.");
    }

    public static async Task RenameScoresAsync()
    {
        const string original = "original λ\t\n.txt", aName = ":[rename-a]* λ\t\n.txt", bName = ":[rename-b]* λ\t\n.txt";
        using RepositoryFixture fixture = await CreateBranchesAsync(f => f.Write(original, Lines("rename")),
            f => File.Move(f.File(original), f.File(aName)), f => File.Move(f.File(original), f.File(bName)));
        GitRepository repository = fixture.Repository;
        IReadOnlyList<GitRevisionDiffGroup> exact = await repository.GetRevisionDiffGroupsAsync(["side-b", "side-a"]);
        foreach (GitRevisionDiffGroup group in exact.Where(group => group.Kind != GitRevisionDiffGroupKind.RangeDiff))
        {
            Check(group.Files.Single() is { Status: 'R', SimilarityPercent: 100, DiffStatus: DiffBranchStatus.SameChange }, "Exact renames with shared old names are equal content changes in all groups.");
        }
        GitRevisionFile rename = Group(exact, GitRevisionDiffGroupKind.Comparison).Files.Single();
        Check(rename.Path == bName && rename.OriginalPath == aName, "NUL parsing keeps both literal rename paths.");
        fixture.Write(bName, EditedLines("rename", 17));
        await CommitAsync(repository, "Edit renamed B file");
        IReadOnlyList<GitRevisionDiffGroup> edited = await repository.GetRevisionDiffGroupsAsync(["side-b", "side-a"]);
        GitRevisionDiffGroup comparison = Group(edited, GitRevisionDiffGroupKind.Comparison);
        GitRevisionFile changed = comparison.Files.Single();
        Check(changed is { Status: 'R', SimilarityPercent: < 100 and >= 50, DiffStatus: DiffBranchStatus.UnequalChange }, "An edited rename preserves its real score and is not excluded as R100.");
        Check(Group(edited, GitRevisionDiffGroupKind.BaseToFirst).Files.Single().DiffStatus == DiffBranchStatus.UnequalChange
            && Group(edited, GitRevisionDiffGroupKind.BaseToSecond).Files.Single().DiffStatus == DiffBranchStatus.UnequalChange, "Changed rename content is unequal in both BASE groups too.");
        string patch = await repository.GetRevisionFileDiffAsync(new(comparison.Revision, comparison.BaseRevision, comparison.Files), changed);
        string gitPatch = (await repository.RunAsync(["--literal-pathspecs", "diff", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3",
            comparison.BaseRevision!, comparison.Revision, "--", aName, bName])).StandardOutput;
        Check(patch == gitPatch && patch.Contains("rename from", StringComparison.Ordinal), "Metadata leaves literal selected rename output identical to Git.");
    }

    public static async Task CopyScoresAsync()
    {
        const string source = "copy-source.txt", aName = ":[copy-a]* λ\t\n.txt", bName = ":[copy-b]* λ\t\n.txt";
        using RepositoryFixture fixture = await CreateBranchesAsync(f => f.Write(source, Lines("copy")),
            f => { File.Copy(f.File(source), f.File(aName)); f.Write(source, EditedLines("copy", 1)); },
            f => { File.Copy(f.File(source), f.File(bName)); f.Write(source, EditedLines("copy", 1)); });
        GitRepository repository = fixture.Repository;
        IReadOnlyList<GitRevisionDiffGroup> exact = await repository.GetRevisionDiffGroupsAsync(["side-b", "side-a"]);
        foreach (GitRevisionDiffGroup group in new[] { Group(exact, GitRevisionDiffGroupKind.BaseToFirst), Group(exact, GitRevisionDiffGroupKind.BaseToSecond) })
        {
            Check(group.Files.Single(file => file.Status == 'C') is { SimilarityPercent: 100, OriginalPath: source, DiffStatus: DiffBranchStatus.SameChange }, "Copies from the same modified source retain C100 and equal content provenance.");
            Check(group.Files.Single(file => file.Path == source).DiffStatus == DiffBranchStatus.SameChange, "Identical source edits are independently equal.");
        }
        Check(Group(exact, GitRevisionDiffGroupKind.Comparison).Files.Single() is { Status: 'R', SimilarityPercent: 100, DiffStatus: DiffBranchStatus.SameChange }, "An exact move between copy destinations is excluded from unequal content changes.");
        fixture.Write(bName, EditedLines("copy", 20));
        await CommitAsync(repository, "Edit B copy");
        IReadOnlyList<GitRevisionDiffGroup> edited = await repository.GetRevisionDiffGroupsAsync(["side-b", "side-a"]);
        GitRevisionDiffGroup baseB = Group(edited, GitRevisionDiffGroupKind.BaseToSecond);
        GitRevisionFile copy = baseB.Files.Single(file => file.Path == bName);
        Check(copy is { Status: 'C', SimilarityPercent: < 100 and >= 50, DiffStatus: DiffBranchStatus.UnequalChange }, "Partial copies preserve their real score and unequal provenance.");
        GitRevisionChanges direct = await repository.GetRevisionChangesAsync("side-b", "base");
        Check(await repository.GetRevisionFileDiffAsync(direct, direct.Files.Single(file => file.Path == bName))
            == await repository.GetRevisionFileDiffAsync(new(baseB.Revision, baseB.BaseRevision, baseB.Files), copy), "Classification preserves copy patch output.");
        File.Copy(fixture.File(source), fixture.File("unchanged-source-copy.txt"));
        await CommitAsync(repository, "Copy an unchanged source");
        Check((await repository.GetRevisionChangesAsync("HEAD")).Files.Single() is { Status: 'A', SimilarityPercent: null }, "The historical --find-copies flag does not silently enable --find-copies-harder.");
    }

    public static async Task ExplicitBasesAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        GitRepository repository = fixture.Repository;
        fixture.Write("shared.txt", "root shared\n"); await CommitAsync(repository, "Root"); await repository.CreateBranchAsync("root", checkout: false);
        fixture.Write("shared.txt", "common shared\n"); await CommitAsync(repository, "Common"); await repository.CreateBranchAsync("base", checkout: false);
        await repository.RunAsync(["checkout", "-b", "side-a"]);
        fixture.Write("early-a.txt", "early A\n"); await CommitAsync(repository, "Early A"); await repository.CreateBranchAsync("base-a", checkout: false);
        fixture.Write("late-a.txt", "late A\n"); await CommitAsync(repository, "Late A");
        await repository.RunAsync(["checkout", "-b", "side-b", "base"]);
        fixture.Write("early-b.txt", "early B\n"); await CommitAsync(repository, "Early B"); await repository.CreateBranchAsync("base-b", checkout: false);
        fixture.Write("late-b.txt", "late B\n"); await CommitAsync(repository, "Late B");
        IReadOnlyList<GitRevisionDiffGroup> four = await repository.GetRevisionDiffGroupsAsync(["side-b", "base-b", "side-a", "base-a"]);
        GitRevisionDiffGroup a = Group(four, GitRevisionDiffGroupKind.BaseToFirst), b = Group(four, GitRevisionDiffGroupKind.BaseToSecond);
        string common = await repository.ResolveRevisionAsync("base");
        Check(a.BaseRevision == common && b.BaseRevision == common, "Four-selection classification still measures ordinary changes from the common BASE.");
        Check(a.Files.Single(file => file.Path == "early-a.txt").DiffStatus == DiffBranchStatus.OnlyAChange
            && b.Files.Single(file => file.Path == "early-b.txt").DiffStatus == DiffBranchStatus.OnlyBChange, "Changes before each explicit range bound remain in the BASE classification.");
        Check(Group(four, GitRevisionDiffGroupKind.RangeDiff).RangeDiff is { First.CommitCount: 1, Second.CommitCount: 1 }, "Explicit series bounds remain separate from classification BASE.");
        IReadOnlyList<GitRevisionDiffGroup> three = await repository.GetRevisionDiffGroupsAsync(["side-b", "root", "side-a"]);
        Check(Group(three, GitRevisionDiffGroupKind.BaseToFirst).Files.Single(file => file.Path == "shared.txt").DiffStatus == DiffBranchStatus.SameChange
            && Group(three, GitRevisionDiffGroupKind.BaseToSecond).Files.Single(file => file.Path == "shared.txt").DiffStatus == DiffBranchStatus.SameChange, "An older selected BASE includes the common intervening change as equal.");
    }

    public static async Task PairwiseNamesAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        GitRepository repository = fixture.Repository;
        fixture.Write("one.txt", Lines("chain")); await CommitAsync(repository, "Origin");
        List<GitRevisionFile> changes = [];
        string previous = "one.txt";
        foreach (string next in new[] { "two.txt", "three.txt", "four.txt" })
        {
            File.Move(fixture.File(previous), fixture.File(next)); await CommitAsync(repository, "Move to " + next);
            changes.Add((await repository.GetRevisionChangesAsync("HEAD")).Files.Single()); previous = next;
        }
        GitRevisionFileClassification classification = GitRevisionFileClassifier.Classify([changes[1]], [changes[0]], [changes[2]]);
        Check(classification.BaseToA.Single().DiffStatus == DiffBranchStatus.OnlyAChange
            && classification.BaseToB.Single().DiffStatus == DiffBranchStatus.OnlyBChange, "Disjoint first and third moves are not joined transitively through the middle move.");
        Check(classification.AToB.Single().DiffStatus == DiffBranchStatus.OnlyAChange, "Pairwise overlap with both sides preserves historical A-before-B precedence.");
        Check(changes.All(file => file.DiffStatus == DiffBranchStatus.Unknown), "The pure classifier returns copies instead of rewriting source records.");
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        try { GitRevisionFileClassifier.Classify(changes, changes, changes, canceled.Token); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }

    public static Task MalformedRecordsAsync()
    {
        foreach (string invalid in new[] { "M\0file", "R100\0old\0", "C101\0old\0new\0", "R-1\0old\0new\0", "R\0old\0new\0", "A1\0file\0", "?\0file\0", "M\0\0", "M\0file\0\0" })
        {
            try { GitRevisionFileParser.ParseNameStatus(invalid); throw new InvalidOperationException("Invalid NUL record was accepted."); }
            catch (FormatException) { }
        }
        GitRevisionFile rewrite = GitRevisionFileParser.ParseNameStatus("M100\0rewritten.txt\0").Single();
        Check(rewrite.Status == 'M' && rewrite.SimilarityPercent is null, "An M rewrite's dissimilarity is not mislabeled as rename/copy similarity.");
        Check(GitRevisionFileParser.ParseNameStatus("").Count == 0, "An unchanged diff has an empty file list.");
        return Task.CompletedTask;
    }

    public static async Task BareAndEligibilityAsync()
    {
        using RepositoryFixture fixture = await CreateProvenanceAsync();
        GitRepository repository = fixture.Repository;
        foreach (IReadOnlyList<GitRevisionDiffGroup> unclassified in new[]
        {
            await repository.GetRevisionDiffGroupsAsync(["side-b"]),
            await repository.GetRevisionDiffGroupsAsync(["side-b", "side-a"], showDiffForAllParents: false),
            await repository.GetRevisionDiffGroupsAsync(["side-b", "side-a", "side-a"]),
            await repository.GetRevisionDiffGroupsAsync(["side-b", "base", "side-b", "base", "side-a"])
        })
        { Check(unclassified.SelectMany(group => group.Files).All(file => file.DiffStatus == DiffBranchStatus.Unknown), "Parent and ineligible ordinary comparisons retain Unknown provenance."); }
        string barePath = Path.Combine(fixture.Root, "bare.git");
        await repository.RunAsync(["clone", "--bare", "--", repository.Path, barePath]);
        GitRepository bare = await GitRepository.OpenAsync(barePath);
        IReadOnlyList<GitRevisionDiffGroup> expected = await repository.GetRevisionDiffGroupsAsync(["side-b", "side-a"]);
        IReadOnlyList<GitRevisionDiffGroup> actual = await bare.GetRevisionDiffGroupsAsync(["side-b", "side-a"]);
        foreach (GitRevisionDiffGroup group in actual)
        {
            GitRevisionDiffGroup original = Group(expected, group.Kind);
            Check(group.Files.SequenceEqual(original.Files), "Bare classification retains every path, type, score and provenance.");
        }
        GitRevisionDiffGroup comparison = Group(actual, GitRevisionDiffGroupKind.Comparison);
        GitRevisionFile selected = comparison.Files.Single(file => file.Path == Different);
        Check(await bare.GetRevisionFileDiffAsync(new(comparison.Revision, comparison.BaseRevision, comparison.Files), selected)
            == await repository.GetRevisionFileDiffAsync(new(comparison.Revision, comparison.BaseRevision, comparison.Files), selected), "Bare and worktree selected patches remain identical.");
        Check((await bare.GetStatusAsync()).Changes.Count == 0, "Classification does not introduce bare worktree changes.");
    }

    public static async Task IndexFlagsAsync()
    {
        const string assume = ":[assume]* λ\t\n.txt", skip = ":[skip]* λ\t\n.txt", both = ":[both]* λ\t\n.txt", normal = " leading \"normal\" λ\t\n.txt", conflict = "conflict λ\t\n.txt";
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        GitRepository repository = fixture.Repository;
        foreach (string path in new[] { assume, skip, both, normal, conflict }) { fixture.Write(path, "initial " + path + "\n"); }
        await CommitAsync(repository, "Index flag files");
        await repository.RunAsync(["update-index", "--assume-unchanged", "--", assume, both]);
        await repository.RunAsync(["update-index", "--skip-worktree", "--", skip, both]);
        byte[] index = await File.ReadAllBytesAsync(fixture.File(".git/index"));
        IReadOnlyList<GitIndexFileFlags> flags = await repository.GetIndexFlagsAsync();
        Check(flags.Count == 5 && flags.Single(file => file.Path == assume) is { IsAssumeUnchanged: true, IsSkipWorktree: false }
            && flags.Single(file => file.Path == skip) is { IsAssumeUnchanged: false, IsSkipWorktree: true }
            && flags.Single(file => file.Path == both) is { IsAssumeUnchanged: true, IsSkipWorktree: true }
            && flags.Single(file => file.Path == normal) is { IsAssumeUnchanged: false, IsSkipWorktree: false }, "Real h, S, s and H index tags preserve literal names and their independent flags.");
        byte[] indexAfter = await File.ReadAllBytesAsync(fixture.File(".git/index"));
        Check(index.SequenceEqual(indexAfter), "Reading index flags does not rewrite the index.");
        await repository.RunAsync(["checkout", "-b", "feature"]); fixture.Write(conflict, "feature conflict\n"); await CommitAsync(repository, "Feature");
        await repository.RunAsync(["checkout", "main"]); fixture.Write(conflict, "main conflict\n"); await CommitAsync(repository, "Main");
        GitResult merge = await new GitRunner().RunAsync(repository.Path, ["merge", "feature"]);
        Check(!merge.Success, "A real merge produced the unmerged index stages.");
        string raw = (await repository.RunAsync(["ls-files", "-v", "-z", "--", conflict])).StandardOutput;
        Check(raw.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length == 3, "Git returned one flag record for each conflict stage.");
        Check((await repository.GetIndexFlagsAsync()).Count(file => file.Path == conflict) == 1, "Duplicate conflict paths become a single combined flag record.");
        await repository.RunAsync(["merge", "--abort"]);
        await repository.RunAsync(["update-index", "--no-assume-unchanged", "--", assume, both]);
        await repository.RunAsync(["update-index", "--no-skip-worktree", "--", skip, both]);
        Check((await repository.GetIndexFlagsAsync()).All(file => !file.IsAssumeUnchanged && !file.IsSkipWorktree), "Clearing flags is reflected by a fresh read.");
        string barePath = Path.Combine(fixture.Root, "flags-bare.git"); await repository.RunAsync(["clone", "--bare", "--", repository.Path, barePath]);
        GitRepository bare = await GitRepository.OpenAsync(barePath);
        try { await bare.GetIndexFlagsAsync(); throw new InvalidOperationException("Index flags unexpectedly succeeded."); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("bare", StringComparison.OrdinalIgnoreCase)) { }
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        try { await repository.GetIndexFlagsAsync(canceled.Token); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }

    private static Task<RepositoryFixture> CreateProvenanceAsync() => CreateBranchesAsync(f =>
    {
        foreach (string path in new[] { Same, Different, DeletedA, DeletedB, DeletedBoth, "case.txt", "Case.txt" }) { f.Write(path, "original " + path + "\n"); }
    }, f =>
    {
        f.Write(Same, "identical same change\n"); f.Write(Different, "different A content\n"); f.Write(AddedA, "unique added A\n"); f.Write(AddedBoth, "identical added content\n");
        File.Delete(f.File(DeletedA)); File.Delete(f.File(DeletedBoth)); f.Write("case.txt", "lowercase changed in A\n");
    }, f =>
    {
        f.Write(Same, "identical same change\n"); f.Write(Different, "different B content\n"); f.Write(AddedB, "unique added B\n"); f.Write(AddedBoth, "identical added content\n");
        File.Delete(f.File(DeletedB)); File.Delete(f.File(DeletedBoth)); f.Write("Case.txt", "uppercase changed in B\n");
    });

    private static async Task<RepositoryFixture> CreateBranchesAsync(Action<RepositoryFixture> baseline, Action<RepositoryFixture> changeA, Action<RepositoryFixture> changeB)
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false);
        try
        {
            GitRepository repository = fixture.Repository;
            baseline(fixture); await CommitAsync(repository, "Common base"); await repository.CreateBranchAsync("base", checkout: false);
            await repository.RunAsync(["checkout", "-b", "side-a"]); changeA(fixture); await CommitAsync(repository, "Branch A");
            await repository.RunAsync(["checkout", "-b", "side-b", "base"]); changeB(fixture); await CommitAsync(repository, "Branch B");
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static async Task CommitAsync(GitRepository repository, string message) { await repository.StageAllAsync(); await repository.CommitAsync(message); }
    private static GitRevisionDiffGroup Group(IReadOnlyList<GitRevisionDiffGroup> groups, GitRevisionDiffGroupKind kind) => groups.Single(group => group.Kind == kind);
    private static string Lines(string prefix) => string.Join('\n', Enumerable.Range(1, 40).Select(index => prefix + " common line " + index.ToString("D2"))) + "\n";
    private static string EditedLines(string prefix, int index)
    {
        string[] lines = Lines(prefix).Split('\n'); lines[index] = prefix + " changed line " + index.ToString("D2"); return string.Join('\n', lines);
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
