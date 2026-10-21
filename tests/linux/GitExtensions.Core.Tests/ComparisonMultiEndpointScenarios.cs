using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;

internal static class ComparisonMultiEndpointScenarios
{
    private const string Literal = ":[same]*? λ\t\n.txt";
    private const string Untracked = ":[new]* λ\n.txt";
    private static readonly GitCompareRevision Index = new("", GitCompareRevisionKind.Index), Working = new("", GitCompareRevisionKind.WorkingTree), Empty = new("", GitCompareRevisionKind.EmptyTree);
    private static readonly GitRevisionDiffGroupKind[] Advanced = [GitRevisionDiffGroupKind.Comparison, GitRevisionDiffGroupKind.RangeDiff,
        GitRevisionDiffGroupKind.BaseToSecond, GitRevisionDiffGroupKind.BaseToFirst];

    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("typed multi groups preserve the existing pair and every committed caller-order contract", PairAndRealCompatibilityAsync),
        ("typed three-way groups keep an earlier explicit BASE and actual Index worktree direction", ThreeMixedAsync),
        ("typed four-way groups preserve separate range bounds and opening HEAD after its ref moves", FourMixedFrozenHeadAsync),
        ("typed multi fallback never accepts an artificial BASE and obeys disabled and overfour native bounds", FallbackAndBoundsAsync),
        ("typed empty and unborn multi groups retain real bytes without inventing HEAD or single parent contracts", EmptyAndUnbornAsync),
        ("typed multi validation precedes IO and cancellation drains exact native identities without mutation locking", ValidationAndCancellationAsync)
    ];

    private static async Task PairAndRealCompatibilityAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = Material(f.Repository.Path);
        foreach (GitCompareRevision other in new[] { new GitCompareRevision(f.B), Index, Working, Empty })
        foreach (bool reverse in new[] { false, true })
        foreach (bool all in new[] { false, true })
        {
            GitCompareRevision from = reverse ? other : new(f.A), to = reverse ? new(f.A) : other;
            IReadOnlyList<GitComparisonGroup> pair = await f.Repository.GetComparisonGroupsAsync(from, to, f.B, all);
            IReadOnlyList<GitComparisonGroup> list = await f.Repository.GetComparisonGroupsAsync([to, from], f.B, all);
            Check(Signature(list) == Signature(pair), "The list overload changed the established pair byte contract or direction.");
        }
        foreach (string[] order in new[] { new[] { f.B, f.Root, f.A }, new[] { f.B, f.BaseB, f.A, f.BaseA }, new[] { f.B, f.BaseB, f.Common, f.BaseA, f.A } })
        foreach (bool all in new[] { false, true })
        {
            IReadOnlyList<GitRevisionDiffGroup> native = await f.Repository.GetRevisionDiffGroupsAsync(order, all);
            IReadOnlyList<GitComparisonGroup> projected = native.Select(group => new GitComparisonGroup(group.Kind,
                new(new(group.BaseRevision!), new(group.Revision), group.Files, []), group.RangeDiff)).ToArray();
            IReadOnlyList<GitComparisonGroup> actual = await f.Repository.GetComparisonGroupsAsync(order.Select(hash => new GitCompareRevision(hash)).ToArray(), null, all);
            Check(Signature(actual) == Signature(projected), "All-real selection no longer delegates the exact existing native grouping contract.");
        }
        Check(Material(f.Repository.Path) == before, "Compatibility reads changed repository materials.");
    }

    private static async Task ThreeMixedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = Material(f.Repository.Path);
        Check(f.Root != f.Common && (await Native(f.Repository, "merge-base", f.A, f.B)).StandardOutput.Trim() == f.Common,
            "The explicit middle BASE must be genuinely earlier than the actual common ancestor.");
        foreach (GitCompareRevision endpoint in new[] { Index, Working })
        foreach (bool reverse in new[] { false, true })
        {
            GitCompareRevision first = reverse ? endpoint : new(f.A), second = reverse ? new(f.A) : endpoint;
            IReadOnlyList<GitComparisonGroup> groups = await f.Repository.GetComparisonGroupsAsync([second, new(f.Root), first], f.B);
            Check(groups.Select(group => group.Kind).SequenceEqual(Advanced), "A valid explicit earlier BASE must retain all original group kinds and order.");
            Check(groups[0].Comparison.Base == first && groups[0].Comparison.Compare == second, "The ordinary group lost the actual typed endpoints or direction.");
            GitRangeDiffComparison range = groups[1].RangeDiff!;
            Check(range.SelectionCount == 3 && range.CommonAncestor == f.Root && range.First.BaseRevision == f.Root && range.Second.BaseRevision == f.Root
                && range.First.TipRevision == (reverse ? f.B : f.A) && range.Second.TipRevision == (reverse ? f.A : f.B),
                "Only real range tips may substitute captured HEAD; the explicit middle BASE must survive.");
            Check(groups[1].Comparison.Base == first && groups[1].Comparison.Compare == second && groups[1].Comparison.Files.Count == 0,
                "The range header must retain typed context without invented file leaves.");
            Check(groups[2].Comparison.Base == new GitCompareRevision(f.Root) && groups[2].Comparison.Compare == second
                && groups[3].Comparison.Base == new GitCompareRevision(f.Root) && groups[3].Comparison.Compare == first,
                "The BASE-to-B/A file readers accidentally used the merge-base or captured commit snapshots.");
            Check(groups[0].Comparison.Files.Single(file => file.Path == "only-a.txt").DiffStatus == (reverse ? DiffBranchStatus.OnlyBChange : DiffBranchStatus.OnlyAChange)
                && groups[0].Comparison.Files.Single(file => file.Path == "only-b.txt").DiffStatus == (reverse ? DiffBranchStatus.OnlyAChange : DiffBranchStatus.OnlyBChange),
                "Change provenance must follow the original A/B sides rather than addition/deletion direction.");
            await AssertNativeGroups(f.Repository, groups); await AssertNativeRange(f.Repository, range);
            if (endpoint.Kind == GitCompareRevisionKind.WorkingTree)
            {
                GitRevisionFile untracked = groups[0].Comparison.Files.Single(file => file.Path == Untracked);
                string patch = await f.Repository.GetComparisonFileDiffAsync(groups[0].Comparison, untracked);
                Check(patch.Contains((reverse ? "-" : "+") + "untracked exact λ", StringComparison.Ordinal), "The grouped untracked diff must read actual worktree bytes in the selected direction.");
            }
        }
        Check(Material(f.Repository.Path) == before, "Three-way group reads changed staged, working, reference or object bytes.");
    }

    private static async Task FourMixedFrozenHeadAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Native(f.Repository, "update-ref", "refs/heads/side-b", f.A);
        Check(await f.Repository.ResolveRevisionAsync("HEAD") == f.A && f.A != f.B, "The real opening HEAD must now differ from live HEAD without rewriting staged/worktree bytes.");
        string before = Material(f.Repository.Path);
        foreach (GitCompareRevision endpoint in new[] { Index, Working })
        {
            IReadOnlyList<GitComparisonGroup> groups = await f.Repository.GetComparisonGroupsAsync([endpoint, new(f.BaseB), new(f.A), new(f.BaseA)], f.B);
            Check(groups.Select(group => group.Kind).SequenceEqual(Advanced), "Two valid explicit ranges with one artificial tip must retain the advanced groups.");
            GitRangeDiffComparison range = groups[1].RangeDiff!;
            Check(range.SelectionCount == 4 && range.First.BaseRevision == f.BaseA && range.First.TipRevision == f.A
                && range.Second.BaseRevision == f.BaseB && range.Second.TipRevision == f.B && range.CommonAncestor == f.Common,
                "The four-way range lost a bound, substituted live HEAD, or replaced its common BASE by one explicit series bound.");
            Check(groups[0].Comparison.Base == new GitCompareRevision(f.A) && groups[0].Comparison.Compare == endpoint
                && groups[2].Comparison.Base == new GitCompareRevision(f.Common) && groups[2].Comparison.Compare == endpoint
                && groups[3].Comparison.Base == new GitCompareRevision(f.Common) && groups[3].Comparison.Compare == new GitCompareRevision(f.A),
                "The classified file groups must use actual A/B endpoints and common BASE while only the range uses separate bounds.");
            await AssertNativeGroups(f.Repository, groups); await AssertNativeRange(f.Repository, range);
            Check(range.First.CommitCount == int.Parse((await Native(f.Repository, "rev-list", "--count", f.BaseA + ".." + f.A, "--")).StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture)
                && range.Second.CommitCount == int.Parse((await Native(f.Repository, "rev-list", "--count", f.BaseB + ".." + f.B, "--")).StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture),
                "Range counts must come from the two explicit native series.");
        }
        Check(Material(f.Repository.Path) == before, "Four-way group reads changed the moved live HEAD, original capture, index, worktree or object bytes.");
    }

    private static async Task FallbackAndBoundsAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = Material(f.Repository.Path);
        GitCompareRevision[][] ineligible =
        [
            [new(f.B), Index, new(f.A)],
            [new(f.B), Index, new(f.A), new(f.Root)],
            [Working, new(f.BaseB), new(f.A), Index],
            [Working, Empty, new(f.A)]
        ];
        foreach (GitCompareRevision[] order in ineligible)
        {
            // Using Common as the capture makes an incorrectly substituted artificial BASE
            // look like a real ancestor of both tips. Windows explicitly forbids that base.
            IReadOnlyList<GitComparisonGroup> groups = await f.Repository.GetComparisonGroupsAsync(order, f.Common);
            Check(groups.All(group => group.Kind == GitRevisionDiffGroupKind.Comparison && group.RangeDiff is null)
                && groups.Count == order.Length - 1, "An artificial/empty explicit BASE was incorrectly promoted to captured HEAD for a range.");
            GitCompareRevision first = order[order.Length == 4 ? 2 : order.Length - 1];
            Check(groups[0].Comparison.Base.Kind == first.Kind && groups[0].Comparison.Compare.Kind == order[0].Kind,
                "The fallback ordinary pair changed its caller-selected first or selected endpoint.");
            await AssertNativeGroups(f.Repository, groups);
            GitCompareRevision[] expected = [first, .. order.Skip(1).Where(item => item != first && item != order[0])];
            Check(groups.Select(group => (group.Comparison.Base.Kind, group.Comparison.Base.Kind == GitCompareRevisionKind.Commit ? group.Comparison.Base.Revision : ""))
                .SequenceEqual(expected.Select(item => (item.Kind, item.Kind == GitCompareRevisionKind.Commit ? item.Revision : ""))),
                "Non-range additional groups must retain caller order and every actual endpoint.");
        }
        GitCompareRevision[] four = [Working, new("missing-unused-middle"), new(f.A), new(f.BaseA)];
        IReadOnlyList<GitComparisonGroup> disabled = await f.Repository.GetComparisonGroupsAsync(four, f.B, showDiffForAllParents: false);
        Check(disabled.Count == 1 && disabled[0].Comparison.Base == new GitCompareRevision(f.BaseA) && disabled[0].Comparison.Compare == Working,
            "With all-parents disabled the primary first is the last selected endpoint, not index2; unused middle values are not native inputs.");
        GitCompareRevision[] many = [Working, new("--unused-middle"), Empty, Index, new(f.A)];
        IReadOnlyList<GitComparisonGroup> overFour = await f.Repository.GetComparisonGroupsAsync(many, f.B);
        Check(overFour.Count == 1 && overFour[0].Comparison.Base == new GitCompareRevision(f.A) && overFour[0].Comparison.Compare == Working,
            "More than four selections must keep only last-to-selected, with no range or unused middle resolution.");
        await AssertNativeGroups(f.Repository, disabled); await AssertNativeGroups(f.Repository, overFour);
        Check(disabled.Concat(overFour).SelectMany(group => group.Comparison.Files).All(file => file.DiffStatus == DiffBranchStatus.Unknown),
            "Disabled/overfour ordinary comparisons must not invent multi-branch classification.");
        Check(Material(f.Repository.Path) == before, "Fallback or disabled/bounded group reads changed repository bytes.");
    }

    private static async Task EmptyAndUnbornAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
        f.Write("staged.txt", "initial staged\n"); await f.Repository.StageAllAsync(); f.Write(Untracked, "untracked exact λ\n");
        string before = Material(f.Repository.Path);
        Check((await f.Repository.GetComparisonGroupsAsync([], null)).Count == 0, "An empty selection must not invent a current commit or group.");
        await Expect<ArgumentException>(() => f.Repository.GetComparisonGroupsAsync([Index], null));
        GitComparison single = await f.Repository.GetArtificialRevisionComparisonAsync(Index);
        Check(single.Base.Kind == GitCompareRevisionKind.EmptyTree && single.Compare == Index && single.Files.Single().Path == "staged.txt",
            "The original single/unborn parent contract must remain in its existing typed reader.");
        IReadOnlyList<GitComparisonGroup> groups = await f.Repository.GetComparisonGroupsAsync([Working, Empty, Index], null);
        Check(groups.Count == 2 && groups.All(group => group.Kind == GitRevisionDiffGroupKind.Comparison && group.RangeDiff is null)
            && groups[0].Comparison.Base == Index && groups[0].Comparison.Compare == Working
            && groups[1].Comparison.Base.Kind == GitCompareRevisionKind.EmptyTree && groups[1].Comparison.Compare == Working,
            "Unborn multi-diff must preserve Index/EmptyTree-to-worktree endpoints without manufacturing HEAD.");
        await AssertNativeGroups(f.Repository, groups); Check(Material(f.Repository.Path) == before, "Unborn/empty group reads wrote an empty-tree object, index or working content.");
        await f.Repository.CommitAsync("First real commit"); string head = await f.Repository.ResolveRevisionAsync("HEAD"); before = Material(f.Repository.Path);
        groups = await f.Repository.GetComparisonGroupsAsync([Working, new(head), Index], capturedHead: null);
        Check(groups.Count == 2 && groups.All(group => group.Kind == GitRevisionDiffGroupKind.Comparison && group.RangeDiff is null),
            "A null opening capture must remain absent after the repository gains a real HEAD.");
        await AssertNativeGroups(f.Repository, groups); Check(Material(f.Repository.Path) == before, "Reading a retained unborn capture changed the later real repository.");
    }

    private static async Task ValidationAndCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string before = Material(f.Repository.Path);
        string marker = Path.Combine(f.Owner.Root, "must-not-invoke"), executable = Path.Combine(f.Owner.Root, "validation git");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf invoked > " + Quote(marker) + "\nexit 91\n"); Mode(executable);
        GitRepository guarded = new(f.Repository.Path, new GitRunner(executable));
        foreach (string capture in new[] { "HEAD", "--all", "abcd", new string('z', 40) })
        { await Expect<ArgumentException>(() => guarded.GetComparisonGroupsAsync([Working, new(f.Root), new(f.A)], capture)); }
        await Expect<ArgumentException>(() => guarded.GetComparisonGroupsAsync([Working], f.B));
        await Expect<ArgumentOutOfRangeException>(() => guarded.GetComparisonGroupsAsync([Working, new("", (GitCompareRevisionKind)99), new(f.A)], f.B));
        using (CancellationTokenSource cancelled = new())
        { cancelled.Cancel(); OperationCanceledException error = await Expect<OperationCanceledException>(() => guarded.GetComparisonGroupsAsync([Working, new(f.Root), new(f.A)], f.B, cancellationToken: cancelled.Token)); Check(error.CancellationToken == cancelled.Token, "Precancellation lost its caller token."); }
        Check(!File.Exists(marker), "Invalid list/capture and precancellation must precede native execution.");
        foreach (string command in new[] { "merge-base", "rev-list" })
        {
            string pid = Path.Combine(f.Owner.Root, command + "-pid"), release = Path.Combine(f.Owner.Root, command + "-release");
            string wrapper = Path.Combine(f.Owner.Root, command + "-git");
            await File.WriteAllTextAsync(wrapper, "#!/usr/bin/python3\nimport os,pathlib,sys,time\n"
                + "if " + Python(command) + " in sys.argv[1:]:\n"
                + "    p=pathlib.Path(" + Python(pid) + "); t=p.with_suffix('.tmp'); t.write_text(str(os.getpid())); os.replace(t,p)\n"
                + "    while not pathlib.Path(" + Python(release) + ").exists(): time.sleep(.02)\n"
                + "os.execv('/usr/bin/git',['/usr/bin/git',*sys.argv[1:]])\n"); Mode(wrapper);
            GitRepository repository = new(f.Repository.Path, new GitRunner(wrapper)); using CancellationTokenSource token = new();
            Task<IReadOnlyList<GitComparisonGroup>> work = repository.GetComparisonGroupsAsync([Working, new(f.Root), new(f.A)], f.B, cancellationToken: token.Token);
            try
            {
                (int Pid, string Start)? observed = null;
                await Until(() => { if (File.Exists(pid) && int.TryParse(File.ReadAllText(pid), out int value) && Start(value) is string start) { observed = (value, start); return true; } return false; });
                (int Pid, string Start) identity = observed ?? throw new InvalidOperationException("A native multi-group reader was not observed.");
                Check(!work.IsCompleted && Alive(identity), "The actual group reader must remain pending before cancellation.");
                GitResult independent = await repository.RunAsync(["--version"]).WaitAsync(TimeSpan.FromSeconds(5));
                Check(independent.Success && !work.IsCompleted, "A read-only group workflow must not retain the repository mutation semaphore.");
                token.Cancel(); OperationCanceledException error = await Expect<OperationCanceledException>(() => work);
                Check(error.CancellationToken == token.Token && work.IsCanceled && !Alive(identity), "Cancellation returned before exact native PID/start retirement or published a partial group list.");
                await File.WriteAllTextAsync(release, "release\n"); Check(!Alive(identity), "A drained group read cannot resume after release.");
            }
            finally { token.Cancel(); await File.WriteAllTextAsync(release, "release\n"); try { await work.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { } }
        }
        Check(Material(f.Repository.Path) == before, "Validation and canceled read phases changed index, working content, refs or objects.");
    }

    private static async Task AssertNativeGroups(GitRepository repository, IReadOnlyList<GitComparisonGroup> groups)
    {
        foreach (GitComparisonGroup group in groups.Where(group => group.Kind != GitRevisionDiffGroupKind.RangeDiff))
        {
            GitComparison pair = group.Comparison;
            List<string> args = ["diff", "--name-status", "-z", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv"];
            bool a = pair.Base.Kind is GitCompareRevisionKind.Commit or GitCompareRevisionKind.EmptyTree, b = pair.Compare.Kind is GitCompareRevisionKind.Commit or GitCompareRevisionKind.EmptyTree;
            if (a && b) { args.AddRange([pair.Base.Revision, pair.Compare.Revision]); }
            else if (a) { if (pair.Compare.Kind == GitCompareRevisionKind.Index) { args.Add("--cached"); } args.Add(pair.Base.Revision); }
            else if (b) { args.Add("-R"); if (pair.Base.Kind == GitCompareRevisionKind.Index) { args.Add("--cached"); } args.Add(pair.Compare.Revision); }
            else if (pair.Base.Kind == GitCompareRevisionKind.WorkingTree) { args.Add("-R"); }
            args.Add("--");
            List<GitRevisionFile> native = [.. GitRevisionFileParser.ParseNameStatus((await Native(repository, args.ToArray())).StandardOutput)];
            List<string> untracked = [];
            if (pair.Base.Kind == GitCompareRevisionKind.WorkingTree || pair.Compare.Kind == GitCompareRevisionKind.WorkingTree)
            {
                foreach (string path in (await Native(repository, "ls-files", "--others", "--exclude-standard", "-z")).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                { if (!native.Any(file => file.Path == path || file.OriginalPath == path)) { native.Add(new(path, pair.Compare.Kind == GitCompareRevisionKind.WorkingTree ? 'A' : 'D')); untracked.Add(path); } }
            }
            Check(FileSignature(pair.Files) == FileSignature(native) && pair.UntrackedPaths.SequenceEqual(untracked),
                "A typed group's file names/statuses/direction or untracked provenance disagrees with independent native Git: " + group.Kind);
        }
    }

    private static async Task AssertNativeRange(GitRepository repository, GitRangeDiffComparison range)
    {
        string native = (await Native(repository, "range-diff", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3",
            range.First.BaseRevision + ".." + range.First.TipRevision, range.Second.BaseRevision + ".." + range.Second.TipRevision)).StandardOutput;
        Check(await repository.GetRangeDiffAsync(range) == native, "The retained real series must execute the exact native range-diff rather than a worktree delta or lost bound.");
    }
    private static string Signature(IReadOnlyList<GitComparisonGroup> groups) => JsonSerializer.Serialize(groups);
    private static string FileSignature(IEnumerable<GitRevisionFile> files) => JsonSerializer.Serialize(files.Select(file => (file.Path, file.Status, file.OriginalPath, file.SimilarityPercent)).ToArray(), new JsonSerializerOptions { IncludeFields = true });
    private static string Material(string repository) => string.Join('\n', Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(repository, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(TimeSpan.FromSeconds(15)); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<GitResult> Native(GitRepository repository, params string[] args)
    { GitResult result = await new GitRunner("/usr/bin/git").RunAsync(repository.Path, ["--no-optional-locks", "--literal-pathspecs", .. args]); if (!result.Success) { throw new GitCommandException(args, result); } return result; }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(10); } Check(condition(), "The native multi-group gate did not reach its barrier."); }
    private static string? Start(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; } catch (IOException) { return null; } }
    private static bool Alive((int Pid, string Start) identity) => Start(identity.Pid) == identity.Start;
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Python(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    private static void Mode(string path)
    { if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private executable gates require Linux."); } File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

    private sealed record Fixture(RepositoryFixture Owner, string Root, string Common, string A, string B, string BaseA, string BaseB) : IDisposable
    {
        internal GitRepository Repository => Owner.Repository;
        internal static async Task<Fixture> CreateAsync()
        {
            RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
            try
            {
                f.Write(Literal, "root content\n"); f.Write("different.txt", "root different\n"); await Commit(f, "Root"); string root = await f.Repository.ResolveRevisionAsync("HEAD");
                f.Write("shared.txt", "shared common\n"); await Commit(f, "Shared common"); string common = await f.Repository.ResolveRevisionAsync("HEAD");
                await Native(f.Repository, "checkout", "-b", "side-a"); f.Write(Literal, "same branch change\n"); f.Write("different.txt", "A content\n"); f.Write("only-a.txt", "only A\n");
                await Commit(f, "A bound"); string baseA = await f.Repository.ResolveRevisionAsync("HEAD"); f.Write("tip-a.txt", "A tip\n"); await Commit(f, "A tip"); string a = await f.Repository.ResolveRevisionAsync("HEAD");
                await Native(f.Repository, "checkout", "-b", "side-b", common); f.Write(Literal, "same branch change\n"); f.Write("different.txt", "B content\n"); f.Write("only-b.txt", "only B\n");
                await Commit(f, "B bound"); string baseB = await f.Repository.ResolveRevisionAsync("HEAD"); f.Write("tip-b.txt", "B tip\n"); await Commit(f, "B tip"); string b = await f.Repository.ResolveRevisionAsync("HEAD");
                f.Write("staged.txt", "actual Index\n"); await f.Repository.StageAsync(["staged.txt"]); f.Write("different.txt", "actual WorkingTree\n"); f.Write(Untracked, "untracked exact λ\n");
                return new(f, root, common, a, b, baseA, baseB);
            }
            catch { f.Dispose(); throw; }
        }
        private static async Task Commit(RepositoryFixture f, string message) { await f.Repository.StageAllAsync(); await f.Repository.CommitAsync(message); }
        public void Dispose() => Owner.Dispose();
    }
}
