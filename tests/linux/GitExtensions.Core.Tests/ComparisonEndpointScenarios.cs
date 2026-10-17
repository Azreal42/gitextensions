using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GitExtensions.Linux;

internal static class ComparisonEndpointScenarios
{
    private const string Literal = ":[file]* été\t\n.txt";
    private static readonly GitCompareRevision Index = new("", GitCompareRevisionKind.Index);
    private static readonly GitCompareRevision Worktree = new("", GitCompareRevisionKind.WorkingTree);
    public static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("typed tree keeps literal index and live bytes modes deletions symlinks and untracked files distinct", TreesAsync),
        ("typed index tree retains all conflict stages and gitlink provenance without a fabricated hash", ConflictsAsync),
        ("unborn artificial comparison reads staged and working contents without HEAD or object writes", () => UnbornAsync(sha256: false)),
        ("SHA256 unborn artificial comparison uses a typed full-width empty tree without writes", () => UnbornAsync(sha256: true)),
        ("captured empty-tree comparison stays empty after the first commit appears", EmptyTreeCaptureAsync),
        ("typed blame reads actual Commit Index and working contents with separate endpoint identity and codec", BlameAsync),
        ("typed new-file blame retains native zero lines and unborn errors instead of attributing HEAD bytes", NewFileBlameAsync),
        ("typed trees support bare commits and linked indexes without crossing worktree identity", RepositoryKindsAsync),
        ("typed tree and binary blame cancellation drain their actual native descendants", CancellationAsync),
    ];

    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static async Task<GitResult> Git(GitRepository repo, params string[] args)
    {
        GitResult result = await new GitRunner().RunAsync(repo.Path, ["--no-optional-locks", "--literal-pathspecs", .. args]);
        if (!result.Success) { throw new GitCommandException(args, result); } return result;
    }
    private static string Snapshot(string root) => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private static async Task TreesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        f.Write(Literal, "committed\n"); f.Write("deleted.txt", "deleted after index\n"); f.Write("executable.sh", "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(f.File("executable.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.CreateSymbolicLink(f.File("link"), Literal);
        }
        await r.StageAllAsync(); await r.CommitAsync("Tree fixture"); string head = await r.ResolveRevisionAsync("HEAD");
        f.Write(Literal, "index\n"); await r.StageAsync([Literal]); f.Write(Literal, "working\n");
        File.Delete(f.File("deleted.txt")); f.Write("untracked [*].txt", "untracked\n");
        string before = Snapshot(r.Path);
        IReadOnlyList<GitComparisonTreeEntry> committed = await r.GetComparisonTreeAsync(new(head));
        IReadOnlyList<GitComparisonTreeEntry> indexed = await r.GetComparisonTreeAsync(Index), live = await r.GetComparisonTreeAsync(Worktree);
        GitComparisonTreeEntry item = indexed.Single(e => e.Path == Literal);
        Check(item.Mode == "100644" && item.Hash is not null && item.IndexStages.Single().Stage == 0, "Stage zero has its real blob hash and mode.");
        Check(committed.Single(e => e.Path == Literal).Hash != item.Hash && committed.All(e => e.IndexStages.Count == 0), "Committed tree must not borrow index entries.");
        Check(indexed.Any(e => e.Path == "deleted.txt") && !live.Any(e => e.Path == "deleted.txt") && live.Any(e => e.Path == "untracked [*].txt"), "Live tree must reflect actual deletions and untracked names.");
        Check(live.All(e => e.Hash is null) && live.Single(e => e.Path == Literal).Mode == "100644", "Live entries have no invented content identity.");
        Check(Encoding.UTF8.GetString(await r.GetComparisonFileBytesAsync(new(head), Literal)) == "committed\n"
            && Encoding.UTF8.GetString(await r.GetComparisonFileBytesAsync(Index, Literal)) == "index\n"
            && Encoding.UTF8.GetString(await r.GetComparisonFileBytesAsync(Worktree, Literal)) == "working\n", "All three endpoint byte sources remain separate.");
        if (!OperatingSystem.IsWindows())
        {
            Check(indexed.Single(e => e.Path == "executable.sh").Mode == "100755" && live.Single(e => e.Path == "executable.sh").Mode == "100755", "Executable mode is retained.");
            Check(indexed.Single(e => e.Path == "link").Mode == "120000" && live.Single(e => e.Path == "link").Mode == "120000", "Symlinks remain symlinks.");
            Check(Encoding.UTF8.GetString(await r.GetComparisonFileBytesAsync(Worktree, "link")) == Literal, "Live symlink bytes are its target, not dereferenced contents.");
        }
        Check(before == Snapshot(r.Path), "Tree and endpoint reads modified repository bytes.");
    }

    private static async Task ConflictsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        string hash = await r.ResolveRevisionAsync("HEAD"), blob = (await Git(r, "rev-parse", "HEAD:seed.txt")).StandardOutput.Trim();
        string link = "module [*]\nλ";
        string records = string.Join('\0', Enumerable.Range(1, 3).Select(stage => $"100644 {blob} {stage}\t{Literal}")) + '\0'
            + string.Join('\0', Enumerable.Range(1, 3).Select(stage => $"160000 {hash} {stage}\t{link}")) + '\0';
        GitResult update = await new GitRunner().RunAsync(r.Path, ["update-index", "-z", "--index-info"], standardInput: records);
        Check(update.Success, update.StandardError); string before = Snapshot(r.Path);
        IReadOnlyList<GitComparisonTreeEntry> tree = await r.GetComparisonTreeAsync(Index);
        foreach (string path in new[] { Literal, link })
        {
            GitComparisonTreeEntry entry = tree.Single(e => e.Path == path);
            Check(entry.IsConflict && entry.Mode is null && entry.Hash is null && entry.IndexStages.Select(e => e.Stage).SequenceEqual([1, 2, 3]), "Conflict stages must not collapse into a fake stage zero.");
        }
        Check(tree.Single(e => e.Path == link).IsSubmodule && !tree.Single(e => e.Path == Literal).IsSubmodule, "Conflicted gitlinks retain their actual stage modes.");
        await Expect<GitCommandException>(() => r.GetComparisonFileBytesAsync(Index, Literal));
        Check(before == Snapshot(r.Path), "Reading conflict metadata or absent stage zero modified the index.");
    }

    private static async Task UnbornAsync(bool sha256)
    {
        using RepositoryFixture owner = await RepositoryFixture.CreateAsync(seed: false); GitRepository r = owner.Repository;
        if (sha256)
        {
            string location = Path.Combine(owner.Root, "sha256"); await Git(r, "init", "--object-format=sha256", "-b", "main", location);
            r = new(location); await RepositoryFixture.ConfigureAsync(r);
        }
        await File.WriteAllTextAsync(Path.Combine(r.Path, Literal), "staged new file\n");
        byte[] image = [137, 80, 78, 71, 13, 10, 26, 10, 0, 255]; await File.WriteAllBytesAsync(Path.Combine(r.Path, "image.png"), image);
        await r.StageAllAsync(); await File.WriteAllTextAsync(Path.Combine(r.Path, Literal), "working new file\n");
        await File.WriteAllTextAsync(Path.Combine(r.Path, "untracked.txt"), "untracked\n");
        string before = Snapshot(r.Path); GitComparison staged = await r.GetArtificialRevisionComparisonAsync(Index);
        Check(staged.Base.Kind == GitCompareRevisionKind.EmptyTree && staged.Compare.Kind == GitCompareRevisionKind.Index
            && staged.Base.Revision.Length == (sha256 ? 64 : 40) && staged.Files.All(e => e.Status == 'A') && staged.Files.Count == 2,
            "Unborn index must compare its actual staged entries to a typed empty tree, never HEAD.");
        Check((await r.GetComparisonTreeAsync(staged.Base)).Count == 0 && (await r.GetComparisonFileBytesAsync(staged.Base, Literal)).Length == 0, "Empty-tree inputs really have no entries/content.");
        Check((await r.GetComparisonFileBytesAsync(Index, "image.png")).SequenceEqual(image), "Image consumers receive exact indexed binary bytes.");
        Check((await r.GetComparisonTreeAsync(Index)).Single(e => e.Path == Literal).Hash is not null, "Unborn tree retains actual stage-zero identity.");
        string diff = await r.GetComparisonFileDiffAsync(staged, staged.Files.Single(e => e.Path == Literal));
        Check(diff.Contains("+staged new file", StringComparison.Ordinal) && !diff.Contains("working new file", StringComparison.Ordinal), "Unborn staged diff must not read live bytes.");
        IReadOnlyList<GitComparisonFileMetadata> metadata = await r.GetComparisonFileMetadataAsync(staged, staged.Base);
        Check(metadata.All(e => e.Staged == GitFileStagedStatus.Index && e.IsTracked), "Typed empty-tree parent retains Index menu provenance.");
        GitComparison working = await r.GetArtificialRevisionComparisonAsync(Worktree);
        string liveDiff = await r.GetComparisonFileDiffAsync(working, working.Files.Single(e => e.Path == Literal));
        Check(working.Base.Kind == GitCompareRevisionKind.Index && working.Compare.Kind == GitCompareRevisionKind.WorkingTree
            && liveDiff.Contains("-staged new file", StringComparison.Ordinal) && liveDiff.Contains("+working new file", StringComparison.Ordinal)
            && working.UntrackedPaths.SequenceEqual(["untracked.txt"]), "Unborn working pair must remain Index-to-working with literal untracked files.");
        Check(await r.GetComparisonMergeBaseAsync(staged.Base, Index) is null && await r.GetComparisonCommitCountsAsync(staged.Base, Index) is null, "An empty tree has no invented commit ancestry.");
        Check(before == Snapshot(r.Path), "Unborn read wrote an empty tree object, refreshed index bytes or changed source files.");
    }

    private static async Task EmptyTreeCaptureAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false); f.Write(Literal, "first commit contents\n"); await f.Repository.StageAllAsync();
        GitComparison captured = await f.Repository.GetArtificialRevisionComparisonAsync(Index);
        await f.Repository.CommitAsync("First real commit"); string before = Snapshot(f.Repository.Path);
        string patch = await f.Repository.GetComparisonFileDiffAsync(captured, captured.Files.Single());
        GitComparison again = await f.Repository.GetComparisonAsync(captured.Base, Index);
        Check(patch.Contains("+first commit contents", StringComparison.Ordinal) && again.Files.Single().Status == 'A', "A captured empty parent must not silently become the newly created HEAD.");
        GitComparison fresh = await f.Repository.GetArtificialRevisionComparisonAsync(Index);
        Check(fresh.Base.Kind == GitCompareRevisionKind.Commit && fresh.Files.Count == 0, "A fresh row resolves the newly real HEAD.");
        await Expect<ArgumentException>(() => f.Repository.RestoreComparisonFilesAsync(captured.Base, captured.Files));
        Check(before == Snapshot(f.Repository.Path), "Rereading a captured empty pair changed the repository.");
    }

    private static async Task BlameAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false); GitRepository r = f.Repository;
        Encoding codec = GitFileEncodingCodec.GetEncoding("windows-1252");
        await r.SetConfigAsync("user.name", "Åuteur Ω"); await r.SetConfigAsync("i18n.filesencoding", "windows-1252");
        File.WriteAllBytes(f.File(Literal), codec.GetBytes("stable\nCafé committed €\n")); await r.StageAllAsync(); await r.CommitAsync("Message Ω"); string hash = await r.ResolveRevisionAsync("HEAD");
        File.WriteAllBytes(f.File(Literal), codec.GetBytes("stable\nCafé indexed €\n")); await r.StageAsync([Literal]);
        File.WriteAllBytes(f.File(Literal), codec.GetBytes("stable\nCafé working €\n")); string before = Snapshot(r.Path);
        foreach ((GitCompareRevision source, string text) in new[] { (new GitCompareRevision(hash), "Café committed €"), (Index, "Café indexed €"), (Worktree, "Café working €") })
        {
            GitFileBlame blame = await r.GetComparisonFileBlameAsync(source, Literal, new(true, true, true));
            Check(blame.Source?.Kind == source.Kind && blame.Revision == (source.Kind == GitCompareRevisionKind.Commit ? hash : null), "Artificial identity must not collapse into a HEAD revision.");
            Check(blame.Lines.Count == 2 && blame.Lines[1].Line.Text == text && blame.Bytes.SequenceEqual(codec.GetBytes("stable\n" + text + "\n")), "Blame text and raw bytes must come from the selected endpoint.");
            Check(blame.Lines[0].Line.Hash == hash && blame.Lines[0].Line.AuthorName == "Åuteur Ω" && blame.Lines.All(line => line.OriginalPath == Literal), "UTF8 metadata/path must remain separate from the file codec.");
            Check(source.Kind == GitCompareRevisionKind.Commit || blame.Lines[1].Line.Hash.All(c => c == '0'), "Uncommitted lines must retain native zero attribution.");
        }
        Check(before == Snapshot(r.Path), "Typed blame changed index, configuration or worktree bytes.");
    }

    private static async Task NewFileBlameAsync()
    {
        foreach (bool unborn in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: !unborn); f.Write(Literal, "new indexed contents\n"); await f.Repository.StageAsync([Literal]);
            f.Write(Literal, "new working contents\n"); string before = Snapshot(f.Repository.Path);
            foreach (GitCompareRevision source in new[] { Index, Worktree })
            {
                if (unborn)
                {
                    GitCommandException exception = await Expect<GitCommandException>(() => f.Repository.GetComparisonFileBlameAsync(source, Literal));
                    Check(exception.Result.ExitCode == 128 && exception.Result.StandardError.Contains("no such ref: HEAD", StringComparison.Ordinal), "Unborn attribution preserves the native error, not unrelated HEAD content.");
                }
                else
                {
                    GitFileBlame blame = await f.Repository.GetComparisonFileBlameAsync(source, Literal);
                    Check(blame.Source?.Kind == source.Kind && blame.Lines.Single().Line.Hash.All(c => c == '0')
                        && blame.Lines[0].Line.Text == (source.Kind == GitCompareRevisionKind.Index ? "new indexed contents" : "new working contents"),
                        "A new indexed path has native zero attribution using the requested endpoint contents, never HEAD bytes.");
                }
            }
            Check(before == Snapshot(f.Repository.Path), "Failed new-file attribution changed repository bytes.");
        }
    }

    private static async Task RepositoryKindsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        string barePath = Path.Combine(f.Root, "bare.git"); await Git(r, "clone", "--bare", "--no-hardlinks", r.Path, barePath);
        GitRepository bare = new(barePath); string before = Snapshot(barePath);
        Check((await bare.GetComparisonTreeAsync(new())).Single().Path == "seed.txt", "Committed trees are available without an index/worktree.");
        await Expect<InvalidOperationException>(() => bare.GetArtificialRevisionComparisonAsync(Index));
        Check(before == Snapshot(barePath), "Bare tree reads altered repository bytes.");
        string linkedPath = Path.Combine(f.Root, "linked"); await Git(r, "worktree", "add", "-b", "linked", linkedPath);
        GitRepository linked = new(linkedPath); await File.WriteAllTextAsync(Path.Combine(linkedPath, "seed.txt"), "linked indexed\n"); await linked.StageAsync(["seed.txt"]);
        await File.WriteAllTextAsync(Path.Combine(linkedPath, "seed.txt"), "linked live\n"); before = Snapshot(r.Path);
        GitComparison pair = await linked.GetArtificialRevisionComparisonAsync(Index);
        Check(pair.Files.Single().Path == "seed.txt" && Encoding.UTF8.GetString(await linked.GetComparisonFileBytesAsync(Index, "seed.txt")) == "linked indexed\n"
            && Encoding.UTF8.GetString(await r.GetComparisonFileBytesAsync(Index, "seed.txt")) == "seed\n", "Linked rows must use their own index rather than the primary one.");
        Check(before == Snapshot(r.Path), "Linked reads changed the shared administrative repository/index.");
    }

    private static async Task CancellationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitRepository r = f.Repository;
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Expect<OperationCanceledException>(() => r.GetComparisonTreeAsync(Index, cancelled.Token));
        await Expect<OperationCanceledException>(() => r.GetArtificialRevisionComparisonAsync(Index, cancelled.Token));
        await Expect<OperationCanceledException>(() => r.GetComparisonFileBlameAsync(Index, "seed.txt", cancellationToken: cancelled.Token));
        if (!OperatingSystem.IsLinux()) { return; }
        foreach (string command in new[] { "ls-files", "blame" })
        {
            string marker = Path.Combine(f.Root, command + "-pids"), wrapper = Path.Combine(f.Root, command + "-git");
            File.WriteAllText(wrapper, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = " + Quote(command) + " ]; then\n  sleep 60 &\n  child=$!\n  printf '%s %s\\n' \"$$\" \"$child\" > " + Quote(marker) + "\n  wait \"$child\"\n fi\ndone\nexec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GitRepository guarded = new(r.Path, new GitRunner(wrapper)); string before = Snapshot(r.Path); using CancellationTokenSource token = new();
            Task pending = command == "blame" ? guarded.GetComparisonFileBlameAsync(Index, "seed.txt", cancellationToken: token.Token) : guarded.GetComparisonTreeAsync(Index, token.Token);
            try
            {
                Stopwatch watch = Stopwatch.StartNew(); while ((!File.Exists(marker) || new FileInfo(marker).Length == 0) && !pending.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
                Check(File.Exists(marker) && new FileInfo(marker).Length > 0, "The actual typed reader did not reach its native gate.");
                int[] pids = File.ReadAllText(marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); token.Cancel();
                await Expect<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Check(pids.All(pid => !LinuxProcessState.IsRunning(pid)) && before == Snapshot(r.Path), "Cancellation returned before actual process drainage or changed repository bytes.");
            }
            finally { token.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
        }
    }
}
