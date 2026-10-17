using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;

internal static class ComparisonGroupScenarios
{
    private const string Literal = ":[same]*? λ\t\n.txt";
    private const string Dirty = "different.txt";
    private const string Untracked = ":[untracked]* λ\t\n.txt";
    private const string Staged = "staged λ[1].txt";
    private static readonly GitRevisionDiffGroupKind[] Advanced = [GitRevisionDiffGroupKind.Comparison,
        GitRevisionDiffGroupKind.RangeDiff, GitRevisionDiffGroupKind.BaseToSecond, GitRevisionDiffGroupKind.BaseToFirst];

    public static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("typed comparison groups reuse real divergent classifications and native range series", DivergentAsync),
        ("typed comparison groups keep linear equal orphan and disabled selections ordinary", IneligibleAsync),
        ("typed Index and worktree groups retain direction untracked bytes and classifications", ArtificialAsync),
        ("typed groups keep captured real HEAD after checkout and branch movement", CapturedHeadAsync),
        ("an unborn captured HEAD stays absent after the repository obtains real commits", UnbornCaptureAsync),
        ("typed comparison group validation rejects invalid captures before native reads", ValidationAsync),
        ("typed groups honor Git range availability and literal range path capability", VersionAndPathsAsync),
        ("typed groups read bare and linked repositories without changing either checkout", BareAndLinkedAsync),
        ("typed comparison group cancellation drains native descendants without a mutation lock", CancellationAsync)
    ];

    private static async Task DivergentAsync()
    {
        using RepositoryFixture f = await CreateSeriesAsync(); GitRepository repository = f.Repository;
        string a = await repository.ResolveRevisionAsync("side-a"), b = await repository.ResolveRevisionAsync("side-b");
        string before = await SnapshotAsync(repository);
        IReadOnlyList<GitComparisonGroup> groups = await repository.GetComparisonGroupsAsync(new(a), new(b), b);
        IReadOnlyList<GitRevisionDiffGroup> original = await repository.GetRevisionDiffGroupsAsync([b, a]);
        Check(groups.Select(group => group.Kind).SequenceEqual(Advanced), "Original group order is retained.");
        for (int index = 0; index < groups.Count; index++)
        {
            GitComparison comparison = groups[index].Comparison; GitRevisionDiffGroup source = original[index];
            Check(comparison.Base == new GitCompareRevision(source.BaseRevision!) && comparison.Compare == new GitCompareRevision(source.Revision), "Each committed group keeps its own actual endpoints.");
            Check(comparison.Files.SequenceEqual(source.Files) && groups[index].RangeDiff == source.RangeDiff, "Shared classification/range results are preserved without reconstruction.");
        }
        GitComparison aToB = Group(groups, GitRevisionDiffGroupKind.Comparison).Comparison;
        Check(aToB.Files.Single(file => file.Path == "only-a.txt").DiffStatus == DiffBranchStatus.OnlyAChange
            && aToB.Files.Single(file => file.Path == "only-b.txt").DiffStatus == DiffBranchStatus.OnlyBChange, "A/B provenance is not inferred from addition/removal direction.");
        Check(Group(groups, GitRevisionDiffGroupKind.BaseToFirst).Comparison.Files.Single(file => file.Path == Literal).DiffStatus == DiffBranchStatus.SameChange, "Identical changes from BASE remain shared.");
        string range = await repository.GetRangeDiffAsync(Group(groups, GitRevisionDiffGroupKind.RangeDiff).RangeDiff!);
        string native = await NativeAsync(repository, "range-diff", "--no-color", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", "--unified=3", "base..side-a", "base..side-b");
        Check(range == native, "The group's real series produces the native range-diff, not the worktree patch.");
        Check(await SnapshotAsync(repository) == before, "Reading all committed groups and their range does not change HEAD, refs, index or worktree.");
    }

    private static async Task IneligibleAsync()
    {
        using RepositoryFixture f = await CreateSeriesAsync(); GitRepository repository = f.Repository;
        string a = await repository.ResolveRevisionAsync("side-a"), b = await repository.ResolveRevisionAsync("side-b"), common = await repository.ResolveRevisionAsync("base");
        foreach ((string from, string to) in new[] { (a, a), (common, a), (a, common), (common, common) })
        {
            IReadOnlyList<GitComparisonGroup> groups = await repository.GetComparisonGroupsAsync(new(from), new(to), null);
            Check(groups.Count == 1 && groups[0].Kind == GitRevisionDiffGroupKind.Comparison, "Equal, root and ancestor endpoints must not invent common-BASE groups.");
            if (from == to) { Check(groups[0].Comparison.Files.Count == 0, "Equal committed snapshots have no delta."); }
        }
        IReadOnlyList<GitComparisonGroup> disabled = await repository.GetComparisonGroupsAsync(new(a), new(b), b, showDiffForAllParents: false);
        Check(disabled.Count == 1 && disabled[0].Comparison.Files.All(file => file.DiffStatus == DiffBranchStatus.Unknown), "Disabled all-parents retains the unclassified ordinary pair.");
        await repository.RunAsync(["checkout", "--orphan", "unrelated"]); await repository.RunAsync(["rm", "-rf", "--", "."]);
        f.Write("orphan.txt", "unrelated\n"); await CommitAsync(repository, "Unrelated root"); string orphan = await repository.ResolveRevisionAsync("HEAD");
        string before = await SnapshotAsync(repository);
        IReadOnlyList<GitComparisonGroup> noCommon = await repository.GetComparisonGroupsAsync(new(a), new(orphan), orphan);
        Check(noCommon.Count == 1 && noCommon[0].Comparison.Files.Count > 0, "No common ancestor still yields a useful ordinary comparison.");
        Check(await SnapshotAsync(repository) == before, "Ineligible group reads do not mutate the repository.");
    }

    private static async Task ArtificialAsync()
    {
        using RepositoryFixture f = await CreateSeriesAsync(); GitRepository repository = f.Repository;
        string a = await repository.ResolveRevisionAsync("side-a"), captured = await repository.ResolveRevisionAsync("HEAD");
        f.Write(Staged, "staged content\n"); await repository.StageAsync([Staged]);
        f.Write(Dirty, "worktree content\n"); f.Write(Untracked, "untracked exact λ\n");
        string before = await SnapshotAsync(repository);
        foreach (GitCompareRevisionKind kind in new[] { GitCompareRevisionKind.Index, GitCompareRevisionKind.WorkingTree })
        {
            GitCompareRevision artificial = new("not-a-commit-or-argument", kind);
            foreach (bool reverse in new[] { false, true })
            {
                GitCompareRevision from = reverse ? artificial : new(a), to = reverse ? new(a) : artificial;
                GitComparison direct = await repository.GetComparisonAsync(from, to);
                IReadOnlyList<GitComparisonGroup> groups = await repository.GetComparisonGroupsAsync(from, to, captured);
                Check(groups.Select(group => group.Kind).SequenceEqual(Advanced), "Artificial endpoints on a divergent captured branch have the full four groups.");
                GitComparison primary = groups[0].Comparison;
                Check(primary.Base == direct.Base && primary.Compare == direct.Compare && SameFiles(primary.Files, direct.Files)
                    && primary.UntrackedPaths.SequenceEqual(direct.UntrackedPaths), "Classification preserves typed endpoint direction, literal names and untracked provenance.");
                GitComparison side = Group(groups, reverse ? GitRevisionDiffGroupKind.BaseToFirst : GitRevisionDiffGroupKind.BaseToSecond).Comparison;
                Check(side.Base.Kind == GitCompareRevisionKind.Commit && side.Compare.Kind == kind
                    && side.Files.Any(file => file.Path == Staged), "The artificial BASE side is not accidentally read from captured HEAD's tree.");
                Check(primary.Files.All(file => file.DiffStatus != DiffBranchStatus.Unknown), "Artificial file lists use the common pairwise classifier too.");
                GitComparisonGroup range = Group(groups, GitRevisionDiffGroupKind.RangeDiff);
                Check(range.Comparison.Base == primary.Base && range.Comparison.Compare == primary.Compare && range.Comparison.Files.Count == 0,
                    "The range row keeps actual artificial context but has no fake file leaf.");
                Check(range.RangeDiff!.First.TipRevision == (reverse ? captured : a) && range.RangeDiff.Second.TipRevision == (reverse ? a : captured), "Only the committed series substitutes the frozen HEAD.");
                if (kind == GitCompareRevisionKind.WorkingTree)
                {
                    GitRevisionFile file = primary.Files.Single(file => file.Path == Untracked);
                    Check(file.Status == (reverse ? 'D' : 'A') && primary.UntrackedPaths.Contains(Untracked), "An untracked literal path remains new/deleted according to presentation direction.");
                    string patch = await repository.GetComparisonFileDiffAsync(primary, file);
                    Check(patch.Contains((reverse ? "-" : "+") + "untracked exact λ", StringComparison.Ordinal), "The grouped untracked preview reads actual bytes in both directions.");
                }
                else { Check(!side.Files.Any(file => file.Path == Untracked), "Index groups never include untracked worktree files."); }
            }
        }
        Check(await SnapshotAsync(repository) == before, "All artificial reads preserve the exact staged and dirty checkout.");
    }

    private static async Task CapturedHeadAsync()
    {
        using RepositoryFixture f = await CreateSeriesAsync(); GitRepository repository = f.Repository;
        string a = await repository.ResolveRevisionAsync("side-a"), captured = await repository.ResolveRevisionAsync("side-b");
        await repository.CheckoutAsync("side-a"); await repository.RunAsync(["update-ref", "refs/heads/side-b", a]);
        string before = await SnapshotAsync(repository);
        GitCompareRevision worktree = new("", GitCompareRevisionKind.WorkingTree);
        IReadOnlyList<GitComparisonGroup> frozen = await repository.GetComparisonGroupsAsync(new(a), worktree, captured);
        Check(frozen.Count == 4 && frozen[0].Comparison.Files.Count == 0, "Advanced eligibility uses captured B even though the actual worktree now equals A.");
        GitRangeDiffComparison range = Group(frozen, GitRevisionDiffGroupKind.RangeDiff).RangeDiff!;
        Check(range.Second.TipRevision == captured && range.First.TipRevision == a, "Neither live HEAD nor a moved branch replaces the snapshot tip.");
        string native = await NativeAsync(repository, "range-diff", "--no-color", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", "--unified=3", range.First.BaseRevision + ".." + a, range.Second.BaseRevision + ".." + captured);
        Check(await repository.GetRangeDiffAsync(range) == native, "Displaying the range after checkout movement keeps the captured real series.");
        Check((await repository.GetComparisonGroupsAsync(new(a), worktree, a)).Count == 1, "Using the new capture instead produces a genuinely linear selection.");
        Check(await SnapshotAsync(repository) == before, "Frozen group eligibility does not restore or modify HEAD/index/worktree.");
    }

    private static async Task UnbornCaptureAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false); GitRepository repository = f.Repository;
        string? captured = (await repository.GetStatusAsync()).Head; Check(captured is null, "The opening capture is unborn.");
        f.Write(Staged, "initial staged\n"); await repository.StageAsync([Staged]); f.Write(Untracked, "initial untracked\n");
        GitCompareRevision index = new("", GitCompareRevisionKind.Index), worktree = new("", GitCompareRevisionKind.WorkingTree);
        IReadOnlyList<GitComparisonGroup> initial = await repository.GetComparisonGroupsAsync(index, worktree, captured);
        Check(initial.Count == 1 && initial[0].Comparison.Files.Single().Path == Untracked, "An unborn Index-to-worktree comparison still exposes its real untracked file.");
        foreach (GitCompareRevision same in new[] { index, worktree })
        { Check((await repository.GetComparisonGroupsAsync(same, same, null)).Single().Comparison.Files.Count == 0, "Equal artificial endpoints retain the Linux empty result, not the inherited Windows HEAD-diff bug."); }
        await repository.RunAsync(["clean", "-fd"]); await repository.RunAsync(["rm", "--cached", "--", Staged]); File.Delete(f.File(Staged));
        await CreateSeriesAsync(f);
        string a = await repository.ResolveRevisionAsync("side-a"), actualHead = await repository.ResolveRevisionAsync("HEAD");
        Check((await repository.GetComparisonGroupsAsync(new(a), worktree, captured)).Count == 1, "A null captured HEAD remains null after the first real commits; there is no implicit checkout re-read.");
        Check((await repository.GetComparisonGroupsAsync(new(a), worktree, actualHead)).Count == 4, "A new opening capture sees the divergent history normally.");
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await CreateSeriesAsync(); GitRepository repository = f.Repository;
        string a = await repository.ResolveRevisionAsync("side-a"), b = await repository.ResolveRevisionAsync("HEAD");
        string marker = Path.Combine(f.Root, "unexpected-invocation"), wrapper = Path.Combine(f.Root, "must-not-run");
        await ExecutableAsync(wrapper, "#!/bin/sh\nprintf invoked > '" + marker + "'\nexit 91\n");
        GitRepository guarded = new(repository.Path, new GitRunner(wrapper));
        foreach (string value in new[] { "HEAD", "--option", "abcd", new string('z', 40) })
        { await ExpectAsync<ArgumentException>(() => guarded.GetComparisonGroupsAsync(new(a), new(b), value)); }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => guarded.GetComparisonGroupsAsync(new(a), new(b), b, cancellationToken: cancellation.Token));
        Check(!File.Exists(marker), "Invalid captures and pre-cancelled reads never start a native process.");
        await ExpectAsync<ArgumentOutOfRangeException>(() => repository.GetComparisonGroupsAsync(new("", (GitCompareRevisionKind)99), new(b), b));
        string blob = (await NativeAsync(repository, "rev-parse", b + ":" + Dirty)).Trim();
        await ExpectAsync<GitCommandException>(() => repository.GetComparisonGroupsAsync(new(a), new("", GitCompareRevisionKind.Index), blob));
    }

    private static async Task VersionAndPathsAsync()
    {
        using RepositoryFixture f = await CreateSeriesAsync(); string a = await f.Repository.ResolveRevisionAsync("side-a"), b = await f.Repository.ResolveRevisionAsync("HEAD");
        foreach (string version in new[] { "2.18.9", "2.37.0" })
        {
            string wrapper = Path.Combine(f.Root, "git-" + version), log = Path.Combine(f.Root, "argv-" + version + ".jsonl");
            await ExecutableAsync(wrapper, "#!/usr/bin/python3\nimport json,os,sys\na=sys.argv[1:]\nwith open(" + JsonSerializer.Serialize(log) + ",'a') as f:f.write(json.dumps(a)+'\\n')\nif '--version' in a:print('git version " + version + "');sys.exit(0)\nos.execv('/usr/bin/git',['git']+a)\n");
            GitRepository repository = new(f.Repository.Path, new GitRunner(wrapper));
            foreach (GitCompareRevision to in new[] { new GitCompareRevision(b), new("", GitCompareRevisionKind.WorkingTree) })
            {
                IReadOnlyList<GitComparisonGroup> groups = await repository.GetComparisonGroupsAsync(new(a), to, b);
                Check(groups.Count == (version.StartsWith("2.18", StringComparison.Ordinal) ? 3 : 4), "Old Git retains all three classified file groups while omitting unsupported range-diff.");
                if (groups.Count == 4)
                { await ExpectAsync<NotSupportedException>(() => repository.GetRangeDiffAsync(Group(groups, GitRevisionDiffGroupKind.RangeDiff).RangeDiff!, new GitRangeDiffOptions(Paths: [Literal]))); }
            }
            int versionCalls = File.ReadLines(log).Count(line => JsonSerializer.Deserialize<string[]>(line)!.Contains("--version"));
            Check((await repository.GetComparisonGroupsAsync(new(a), new("", GitCompareRevisionKind.Index), b, false)).Count == 1, "showAll false is an ordinary artificial read.");
            Check(File.ReadLines(log).Count(line => JsonSerializer.Deserialize<string[]>(line)!.Contains("--version")) == versionCalls, "Disabled advanced groups do not request range support/version metadata.");
        }
        IReadOnlyList<GitComparisonGroup> nativeGroups = await f.Repository.GetComparisonGroupsAsync(new(a), new(b), b);
        string output = await f.Repository.GetRangeDiffAsync(Group(nativeGroups, GitRevisionDiffGroupKind.RangeDiff).RangeDiff!, new GitRangeDiffOptions(Paths: [Literal]));
        Check(output.Contains("@@", StringComparison.Ordinal) || output.Contains("=", StringComparison.Ordinal), "Supported literal path-filtered range-diff is still displayable.");
    }

    private static async Task BareAndLinkedAsync()
    {
        using RepositoryFixture f = await CreateSeriesAsync(); string a = await f.Repository.ResolveRevisionAsync("side-a"), b = await f.Repository.ResolveRevisionAsync("HEAD");
        string barePath = Path.Combine(f.Root, "bare.git"); await NativeAsync(f.Repository, "clone", "--bare", "--", f.Repository.Path, barePath);
        GitRepository bare = await GitRepository.OpenAsync(barePath); string beforeBare = await SnapshotAsync(bare);
        Check((await bare.GetComparisonGroupsAsync(new(a), new(b), null)).Count == 4, "Bare repositories do not need a status/HEAD lookup for committed groups.");
        await ExpectAsync<InvalidOperationException>(() => bare.GetComparisonGroupsAsync(new(a), new("", GitCompareRevisionKind.Index), b));
        Check(await SnapshotAsync(bare) == beforeBare, "Bare reference and object state stays unchanged.");
        string linkedPath = Path.Combine(f.Root, "linked λ worktree"); await NativeAsync(f.Repository, "worktree", "add", "--detach", "--", linkedPath, "side-a");
        GitRepository linked = await GitRepository.OpenAsync(linkedPath); await File.WriteAllTextAsync(Path.Combine(linked.Path, Untracked), "linked only\n");
        string original = await SnapshotAsync(f.Repository), linkedBefore = await SnapshotAsync(linked);
        IReadOnlyList<GitComparisonGroup> groups = await linked.GetComparisonGroupsAsync(new(b), new("", GitCompareRevisionKind.WorkingTree), a);
        Check(groups.Count == 4 && groups[0].Comparison.UntrackedPaths.Contains(Untracked), "Linked worktree content is read at its own root with captured local HEAD.");
        Check(await SnapshotAsync(f.Repository) == original && await SnapshotAsync(linked) == linkedBefore, "Neither source nor linked checkout changes.");
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using RepositoryFixture f = await CreateSeriesAsync(); string a = await f.Repository.ResolveRevisionAsync("side-a"), b = await f.Repository.ResolveRevisionAsync("HEAD");
        string before = await SnapshotAsync(f.Repository);
        foreach (string command in new[] { "merge-base", "rev-list" })
        {
            string pidPath = Path.Combine(f.Root, command + ".pid"), childPath = Path.Combine(f.Root, command + ".child"), wrapper = Path.Combine(f.Root, command + ".git");
            await ExecutableAsync(wrapper, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = '" + command + "' ]; then\n  printf '%s' \"$$\" > '" + pidPath + "'\n  sleep 60 &\n  printf '%s' \"$!\" > '" + childPath + "'\n  wait\n  exit 1\n fi\ndone\nexec /usr/bin/git \"$@\"\n");
            GitRepository repository = new(f.Repository.Path, new GitRunner(wrapper));
            using CancellationTokenSource cancellation = new();
            Task<IReadOnlyList<GitComparisonGroup>> pending = repository.GetComparisonGroupsAsync(new(a), new("", GitCompareRevisionKind.WorkingTree), b, cancellationToken: cancellation.Token);
            try
            {
                await UntilAsync(() => File.Exists(childPath) && new FileInfo(childPath).Length > 0);
                // A blocked group read must not own the mutation semaphore.
                // RunAsync uses that semaphore even for this harmless --version command.
                await repository.RunAsync(["--version"]).WaitAsync(TimeSpan.FromSeconds(5));
                Check(!pending.IsCompleted, "The native group read is still blocked while an independent read completes.");
                int parent = int.Parse(await File.ReadAllTextAsync(pidPath)), child = int.Parse(await File.ReadAllTextAsync(childPath));
                cancellation.Cancel(); await ExpectAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                await UntilAsync(() => !LinuxProcessState.IsRunning(parent) && !LinuxProcessState.IsRunning(child));
                Console.WriteLine($"DRAIN comparison-groups {command}: {parent} {child} terminated");
            }
            finally { cancellation.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
        }
        Check(await SnapshotAsync(f.Repository) == before, "Cancellation after partial read work does not change repository state or return partial groups.");
    }

    private static async Task<RepositoryFixture> CreateSeriesAsync()
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
        try { await CreateSeriesAsync(f); return f; } catch { f.Dispose(); throw; }
    }
    private static async Task CreateSeriesAsync(RepositoryFixture f)
    {
        GitRepository repository = f.Repository;
        f.Write(Literal, "shared base\n"); f.Write(Dirty, "different base\n"); await CommitAsync(repository, "Base"); await repository.CreateBranchAsync("base");
        await repository.RunAsync(["checkout", "-b", "side-a"]); f.Write(Literal, "same branch change\n"); f.Write(Dirty, "change A\n"); f.Write("only-a.txt", "only A\n"); await CommitAsync(repository, "Series A");
        await repository.RunAsync(["checkout", "-b", "side-b", "base"]); f.Write(Literal, "same branch change\n"); f.Write(Dirty, "change B\n"); f.Write("only-b.txt", "only B\n"); await CommitAsync(repository, "Series B");
    }
    private static async Task CommitAsync(GitRepository repository, string message)
    { await repository.StageAllAsync(); await repository.CommitAsync(message); }
    private static GitComparisonGroup Group(IReadOnlyList<GitComparisonGroup> groups, GitRevisionDiffGroupKind kind) => groups.Single(group => group.Kind == kind);
    private static bool SameFiles(IReadOnlyList<GitRevisionFile> first, IReadOnlyList<GitRevisionFile> second)
        => first.Select(file => (file.Path, file.OriginalPath, file.Status, file.SimilarityPercent)).SequenceEqual(second.Select(file => (file.Path, file.OriginalPath, file.Status, file.SimilarityPercent)));
    private static async Task<string> NativeAsync(GitRepository repository, params string[] arguments)
    {
        GitResult result = await new GitRunner("/usr/bin/git").RunAsync(repository.Path, ["--no-optional-locks", .. arguments]);
        if (!result.Success) { throw new GitCommandException(arguments, result); }
        return result.StandardOutput;
    }
    private static async Task<string> SnapshotAsync(GitRepository repository)
    {
        GitRepositoryInfo info = await repository.GetInfoAsync();
        string refs = await NativeAsync(repository, "for-each-ref", "--format=%(objectname) %(refname)");
        string headPath = (await NativeAsync(repository, "rev-parse", "--path-format=absolute", "--git-path", "HEAD")).TrimEnd('\r', '\n');
        string indexPath = (await NativeAsync(repository, "rev-parse", "--path-format=absolute", "--git-path", "index")).TrimEnd('\r', '\n');
        Dictionary<string, string> files = [];
        if (!info.IsBare)
        {
            foreach (string file in Directory.EnumerateFiles(repository.Path, "*", SearchOption.AllDirectories).Where(path => !Path.GetRelativePath(repository.Path, path).Split(Path.DirectorySeparatorChar).Contains(".git")))
            { files[Path.GetRelativePath(repository.Path, file)] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))); }
        }
        return JsonSerializer.Serialize(new { Refs = refs, Head = await File.ReadAllTextAsync(headPath), Index = File.Exists(indexPath) ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(indexPath))) : null,
            Worktree = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray() });
    }
    private static async Task ExecutableAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!predicate() && wait.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
        Check(predicate(), "A bounded native process barrier/drain must complete.");
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
