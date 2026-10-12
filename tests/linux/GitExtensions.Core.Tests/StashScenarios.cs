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
        ("selected stash cherry-pick stages exact binary non-UTF8 rename deletion and untracked bytes", CherryPickSelectedAsync),
        ("selected stash copy destination excludes changes to its source path", CopySelectedAsync),
        ("selected stash cherry-pick merges through the index and retains stash on conflict", CherryPickConflictAsync),
        ("selected stash reset uses first parent and preserves optional new files", RestoreSelectedAsync),
        ("selected stash reset rejects all untracked collisions before changing the index", RestoreCollisionAsync),
        ("invalid stale bare cancelled and foreign stash file selections have no partial effect", FileSelectionValidationAsync),
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

    private static async Task CherryPickSelectedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        const string literal = ":text[1]* λ\n雪.txt";
        byte[] originalText = [0xE9, 0x20, 0x62, 0x61, 0x73, 0x65, 0x0A], changedText = [0xE9, 0x20, 0x6E, 0x65, 0x77, 0x0A];
        byte[] binary = [0, 255, 128, 13, 10], untracked = [0, 129, 254, 10];
        await File.WriteAllBytesAsync(f.File(literal), originalText); await File.WriteAllBytesAsync(f.File("binary.bin"), [0, 1, 2, 3]);
        f.Write("move.txt", "renamed contents\n"); f.Write("delete.txt", "deleted contents\n"); f.Write("unrelated.txt", "base\n");
        await r.StageAllAsync(); await r.CommitAsync("Selected patch base");
        await File.WriteAllBytesAsync(f.File(literal), changedText); await File.WriteAllBytesAsync(f.File("binary.bin"), binary);
        await File.WriteAllBytesAsync(f.File("new\nλ.bin"), untracked); File.Delete(f.File("delete.txt"));
        await r.RunAsync(["mv", "--", "move.txt", "renamed λ.txt"]); f.Write("seed.txt", "excluded stash change\n");
        await r.StashAsync(new GitStashSaveOptions(IncludeUntracked: true)); GitStash stash = (await r.GetStashesAsync()).Single();
        GitStashChanges changes = await r.GetStashChangesAsync(stash);
        f.Write("seed.txt", "keep staged\n"); await r.StageAsync(["seed.txt"]); f.Write("unrelated.txt", "keep unstaged\n");
        byte[] untouchedIndex = await r.GetIndexFileBytesAsync("seed.txt");
        GitRevisionFile[] selected = [.. changes.Tracked.Files.Where(file => file.Path != "seed.txt"), .. changes.Untracked!.Files];
        await r.SetConfigAsync("diff.noprefix", "true"); await r.SetConfigAsync("diff.mnemonicprefix", "true");
        await r.SetConfigAsync("diff.outputIndicatorNew", ">"); await r.SetConfigAsync("diff.outputIndicatorOld", "<"); await r.SetConfigAsync("diff.outputIndicatorContext", "=");
        await r.ApplyStashChangesAsync(stash, selected);
        Check((await File.ReadAllBytesAsync(f.File(literal))).SequenceEqual(changedText) && (await r.GetIndexFileBytesAsync(literal)).SequenceEqual(changedText), "Canonical non-UTF8 patch bytes reach both index and worktree.");
        Check((await File.ReadAllBytesAsync(f.File("binary.bin"))).SequenceEqual(binary) && (await r.GetIndexFileBytesAsync("binary.bin")).SequenceEqual(binary), "Binary patch reaches both destinations.");
        Check((await r.GetIndexFileBytesAsync("new\nλ.bin")).SequenceEqual(untracked), "Selected untracked stash parent is staged with exact bytes.");
        Check(!File.Exists(f.File("delete.txt")) && !File.Exists(f.File("move.txt")) && File.ReadAllText(f.File("renamed λ.txt")) == "renamed contents\n", "Selected deletion and rename apply completely.");
        Check((await r.GetIndexFileBytesAsync("seed.txt")).SequenceEqual(untouchedIndex) && File.ReadAllText(f.File("unrelated.txt")) == "keep unstaged\n", "Excluded changes keep their index/worktree state.");
        Check((await r.GetStashesAsync()).Single().Hash == stash.Hash, "Cherry-pick never drops or rewrites its stash.");
    }

    private static async Task CherryPickConflictAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("seed.txt", "stashed line\n"); await r.StashAsync(new GitStashSaveOptions());
        GitStash stash = (await r.GetStashesAsync()).Single(); GitRevisionFile[] files = [.. (await r.GetStashChangesAsync(stash)).Tracked.Files];
        f.Write("seed.txt", "upstream line\n"); await r.StageAllAsync(); await r.CommitAsync("Conflicting branch");
        try { await r.ApplyStashChangesAsync(stash, files); throw new Exception("Expected three-way conflict."); } catch (GitCommandException) { }
        Check((await r.GetStatusAsync()).Changes.Single().IsConflict && (await r.GetStashesAsync()).Single().Hash == stash.Hash, "Three-way conflict retains the stash and exposes unresolved index stages.");
        await r.RestoreStashFilesAsync(stash, files, toParent: true);
        Check(!(await r.GetStatusAsync()).Changes.Any(change => change.IsConflict) && File.ReadAllText(f.File("seed.txt")) == "seed\n" && (await r.GetIndexFileBytesAsync("seed.txt")).SequenceEqual("seed\n"u8.ToArray()), "Reset to first parent resolves selected conflict in both index and worktree.");
        await r.RunAsync(["reset", "--hard", "HEAD"]); f.Write("seed.txt", "uncommitted line\n");
        string before = await Snapshot(r);
        try { await r.ApplyStashChangesAsync(stash, files); throw new Exception("Expected dirty index/worktree rejection."); } catch (GitCommandException) { }
        Check(await Snapshot(r) == before && File.ReadAllText(f.File("seed.txt")) == "uncommitted line\n" && (await r.GetStashesAsync()).Single().Hash == stash.Hash, "Failed index precondition preserves local work and original stash.");
    }

    private static async Task CopySelectedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("source.txt", "original copied contents\n"); await r.StageAllAsync(); await r.CommitAsync("Copy base");
        f.Write("copied λ.txt", "original copied contents\n"); f.Write("source.txt", "unselected source changes\n");
        await r.StageAllAsync();
        await r.StashAsync(new GitStashSaveOptions()); GitStash stash = (await r.GetStashesAsync()).Single();
        GitRevisionFile copied = (await r.GetStashChangesAsync(stash)).Tracked.Files.Single(file => file.Path == "copied λ.txt");
        Check(copied.Status == 'C' && copied.OriginalPath == "source.txt", "Real Git identifies the changed-source copy.");
        await r.ApplyStashChangesAsync(stash, [copied]);
        Check(File.ReadAllText(f.File("source.txt")) == "original copied contents\n" && (await r.GetStatusAsync()).Changes.Single().Path == "copied λ.txt", "Cherry-pick touches the copy destination only.");
        await r.RestoreStashFilesAsync(stash, [copied], toParent: true, deleteNewFiles: true);
        Check(!File.Exists(f.File("copied λ.txt")) && (await r.GetStatusAsync()).Changes.Count == 0, "First-parent reset of a copy only removes its destination.");
        await r.RestoreStashFilesAsync(stash, [copied]);
        Check(File.ReadAllText(f.File("source.txt")) == "original copied contents\n" && File.ReadAllText(f.File("copied λ.txt")) == "original copied contents\n" && (await r.GetStashesAsync()).Single().Hash == stash.Hash, "Stash snapshot restore also leaves copy source and stash untouched.");
    }

    private static async Task RestoreSelectedAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        const string literal = ":reset[1]* λ\n雪.bin";
        byte[] beforeBytes = [0, 128, 254, 10], stashBytes = [0, 255, 129, 13, 10];
        await File.WriteAllBytesAsync(f.File(literal), beforeBytes); f.Write("move.txt", "rename base\n"); f.Write("delete.txt", "delete base\n");
        f.Write("other.txt", "other base\n"); await r.StageAllAsync(); await r.CommitAsync("Restore base");
        await File.WriteAllBytesAsync(f.File(literal), stashBytes); File.Delete(f.File("delete.txt"));
        await r.RunAsync(["mv", "--", "move.txt", "renamed λ.txt"]); f.Write("added.txt", "new tracked\n"); await r.StageAsync(["added.txt"]);
        f.Write("untracked.txt", "new untracked\n"); await r.StashAsync(new GitStashSaveOptions(IncludeUntracked: true));
        GitStash stash = (await r.GetStashesAsync()).Single(); GitStashChanges changes = await r.GetStashChangesAsync(stash);
        GitRevisionFile[] files = [.. changes.Tracked.Files, .. changes.Untracked!.Files];
        f.Write("other.txt", "keep staged\n"); await r.StageAsync(["other.txt"]); f.Write("other.txt", "keep unstaged\n"); byte[] otherIndex = await r.GetIndexFileBytesAsync("other.txt");
        await r.RestoreStashFilesAsync(stash, files);
        Check((await File.ReadAllBytesAsync(f.File(literal))).SequenceEqual(stashBytes) && (await r.GetIndexFileBytesAsync(literal)).SequenceEqual(stashBytes), "Restoring stash snapshot preserves literal binary bytes.");
        Check(File.Exists(f.File("delete.txt")) && File.Exists(f.File("move.txt")) && File.Exists(f.File("renamed λ.txt")), "Default reset preserves missing snapshot paths and the opposite rename name.");
        Check(File.ReadAllText(f.File("added.txt")) == "new tracked\n" && File.ReadAllText(f.File("untracked.txt")) == "new untracked\n", "Tracked additions and untracked stash snapshot files restore.");
        await r.RestoreStashFilesAsync(stash, files, toParent: true);
        Check((await File.ReadAllBytesAsync(f.File(literal))).SequenceEqual(beforeBytes) && File.Exists(f.File("added.txt")) && File.Exists(f.File("untracked.txt")), "First-parent reset preserves new files unless deletion is chosen.");
        Check((await r.GetStatusAsync()).Changes.Single(change => change.Path == "added.txt").IsUntracked, "Preserved new files are unstaged towards HEAD, as on Windows.");
        await r.RestoreStashFilesAsync(stash, files, toParent: true, deleteNewFiles: true);
        Check(!File.Exists(f.File("added.txt")) && !File.Exists(f.File("untracked.txt")) && !File.Exists(f.File("renamed λ.txt")) && File.Exists(f.File("move.txt")), "Delete-new-files removes only selected absent snapshot paths.");
        await r.RestoreStashFilesAsync(stash, files, deleteNewFiles: true);
        Check(!File.Exists(f.File("delete.txt")) && !File.Exists(f.File("move.txt")) && File.Exists(f.File("renamed λ.txt")), "Stash-directed reset deletes selected stash deletion and old rename name.");
        Check((await r.GetStatusAsync()).Changes.Single(change => change.Path == "delete.txt").IndexStatus == '.', "Optional deletion leaves the HEAD index entry rather than staging a deletion.");
        Check((await r.GetIndexFileBytesAsync("other.txt")).SequenceEqual(otherIndex) && File.ReadAllText(f.File("other.txt")) == "keep unstaged\n" && (await r.GetStashesAsync()).Single().Hash == stash.Hash, "Repeated reset keeps excluded index/worktree and stash unchanged.");
    }

    private static async Task RestoreCollisionAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("seed.txt", "stashed\n"); f.Write("added.txt", "stashed new\n"); await r.StashAsync(new GitStashSaveOptions(IncludeUntracked: true));
        GitStash stash = (await r.GetStashesAsync()).Single(); GitStashChanges changes = await r.GetStashChangesAsync(stash);
        GitRevisionFile[] files = [.. changes.Tracked.Files, .. changes.Untracked!.Files];
        f.Write("seed.txt", "preserve staged\n"); await r.StageAsync(["seed.txt"]); f.Write("added.txt", "preserve untracked\n"); string before = await Snapshot(r);
        try { await r.RestoreStashFilesAsync(stash, files); throw new Exception("Untracked collision accepted."); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("untracked", StringComparison.Ordinal), "Collision explains protected file."); }
        Check(await Snapshot(r) == before && File.ReadAllText(f.File("added.txt")) == "preserve untracked\n" && (await r.GetIndexFileBytesAsync("seed.txt")).SequenceEqual("preserve staged\n"u8.ToArray()), "A later invalid path cannot partially reset an earlier selected path.");
        File.Delete(f.File("added.txt")); Directory.CreateDirectory(f.File("added.txt")); f.Write("added.txt/keep.txt", "keep directory\n"); before = await Snapshot(r);
        try { await r.RestoreStashFilesAsync(stash, files, toParent: true, deleteNewFiles: true); throw new Exception("Directory deletion accepted."); } catch (InvalidOperationException) { }
        Check(await Snapshot(r) == before && File.ReadAllText(f.File("added.txt/keep.txt")) == "keep directory\n" && (await r.GetStashesAsync()).Single().Hash == stash.Hash, "Directory obstruction is rejected before index changes and retains the stash.");

        using RepositoryFixture linked = await RepositoryFixture.CreateAsync(); GitRepository lr = linked.Repository;
        linked.Write("nested/file.txt", "nested base\n"); await lr.StageAllAsync(); await lr.CommitAsync("Symlink base");
        linked.Write("nested/file.txt", "nested stash\n"); linked.Write("seed.txt", "stash seed\n"); await lr.StashAsync(new GitStashSaveOptions());
        GitStash linkedStash = (await lr.GetStashesAsync()).Single(); GitRevisionFile[] linkedFiles = [.. (await lr.GetStashChangesAsync(linkedStash)).Tracked.Files];
        File.Delete(linked.File("nested/file.txt")); Directory.Delete(linked.File("nested"));
        string outside = Path.Combine(linked.Root, "outside"); Directory.CreateDirectory(outside); await File.WriteAllTextAsync(Path.Combine(outside, "file.txt"), "outside bytes\n");
        Directory.CreateSymbolicLink(linked.File("nested"), outside); linked.Write("seed.txt", "keep staged\n"); await lr.StageAsync(["seed.txt"]); before = await Snapshot(lr);
        try { await lr.RestoreStashFilesAsync(linkedStash, linkedFiles); throw new Exception("Symlink ancestor accepted."); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("symbolic link", StringComparison.Ordinal), "Escape rejection explains the ancestor link."); }
        Check(await Snapshot(lr) == before && File.ReadAllText(Path.Combine(outside, "file.txt")) == "outside bytes\n" && (await lr.GetStashesAsync()).Single().Hash == linkedStash.Hash, "Symlink ancestors cannot escape the worktree or partially reset selected index paths.");
    }

    private static async Task FileSelectionValidationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write("seed.txt", "stashed\n"); await r.StashAsync(new GitStashSaveOptions()); GitStash stash = (await r.GetStashesAsync()).Single();
        GitRevisionFile file = (await r.GetStashChangesAsync(stash)).Tracked.Files.Single(); f.Write("seed.txt", "preserve local\n");
        string before = await Snapshot(r);
        foreach (Func<Task> operation in new Func<Task>[]
        {
            () => r.ApplyStashChangesAsync(stash, [file, new("foreign.txt", 'M')]),
            () => r.RestoreStashFilesAsync(stash, [file, new("foreign.txt", 'M')]),
            () => r.ApplyStashChangesAsync(stash, [file with { Status = 'D' }]),
            () => r.RestoreStashFilesAsync(stash, [file with { OriginalPath = "foreign.txt" }]),
            () => r.ApplyStashChangesAsync(stash, []),
            () => r.RestoreStashFilesAsync(stash, [new("../outside", 'M')])
        })
        {
            try { await operation(); throw new Exception("Invalid selection accepted."); } catch (ArgumentException) { }
            Check(await Snapshot(r) == before && File.ReadAllText(f.File("seed.txt")) == "preserve local\n", "Invalid selection leaves every selected index/worktree path untouched.");
        }
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        foreach (Func<Task> operation in new Func<Task>[] { () => r.ApplyStashChangesAsync(stash, [file], cancelled.Token), () => r.RestoreStashFilesAsync(stash, [file], cancellationToken: cancelled.Token) })
        { try { await operation(); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { } }
        Check(await Snapshot(r) == before, "Pre-cancelled file operations do not mutate the repository.");
        await r.StashAsync(new GitStashSaveOptions()); before = await Snapshot(r);
        foreach (Func<Task> operation in new Func<Task>[] { () => r.ApplyStashChangesAsync(stash, [file]), () => r.RestoreStashFilesAsync(stash, [file]) })
        { try { await operation(); throw new Exception("Stale file selection accepted."); } catch (InvalidOperationException) { } Check(await Snapshot(r) == before, "Stale selections cannot act on a newer stash."); }
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Root, "bare.git"), bare: true);
        await bare.RunAsync(["fetch", r.Path, "refs/stash"]); GitStash latest = (await r.GetStashesAsync()).First();
        await bare.RunAsync(["update-ref", "--create-reflog", "-m", "Copied stash", "refs/stash", latest.Hash]); GitStash bareStash = (await bare.GetStashesAsync()).Single();
        GitRevisionFile bareFile = (await bare.GetStashChangesAsync(bareStash)).Tracked.Files.Single();
        foreach (Func<Task> operation in new Func<Task>[] { () => bare.ApplyStashChangesAsync(bareStash, [bareFile]), () => bare.RestoreStashFilesAsync(bareStash, [bareFile]) })
        { try { await operation(); throw new Exception("Bare stash file mutation accepted."); } catch (InvalidOperationException) { } }
        Check((await bare.GetStashesAsync()).Single().Hash == bareStash.Hash, "Bare rejections preserve the copied stash.");
    }
}
