using System.Diagnostics;
using System.Text;
using GitExtensions.Linux;

internal static class ComparisonFileMenuScenarios
{
    private const string Literal = ":[menu]* λ\t\n.txt";
    private const string Other = "unrelated.txt";
    private static readonly GitCompareRevision Index = new("", GitCompareRevisionKind.Index);
    private static readonly GitCompareRevision Worktree = new("", GitCompareRevisionKind.WorkingTree);
    public static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("file menu stage state requires the supplied Windows parent, never a HEAD guess", StagedParentAsync),
        ("file menu reversed worktree and historical metadata preserves tracking and identity", DirectionsAsync),
        ("file menu snapshots read gitlinks from both trees and the real index", GitlinksAsync),
        ("file menu metadata reads committed bare snapshots without requiring an index", BareAsync),
        ("typed Index reset copies exact selected bytes and preserves the complete index", ResetIndexAsync),
        ("typed Index reset keeps or deletes only selected new literal paths", NewFilesAsync),
        ("typed Index reset preserves Windows oriented rename and delete semantics", RenamesAsync),
        ("typed Index reset rejects directory collisions and unmerged paths before any write", CollisionsAsync),
        ("typed Index reset restores symlinks without following escaping parent links", SymlinksAsync),
        ("typed Commit reset delegates the existing revision reset and rejects worktree source", CommitAsync),
        ("typed index flag setters preserve literal scopes mixed bits and no-op index bytes", FlagsAsync),
        ("file menu pre-cancellation and invalid paths do not launch native Git", ValidationAsync),
        ("Index reset cancellation drains its actual native actors and preserves the index", CancellationAsync),
        ("interactive file callbacks retain native readonly argv and the mutation lease", InteractiveAsync)
    ];

    private static async Task<RepositoryFixture> CreateAsync()
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync();
        await Git(f, "config", "core.autocrlf", "false");
        f.Write(Literal, "initial literal\n"); f.Write(Other, "initial unrelated\n"); f.Write("-flag[0].txt", "dash literal\n");
        await Git(f, "add", "--all"); await Git(f, "commit", "-m", "Menu base"); return f;
    }
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static async Task<GitResult> Git(RepositoryFixture f, params string[] args)
    {
        GitResult result = await new GitRunner().RunAsync(f.Repository.Path, args);
        if (!result.Success) { throw new GitCommandException(args, result); } return result;
    }
    private static async Task<string> Head(RepositoryFixture f) => (await Git(f, "rev-parse", "HEAD")).StandardOutput.Trim();
    private static byte[] IndexBytes(RepositoryFixture f) => File.ReadAllBytes(f.File(".git/index"));
    private static Task Commit(RepositoryFixture f, string message) => Git(f, "commit", "-am", message);
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(20); }
        Check(condition(), "The native menu fixture did not reach its checkpoint.");
    }

    private static async Task StagedParentAsync()
    {
        using RepositoryFixture f = await CreateAsync(); GitRepository r = f.Repository;
        string before = await Head(f); f.Write(Literal, "second committed\n"); await Commit(f, "Next"); string head = await Head(f);
        f.Write(Literal, "indexed\n"); await r.StageAsync([Literal]); f.Write(Literal, "worktree\n");
        GitComparison staged = await r.GetComparisonAsync(new(head), Index);
        Check((await r.GetComparisonFileMetadataAsync(staged, new(head))).Single().Staged == GitFileStagedStatus.Index, "Only the supplied actual first parent makes this an Index item.");
        Check((await r.GetComparisonFileMetadataAsync(staged, null)).Single().Staged == GitFileStagedStatus.Unknown, "Unhydrated FormDiff has no implicit HEAD parent.");
        GitComparison old = await r.GetComparisonAsync(new(before), Index);
        Check((await r.GetComparisonFileMetadataAsync(old, new(head))).Single().Staged == GitFileStagedStatus.Unknown, "An arbitrary commit is not the parent merely because Second is Index.");
        Check((await r.GetComparisonFileMetadataAsync(await r.GetComparisonAsync(Index, Worktree), null)).Single().Staged == GitFileStagedStatus.WorkTree, "Index-to-worktree does not need a parent guess.");
        Check((await r.GetComparisonFileMetadataAsync(await r.GetComparisonAsync(Worktree, Index), new(head))).Single().Staged == GitFileStagedStatus.Unknown, "The reverse pair is not a worktree staging source.");
    }

    private static async Task DirectionsAsync()
    {
        using RepositoryFixture f = await CreateAsync(); GitRepository r = f.Repository; string first = await Head(f);
        f.Write(Literal, "changed\n"); await Commit(f, "Changed"); string second = await Head(f);
        f.Write("new λ*.txt", "untracked\n");
        GitComparison history = await r.GetComparisonAsync(new(first), new(second));
        GitRevisionFile original = history.Files.Single(); byte[] index = IndexBytes(f);
        GitComparisonFileMetadata historical = (await r.GetComparisonFileMetadataAsync(history, null)).Single();
        Check(historical.Staged == GitFileStagedStatus.None && historical.IsTracked && ReferenceEquals(historical.File, original), "Sidecars keep the original diff object and historical tracking.");
        foreach (bool reverse in new[] { false, true })
        {
            GitComparison pair = await r.GetComparisonAsync(reverse ? Worktree : new(second), reverse ? new(second) : Worktree);
            GitComparisonFileMetadata untracked = (await r.GetComparisonFileMetadataAsync(pair, null)).Single(item => item.File.Path == "new λ*.txt");
            Check(!untracked.IsTracked && untracked.Staged == GitFileStagedStatus.Unknown && untracked.File.Status == (reverse ? 'D' : 'A'), "Untracked provenance survives a reversed delete presentation.");
        }
        Check(IndexBytes(f).SequenceEqual(index), "Menu reads never refresh or rewrite index bytes.");
    }

    private static async Task GitlinksAsync()
    {
        using RepositoryFixture f = await CreateAsync(); GitRepository r = f.Repository; string baseHash = await Head(f);
        string old = "modules/old λ[1]", current = "modules/new λ[1]";
        await Git(f, "update-index", "--add", "--cacheinfo", "160000," + baseHash + "," + old);
        await Git(f, "commit", "-m", "Gitlink old"); string first = await Head(f);
        await Git(f, "update-index", "--force-remove", "--", old);
        await Git(f, "update-index", "--add", "--cacheinfo", "160000," + baseHash + "," + current);
        await Git(f, "commit", "-m", "Gitlink renamed"); string second = await Head(f);
        GitComparison pair = await r.GetComparisonAsync(new(first), new(second));
        Check((await r.GetComparisonFileMetadataAsync(pair, null)).All(item => item.IsSubmodule && item.IsTracked), "Gitlink identity is read from old/new trees, not Directory.Exists.");
        GitComparison indexed = new(new(second), Index, [new(current, 'M')], []);
        Check((await r.GetComparisonFileMetadataAsync(indexed, null)).Single().IsSubmodule, "The real index mode retains gitlink metadata.");
        GitComparison renamed = new(new(first), new(second), [new(current, 'R', old)], []);
        Check((await r.GetComparisonFileMetadataAsync(renamed, null)).Single().IsSubmodule, "The first side uses OriginalPath for a renamed gitlink.");
    }

    private static async Task BareAsync()
    {
        using RepositoryFixture f = await CreateAsync(); string before = await Head(f); f.Write(Literal, "committed\n"); await Commit(f, "New");
        string bare = Path.Combine(f.Root, "bare.git"); await Git(f, "clone", "--bare", "--no-hardlinks", f.Repository.Path, bare);
        GitRepository r = new(bare); GitComparison pair = await r.GetComparisonAsync(new(before), new(await Head(f)));
        GitComparisonFileMetadata item = (await r.GetComparisonFileMetadataAsync(pair, null)).Single();
        Check(item.IsTracked && item.Staged == GitFileStagedStatus.None && !item.IsAssumeUnchanged && !item.IsSkipWorktree, "Bare history does not need a worktree/index flag query.");
    }

    private static async Task ResetIndexAsync()
    {
        using RepositoryFixture f = await CreateAsync(); GitRepository r = f.Repository;
        byte[] expected = [0, 255, 10, 13, 128, 42]; File.WriteAllBytes(f.File(Literal), expected); await r.StageAsync([Literal]);
        await r.SetSkipWorktreeAsync([Literal], true); await r.SetAssumeUnchangedAsync([Literal], true);
        f.Write(Literal, "dirty selected\n"); f.Write(Other, "indexed unrelated\n"); await r.StageAsync([Other]); f.Write(Other, "dirty unrelated\n");
        byte[] index = IndexBytes(f); string head = await Head(f);
        Check((await r.GetIndexFilePathsAsync()).Contains(Literal, StringComparer.Ordinal), "The reset dialog can inspect actual stage-zero presence without decoding a blob.");
        await r.RestoreComparisonFilesAsync(Index, [new(Literal, 'M')]);
        Check(File.ReadAllBytes(f.File(Literal)).SequenceEqual(expected), "Reset reads index bytes, not HEAD or a text decoder.");
        Check(File.ReadAllText(f.File(Other)) == "dirty unrelated\n" && IndexBytes(f).SequenceEqual(index) && await Head(f) == head, "Neither unrelated paths nor any index byte/ref changes.");
    }

    private static async Task NewFilesAsync()
    {
        using RepositoryFixture f = await CreateAsync(); string selected = ":[new]* λ\t\n", sibling = ":[new]other λ";
        f.Write(selected, "delete only me\n"); f.Write(sibling, "keep sibling\n"); byte[] index = IndexBytes(f);
        await f.Repository.RestoreComparisonFilesAsync(Index, [new(selected, 'A')]);
        Check(File.Exists(f.File(selected)), "Reset without explicit deletion keeps untracked files.");
        await f.Repository.RestoreComparisonFilesAsync(Index, [new(selected, 'A')], deleteNewFiles: true);
        Check(!File.Exists(f.File(selected)) && File.ReadAllText(f.File(sibling)) == "keep sibling\n" && IndexBytes(f).SequenceEqual(index), "The explicit delete checkbox is literal and index-preserving.");
    }

    private static async Task RenamesAsync()
    {
        using RepositoryFixture f = await CreateAsync(); string renamed = "renamed λ*[2].txt"; byte[] index = IndexBytes(f);
        File.Move(f.File(Literal), f.File(renamed)); f.Write(renamed, "renamed dirty\n");
        await f.Repository.RestoreComparisonFilesAsync(Index, [new(renamed, 'R', Literal)]);
        Check(File.ReadAllText(f.File(Literal)) == "initial literal\n" && File.ReadAllText(f.File(renamed)) == "renamed dirty\n", "Without deletion, Windows checks out the old name and retains the new physical path.");
        File.Delete(f.File(Literal));
        await f.Repository.RestoreComparisonFilesAsync(Index, [new(renamed, 'R', Literal)], deleteNewFiles: true);
        Check(!File.Exists(f.File(renamed)) && !File.Exists(f.File(Literal)) && IndexBytes(f).SequenceEqual(index), "The deleted rename is not subsequently checked out by the Windows Index reset loop.");
        await f.Repository.RestoreComparisonFilesAsync(Index, [new(Literal, 'D')]);
        Check(File.Exists(f.File(Literal)) && IndexBytes(f).SequenceEqual(index), "A plain deleted tracked path is restored from the index.");
    }

    private static async Task CollisionsAsync()
    {
        using RepositoryFixture f = await CreateAsync(); GitRepository r = f.Repository;
        byte[] original = IndexBytes(f);
        await Expect<InvalidOperationException>(() => r.RestoreComparisonFilesAsync(Index,
            [new(Other, 'R', Literal), new(Other, 'M')], deleteNewFiles: true));
        Check(IndexBytes(f).SequenceEqual(original) && File.ReadAllText(f.File(Other)) == "initial unrelated\n", "Ambiguous delete/restore destinations are refused before either selected action.");
        f.Write(Other, "must not change\n"); File.Delete(f.File(Literal)); Directory.CreateDirectory(f.File(Literal)); f.Write(Literal + "/child", "untracked child\n");
        byte[] index = IndexBytes(f);
        await Expect<InvalidOperationException>(() => r.RestoreComparisonFilesAsync(Index, [new(Other, 'M'), new(Literal, 'M')]));
        Check(File.ReadAllText(f.File(Other)) == "must not change\n" && File.Exists(f.File(Literal + "/child")) && IndexBytes(f).SequenceEqual(index), "Validate every collision before the first write.");
        Directory.Delete(f.File(Literal), true); await r.RestoreComparisonFilesAsync(Index, [new(Literal, 'D')]);
        f.Write(Literal, "left\n"); await Commit(f, "Left"); await Git(f, "branch", "left"); await Git(f, "checkout", "-b", "right", "HEAD^");
        f.Write(Literal, "right\n"); await Commit(f, "Right");
        GitResult merge = await new GitRunner().RunAsync(r.Path, ["merge", "left"]); Check(merge.ExitCode == 1, "The real Git fixture must be unmerged.");
        index = IndexBytes(f); string text = File.ReadAllText(f.File(Literal));
        Check(!(await r.GetIndexFilePathsAsync()).Contains(Literal, StringComparer.Ordinal), "Unmerged entries are not advertised as restorable stage-zero paths.");
        await Expect<InvalidOperationException>(() => r.RestoreComparisonFilesAsync(Index, [new(Literal, 'U')]));
        Check(IndexBytes(f).SequenceEqual(index) && File.ReadAllText(f.File(Literal)) == text, "Unmerged stages are not replaced or unstaged.");
    }

    private static async Task SymlinksAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        using RepositoryFixture f = await CreateAsync(); GitRepository r = f.Repository; string link = "link λ[2]";
        File.CreateSymbolicLink(f.File(link), Other); await r.StageAsync([link]); byte[] index = IndexBytes(f);
        File.Delete(f.File(link)); File.CreateSymbolicLink(f.File(link), "seed.txt");
        await r.RestoreComparisonFilesAsync(Index, [new(link, 'M')]);
        Check(new FileInfo(f.File(link)).LinkTarget == Other && IndexBytes(f).SequenceEqual(index)
            && File.ReadAllText(f.File(Other)) == "initial unrelated\n", "A symlink is restored as a symlink, not through its target.");
        f.Write("inside/item.txt", "indexed inner\n"); await r.StageAsync(["inside/item.txt"]); index = IndexBytes(f);
        Directory.Delete(f.File("inside"), true); string outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "item.txt"), "outside untouched\n"); Directory.CreateSymbolicLink(f.File("inside"), outside);
        await Expect<InvalidOperationException>(() => r.RestoreComparisonFilesAsync(Index, [new("inside/item.txt", 'M')]));
        Check(File.ReadAllText(Path.Combine(outside, "item.txt")) == "outside untouched\n" && IndexBytes(f).SequenceEqual(index), "An escaping parent symlink cannot redirect a selected reset.");
    }

    private static async Task CommitAsync()
    {
        using RepositoryFixture f = await CreateAsync(); string source = await Head(f); f.Write(Literal, "indexed\n"); await f.Repository.StageAsync([Literal]);
        await f.Repository.RestoreComparisonFilesAsync(new(source), [new(Literal, 'M')]);
        Check(File.ReadAllText(f.File(Literal)) == "initial literal\n" && (await Git(f, "diff", "--cached", "--", Literal)).StandardOutput.Length == 0, "Commit source keeps the existing index+worktree reset contract.");
        await Expect<ArgumentException>(() => f.Repository.RestoreComparisonFilesAsync(Worktree, [new(Literal, 'M')]));
    }

    private static async Task FlagsAsync()
    {
        using RepositoryFixture f = await CreateAsync(); GitRepository r = f.Repository; byte[] data = File.ReadAllBytes(f.File(Literal));
        await r.SetSkipWorktreeAsync([Literal], true); await r.SetAssumeUnchangedAsync([Literal], true);
        IReadOnlyList<GitIndexFileFlags> flags = await r.GetIndexFlagsAsync();
        Check(flags.Single(item => item.Path == Literal) is { IsSkipWorktree: true, IsAssumeUnchanged: true } && flags.Single(item => item.Path == Other) is { IsSkipWorktree: false, IsAssumeUnchanged: false }, "NUL stdin retains both bits only on the literal selected name.");
        byte[] index = IndexBytes(f); await r.SetSkipWorktreeAsync([Literal, Literal], true);
        Check(IndexBytes(f).SequenceEqual(index), "A same-value update is a byte-preserving no-op.");
        GitComparison pair = new(new(await Head(f)), Worktree, [new(Literal, 'M')], []);
        GitComparisonFileMetadata metadata = (await r.GetComparisonFileMetadataAsync(pair, null)).Single();
        Check(metadata.IsSkipWorktree && metadata.IsAssumeUnchanged && metadata.IsTracked, "The menu sidecar rereads both current index flags.");
        await Expect<InvalidOperationException>(() => r.SetSkipWorktreeAsync([Other, "not indexed"], true));
        Check(IndexBytes(f).SequenceEqual(index), "A mixed invalid request does not partly change valid selected flags.");
        await r.SetSkipWorktreeAsync([Literal], false); await r.SetAssumeUnchangedAsync([Literal], false);
        await r.SetAssumeUnchangedAsync(["-flag[0].txt"], true);
        Check((await r.GetIndexFlagsAsync()).Single(item => item.Path == "-flag[0].txt").IsAssumeUnchanged, "A leading dash in a NUL stdin pathname never becomes an option.");
        await r.SetAssumeUnchangedAsync(["-flag[0].txt"], false);
        Check((await r.GetIndexFlagsAsync()).All(item => !item.IsSkipWorktree && !item.IsAssumeUnchanged) && File.ReadAllBytes(f.File(Literal)).SequenceEqual(data), "Turning flags off preserves worktree bytes.");
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await CreateAsync(); string marker = Path.Combine(f.Root, "called");
        string wrapper = await Executable(f, "must-not-run", "#!/bin/sh\nprintf called > " + Quote(marker) + "\nexit 93\n");
        GitRepository guarded = new(f.Repository.Path, new GitRunner(wrapper)); using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        GitComparison pair = new(new(await Head(f)), Worktree, [new(Literal, 'M')], []);
        await Expect<OperationCanceledException>(() => guarded.GetComparisonFileMetadataAsync(pair, null, cancelled.Token));
        await Expect<OperationCanceledException>(() => guarded.RestoreComparisonFilesAsync(Index, [new(Literal, 'M')], cancellationToken: cancelled.Token));
        await Expect<OperationCanceledException>(() => guarded.SetSkipWorktreeAsync([Literal], true, cancelled.Token));
        await Expect<OperationCanceledException>(() => guarded.SetAssumeUnchangedAsync([Literal], true, cancelled.Token));
        await Expect<OperationCanceledException>(() => guarded.RunInteractiveFileActionAsync(GitInteractiveFileAction.AddPatch, [Literal], (_, _) => throw new InvalidOperationException("Unexpected callback"), cancelled.Token));
        await Expect<ArgumentException>(() => guarded.RestoreComparisonFilesAsync(Index, [new("../escape", 'M')]));
        await Expect<ArgumentException>(() => guarded.SetSkipWorktreeAsync([], true));
        await Expect<ArgumentException>(() => guarded.RunInteractiveFileActionAsync(GitInteractiveFileAction.AddPatch, ["../escape"], (_, _) => throw new InvalidOperationException("Unexpected callback")));
        Check(!File.Exists(marker), "Cancelled and invalid operations never dispatch native Git or a console callback.");
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        using RepositoryFixture f = await CreateAsync(); string marker = Path.Combine(f.Root, "actors");
        string wrapper = await Executable(f, "blocked checkout-index", "#!/bin/sh\ncase \" $* \" in *' checkout-index '*) sleep 60 &\nchild=$!\nprintf '%s %s\\n' \"$$\" \"$child\" > " + Quote(marker) + "\nwait \"$child\";; *) exec /usr/bin/git \"$@\";; esac\n");
        GitRepository guarded = new(f.Repository.Path, new GitRunner(wrapper)); f.Write(Literal, "keep on cancellation\n"); byte[] index = IndexBytes(f);
        using CancellationTokenSource cancel = new(); Task task = guarded.RestoreComparisonFilesAsync(Index, [new(Literal, 'M')], cancellationToken: cancel.Token);
        await Until(() => File.Exists(marker)); int[] actors = File.ReadAllText(marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        cancel.Cancel(); await Expect<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(15)));
        await Until(() => actors.All(Stopped));
        Check(File.ReadAllText(f.File(Literal)) == "keep on cancellation\n" && IndexBytes(f).SequenceEqual(index), "Cancellation drains both owned actors without a reset or index write.");
    }

    private static async Task InteractiveAsync()
    {
        using RepositoryFixture f = await CreateAsync(); GitRepository r = f.Repository; f.Write(Literal, "initial literal\nnew change\n"); byte[] index = IndexBytes(f);
        foreach (GitInteractiveFileAction action in Enum.GetValues<GitInteractiveFileAction>())
        {
            GitResult result = await r.RunInteractiveFileActionAsync(action, [Literal], async (args, token) =>
            {
                Check(args.SequenceEqual(new[] { "--literal-pathspecs", action == GitInteractiveFileAction.AddPatch ? "add" : "checkout", "--patch", "--", Literal }), "Native argv distinguish checkout -p from index-mutating reset -p.");
                Check(args is IList<string> { IsReadOnly: true }, "The console cannot mutate the captured argument collection.");
                using CancellationTokenSource competing = new(TimeSpan.FromMilliseconds(100));
                await Expect<OperationCanceledException>(() => r.SetSkipWorktreeAsync([Other], true, competing.Token));
                return await new GitRunner(r.GitExecutable).RunAsync(r.Path, args, token, standardInput: "n\n");
            });
            Check(result.Success, "Actual interactive Git accepts the callback's literal argv.");
            Check(IndexBytes(f).SequenceEqual(index), "Answering no in both patch operations preserves the exact index.");
        }
        string entries = (await Git(f, "ls-files", "--stage", "-z")).StandardOutput;
        GitResult applied = await r.RunInteractiveFileActionAsync(GitInteractiveFileAction.ResetPatch, [Literal], (args, token)
            => new GitRunner().RunAsync(r.Path, args, token, standardInput: "y\n"));
        Check(applied.Success && File.ReadAllText(f.File(Literal)) == "initial literal\n"
            && (await Git(f, "ls-files", "--stage", "-z")).StandardOutput == entries,
            "Native checkout -p restores worktree and preserves index entries; Git may update its index stat cache.");
    }

    private static async Task<string> Executable(RepositoryFixture f, string name, string contents)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        string path = Path.Combine(f.Root, name); await File.WriteAllTextAsync(path, contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static bool Stopped(int pid)
    {
        try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); int end = stat.LastIndexOf(')'); return end >= 0 && stat.Length > end + 2 && stat[end + 2] is 'Z' or 'X'; }
        catch (IOException) { return true; }
    }
}
