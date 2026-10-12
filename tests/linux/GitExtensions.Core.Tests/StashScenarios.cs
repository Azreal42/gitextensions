using GitExtensions.Linux;

internal static class StashScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("selected stash paths remain literal with untracked files and a multiline message", SelectedAsync),
        ("keep index saves staged changes while leaving the real index intact", KeepIndexAsync),
        ("stash staged preserves other unstaged and untracked changes", StagedAsync),
        ("stash preview exposes tracked deletions renames binary and untracked parent", PreviewAsync),
        ("Windows stash apply and pop do not restore the saved index", ApplyPopAsync),
        ("conflicting pop keeps the stash and exposes unresolved index entries", ConflictAsync),
        ("stale stash selection refuses to apply or delete a different entry", StaleAsync),
        ("invalid empty selection bare unborn and cancelled stash preserve the repository", ValidationAsync)
    ];

    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static Task<string> Snapshot(GitRepository r) => SnapshotCore(r);
    private static async Task<string> SnapshotCore(GitRepository r)
        => (await r.RunAsync(["status", "--porcelain=v2", "-z"])).StandardOutput + "\0" + (await r.RunAsync(["ls-files", "--stage", "-z"])).StandardOutput
            + "\0" + (await r.RunAsync(["for-each-ref", "--format=%(refname) %(objectname)"])).StandardOutput;

    private static async Task SelectedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        const string literal = ":x[1]* λ\n雪.txt", adjacent = ":x1 λ\n雪.txt";
        f.Write(literal, "literal base\n"); f.Write(adjacent, "adjacent base\n");
        await r.StageAllAsync(); await r.CommitAsync("Literal names");
        f.Write(literal, "literal change\n"); f.Write(adjacent, "adjacent change\n"); f.Write("new\nλ.txt", "untracked\n");
        await r.StashAsync(new GitStashSaveOptions(" message λ\nsecond line ", true, Paths: [literal, "new\nλ.txt"]));
        Check(File.ReadAllText(f.File(literal)) == "literal base\n" && File.ReadAllText(f.File(adjacent)) == "adjacent change\n", "Only literal selected tracked path is reverted.");
        Check(!File.Exists(f.File("new\nλ.txt")), "Selected untracked file is stashed and cleaned.");
        GitStash stash = (await r.GetStashesAsync()).Single(); GitStashChanges changes = await r.GetStashChangesAsync(stash);
        Check(changes.Message.Contains("message λ\nsecond line", StringComparison.Ordinal), "Multiline message is preserved.");
        Check(changes.Tracked.Files.Single().Path == literal && changes.Untracked!.Files.Single().Path == "new\nλ.txt", "Selected files occupy the right parents.");
        await r.ApplyStashAsync(stash);
        Check(File.ReadAllText(f.File(literal)) == "literal change\n" && File.ReadAllText(f.File("new\nλ.txt")) == "untracked\n", "Exact selected contents restore.");
    }

    private static async Task KeepIndexAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("seed.txt", "staged\n"); await r.StageAsync(["seed.txt"]); f.Write("other.txt", "original\n");
        await r.StageAsync(["other.txt"]); await r.CommitAsync("Other base");
        f.Write("seed.txt", "new staged\n"); await r.StageAsync(["seed.txt"]); f.Write("other.txt", "unstaged\n");
        byte[] index = await r.GetIndexFileBytesAsync("seed.txt");
        await r.StashAsync(new GitStashSaveOptions(KeepIndex: true));
        Check((await r.GetIndexFileBytesAsync("seed.txt")).SequenceEqual(index) && File.ReadAllText(f.File("seed.txt")) == "new staged\n", "Staged bytes and index stay intact.");
        Check(File.ReadAllText(f.File("other.txt")) == "original\n", "Other tracked worktree changes revert.");
        Check((await r.GetStashChangesAsync((await r.GetStashesAsync()).Single())).Tracked.Files.Count == 2, "Stash still contains both changes.");
    }

    private static async Task StagedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        Check(await r.SupportsStashStagedAsync(), "The test Git supports staged stashes.");
        f.Write("other.txt", "base\n"); await r.StageAllAsync(); await r.CommitAsync("Base");
        f.Write("seed.txt", "staged\n"); await r.StageAsync(["seed.txt"]); f.Write("other.txt", "unstaged\n"); f.Write("untracked.txt", "keep\n");
        await r.StashAsync(new GitStashSaveOptions(StagedOnly: true));
        Check(File.ReadAllText(f.File("seed.txt")) == "seed\n" && File.ReadAllText(f.File("other.txt")) == "unstaged\n" && File.ReadAllText(f.File("untracked.txt")) == "keep\n", "Only staged contents revert.");
        Check((await r.GetStashChangesAsync((await r.GetStashesAsync()).Single())).Tracked.Files.Single().Path == "seed.txt", "Only staged path enters stash.");
    }

    private static async Task PreviewAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("delete.txt", "delete\n"); f.Write("move.txt", "move\n"); await r.StageAllAsync(); await r.CommitAsync("Preview base");
        await r.RunAsync(["mv", "--", "move.txt", "renamed λ.txt"]); File.Delete(f.File("delete.txt"));
        byte[] bytes = [0, 128, 255, 10]; await File.WriteAllBytesAsync(f.File("binary.bin"), bytes); f.Write("seed.txt", "preview\n");
        await r.StashAsync(new GitStashSaveOptions(IncludeUntracked: true)); GitStash stash = (await r.GetStashesAsync()).Single();
        string before = await Snapshot(r); GitStashChanges changes = await r.GetStashChangesAsync(stash);
        Check(changes.Tracked.Files.Any(file => file.Path == "delete.txt" && file.IsDeleted) && changes.Tracked.Files.Any(file => file.OriginalPath == "move.txt"), "Preview preserves deletions and renames.");
        Check((await r.GetFileBytesAsync("binary.bin", changes.Untracked!.Revision)).SequenceEqual(bytes), "Untracked binary bytes remain exact.");
        string patch = await r.GetRevisionFileDiffAsync(changes.Tracked, changes.Tracked.Files.Single(file => file.Path == "seed.txt"));
        Check(patch.Contains("+preview", StringComparison.Ordinal) && await Snapshot(r) == before, "Preview reads do not mutate index/worktree/refs.");
    }

    private static async Task ApplyPopAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("seed.txt", "staged change\n"); await r.StageAsync(["seed.txt"]); await r.StashAsync(new GitStashSaveOptions());
        GitStash stash = (await r.GetStashesAsync()).Single(); await r.ApplyStashAsync(stash);
        Check((await r.GetStatusAsync()).Changes.Single().IndexStatus == '.' && (await r.GetStashesAsync()).Count == 1, "Apply retains stash without --index.");
        await r.RunAsync(["reset", "--hard", "HEAD"]); await r.PopLatestStashAsync();
        Check((await r.GetStatusAsync()).Changes.Single().IndexStatus == '.' && (await r.GetStashesAsync()).Count == 0, "Successful pop drops stash without restoring index.");
    }

    private static async Task ConflictAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("seed.txt", "stashed\n"); await r.StashAsync(new GitStashSaveOptions()); GitStash stash = (await r.GetStashesAsync()).Single();
        f.Write("seed.txt", "upstream\n"); await r.StageAllAsync(); await r.CommitAsync("Conflicting checkout");
        try { await r.PopLatestStashAsync(); throw new InvalidOperationException("Expected conflict."); } catch (GitCommandException) { }
        Check((await r.GetStashesAsync()).Single().Hash == stash.Hash && (await r.GetStatusAsync()).Changes.Single().IsConflict, "Conflict keeps the selected stash and unresolved index.");
    }

    private static async Task StaleAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("seed.txt", "first\n"); await r.StashAsync(new GitStashSaveOptions()); GitStash stale = (await r.GetStashesAsync()).Single();
        f.Write("seed.txt", "second\n"); await r.StashAsync(new GitStashSaveOptions()); string before = await Snapshot(r);
        foreach (Func<Task> operation in new Func<Task>[] { () => r.DropStashAsync(stale), () => r.ApplyStashAsync(stale), () => r.ApplyStashAsync(stale, pop: true) })
        {
            try { await operation(); throw new Exception("Stale selection was accepted."); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("stash list changed", StringComparison.Ordinal), "Stale selection explains refresh."); }
            Check(await Snapshot(r) == before && (await r.GetStashesAsync()).Count == 2, "Stale selection cannot affect another entry.");
        }
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("seed.txt", "pending\n"); string before = await Snapshot(r);
        try { await r.StashAsync(new GitStashSaveOptions(Paths: [])); throw new Exception("Empty path list accepted."); } catch (ArgumentException) { }
        try { await r.StashAsync(new GitStashSaveOptions(StagedOnly: true, KeepIndex: true)); throw new Exception("Conflicting flags accepted."); } catch (ArgumentException) { }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        try { await r.StashAsync(new GitStashSaveOptions(), cancellation.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        Check(await Snapshot(r) == before && File.ReadAllText(f.File("seed.txt")) == "pending\n", "Validation/cancellation leave state intact.");
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare.git"), bare: true);
        try { await bare.StashAsync(new GitStashSaveOptions()); throw new Exception("Bare stash accepted."); } catch (InvalidOperationException) { }
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(seed: false); unborn.Write("new.txt", "new\n");
        try { await unborn.Repository.StashAsync(new GitStashSaveOptions(IncludeUntracked: true)); throw new Exception("Unborn stash accepted."); } catch (GitCommandException) { }
        Check(File.ReadAllText(unborn.File("new.txt")) == "new\n", "Unborn error preserves the file.");
    }
}
