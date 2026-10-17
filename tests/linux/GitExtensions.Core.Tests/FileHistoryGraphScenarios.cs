using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GitExtensions.Linux;

internal static class FileHistoryGraphScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("Browse file history discovers former names then rewrites graph parents without a selection bound", RenamesAsync),
        ("Browse file history keeps all/current/branch scopes and native sort/paging/filter semantics", ScopeAndQueryAsync),
        ("Browse literal NUL names preserve leading LF glob characters quotes Unicode and hash-like paths", LiteralPathsAsync),
        ("Browse directory histories do not follow renames and preserve subtree filters", DirectoryAsync),
        ("Browse rename options retain exact-only and no-follow semantics", RenameOptionsAsync),
        ("Browse file history retains deleted files and genuinely empty histories", DeletedAndEmptyAsync),
        ("Browse file graph works in bare linked unborn and SHA256 repositories without writes", RepositoryKindsAsync),
        ("Browse graph discovery and graph reads disable optional locks and retain ordinary GetLog output", ReadonlyAndArgumentsAsync),
        ("Browse graph validates paths revisions and cancellation without widening scope", InvalidAsync),
        ("Browse graph cancellation drains the actual rename-discovery reader", () => CancellationAsync(discovery: true)),
        ("Browse graph cancellation drains the actual graph reader", () => CancellationAsync(discovery: false)),
        ("Browse orphan HEAD retains other reference histories without inventing a HEAD-followed path map", OrphanHeadAsync),
    ];
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<GitResult> Git(GitRepository repo, params string[] args)
    {
        GitResult result = await new GitRunner().RunAsync(repo.Path, ["--no-optional-locks", "--literal-pathspecs", .. args]);
        if (!result.Success) { throw new GitCommandException(args, result); } return result;
    }
    private static async Task<string> Hash(GitRepository repo) => (await Git(repo, "rev-parse", "HEAD")).StandardOutput.Trim();
    private static async Task<string> Commit(RepositoryFixture fixture, string subject, int year)
    {
        await fixture.Repository.StageAllAsync();
        GitResult result = await new GitRunner().RunAsync(fixture.Repository.Path, ["commit", "-qm", subject], environment: new Dictionary<string, string?>
        { ["GIT_AUTHOR_DATE"] = $"{year}-01-01T12:00:00+00:00", ["GIT_COMMITTER_DATE"] = $"{year}-01-01T12:00:00+00:00" });
        Check(result.Success, result.StandardError); return await Hash(fixture.Repository);
    }
    private sealed record Fixture(RepositoryFixture Owner, string Root, string Rename, string Tip, string OldPath, string Path) : IDisposable
    {
        internal GitRepository Repo => Owner.Repository;
        public void Dispose() => Owner.Dispose();
    }
    private static async Task<Fixture> Create(string oldPath = "old.txt", string path = "new.txt", bool editAtRename = false)
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
        f.Write(oldPath, string.Join('\n', Enumerable.Range(0, 40).Select(i => "stable line " + i)) + "\n");
        string root = await Commit(f, "Original tracked file", 2001);
        f.Write("noise.txt", "unrelated\n"); await Commit(f, "Unrelated intermediate commit", 2002);
        await Git(f.Repository, "mv", "--", oldPath, path);
        if (editAtRename) { File.AppendAllText(f.File(path), "added during rename\n"); }
        string rename = await Commit(f, "Rename tracked file", 2003);
        File.AppendAllText(f.File(path), "selected future content\n"); string tip = await Commit(f, "Future file modification", 2004);
        return new(f, root, rename, tip, oldPath, path);
    }
    private static async Task RenamesAsync()
    {
        using Fixture f = await Create(); string before = Snapshot(f.Repo.Path);
        GitFileHistoryGraph graph = await f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path));
        Check(graph.Commits.Select(c => c.Hash).SequenceEqual([f.Tip, f.Rename, f.Root]), "Selection must not act as an ancestor bound or include unrelated commits.");
        Check(graph.Paths.SequenceEqual([f.Path, f.OldPath]) && graph.PathByCommit[f.Root] == f.OldPath && graph.PathByCommit[f.Rename] == f.Path,
            "The separate HEAD-followed names retain the complete old/new names and first name per commit.");
        Check(graph.Commits.Single(c => c.Hash == f.Rename).Parents.SequenceEqual([f.Root]), "Native --parents must bypass an omitted unrelated commit.");
        Check(Snapshot(f.Repo.Path) == before, "Graph reads changed repository/index/working bytes.");
    }
    private static async Task ScopeAndQueryAsync()
    {
        using Fixture f = await Create(); await Git(f.Repo, "checkout", "-qb", "side", f.Root);
        File.AppendAllText(f.Owner.File(f.OldPath), "only the divergent branch\n"); string side = await Commit(f.Owner, "Divergent file modification", 2010);
        await Git(f.Repo, "checkout", "main");
        GitFileHistoryGraph all = await f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path));
        Check(all.Commits.Select(c => c.Hash).ToHashSet().SetEquals([f.Root, f.Rename, f.Tip, side]), "All branches must include a divergent former-name modification.");
        GitFileHistoryGraph current = await f.Repo.GetFileHistoryGraphAsync(new(All: false, Path: f.Path));
        Check(current.Commits.Select(c => c.Hash).SequenceEqual([f.Tip, f.Rename, f.Root]), "Current scope must not acquire other refs.");
        GitFileHistoryGraph branch = await f.Repo.GetFileHistoryGraphAsync(new GitLogQuery(Path: f.Path) { BranchFilter = "side" });
        Check(branch.Commits.Select(c => c.Hash).SequenceEqual([side, f.Root]), "The accepted branch expression must remain authoritative.");
        Check(!branch.PathByCommit.ContainsKey(side), "HEAD-only discovery must not invent a per-commit mapping for another branch.");
        foreach (GitLogSort sort in Enum.GetValues<GitLogSort>())
        {
            GitFileHistoryGraph graph = await f.Repo.GetFileHistoryGraphAsync(new GitLogQuery(Path: f.Path) { Sort = sort });
            string[] order = sort switch { GitLogSort.Topology => ["--topo-order"], GitLogSort.AuthorDate => ["--author-date-order"], GitLogSort.Date => ["--date-order"], _ => [] };
            string[] expected = (await Git(f.Repo, ["log", "--parents", "--format=%H", .. order, "--all", "--", f.Path, f.OldPath])).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(graph.Commits.Select(c => c.Hash).SequenceEqual(expected), "Graph reader native sort mismatch: " + sort);
            GitFileHistoryGraph page = await f.Repo.GetFileHistoryGraphAsync(new GitLogQuery(Count: 1, Skip: 1, Path: f.Path) { Sort = sort });
            Check(page.Commits.Select(c => c.Hash).SequenceEqual(expected.Skip(1).Take(1)) && page.Paths.Contains(f.OldPath), "Pagination truncated rename discovery or changed graph order.");
        }
        GitFileHistoryGraph filtered = await f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path, Message: "Future", Author: "Linux Test", Since: DateTimeOffset.Parse("2004-01-01", CultureInfo.InvariantCulture)));
        Check(filtered.Commits.Select(c => c.Hash).SequenceEqual([f.Tip]), "Header/date filters were discarded by the path expansion.");
        GitFileHistoryGraph overridden = await f.Repo.GetFileHistoryGraphAsync(new GitLogQuery(Count: 1, Path: f.Path) { BranchFilter = "--max-count=2 main" });
        Check(overridden.Commits.Count == 2, "Original branch-option precedence must survive graph reuse.");
    }
    private static async Task LiteralPathsAsync()
    {
        foreach ((string oldPath, string path) in new[] { ("\nold [*]?;$()'é.txt", "\nnew [*]?;$()'\"é.txt"), (new string('b', 40), new string('a', 40)) })
        {
            using Fixture f = await Create(oldPath, path);
            f.Owner.Write("unrelated-matching-name.txt", "unrelated\n"); await Commit(f.Owner, "Unrelated literal file", 2005);
            GitFileHistoryGraph graph = await f.Repo.GetFileHistoryGraphAsync(new(Path: path));
            Check(graph.Paths.SequenceEqual([path, oldPath]) && graph.PathByCommit[f.Root] == oldPath && graph.Commits.Count == 3,
                "NUL pathname parsing trimmed literal LF, interpreted a glob, confused a hash-like file or lost quoting/Unicode.");
        }
    }
    private static async Task DirectoryAsync()
    {
        using Fixture f = await Create("folder/old.txt", "folder/new.txt");
        f.Owner.Write("elsewhere/outside.txt", "outside\n"); await Commit(f.Owner, "Outside directory", 2005);
        GitFileHistoryGraph graph = await f.Repo.GetFileHistoryGraphAsync(new(Path: "folder/"), new(IsDirectory: true));
        Check(graph.Paths.SequenceEqual(["folder/"]) && graph.PathByCommit.Count == 0 && graph.Commits.Select(c => c.Hash).SequenceEqual([f.Tip, f.Rename, f.Root]),
            "A folder must retain its subtree graph without pretending it is one followed file.");
        GitFileHistoryGraph empty = await f.Repo.GetFileHistoryGraphAsync(new(Path: "absent/folder/"), new(IsDirectory: true));
        Check(empty.Commits.Count == 0 && empty.Paths.SequenceEqual(["absent/folder/"]), "An empty directory filter must not widen to the repository.");
    }
    private static async Task RenameOptionsAsync()
    {
        using Fixture f = await Create(editAtRename: true);
        GitFileHistoryGraph normal = await f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path));
        GitFileHistoryGraph exact = await f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path), new(ExactRenames: true));
        GitFileHistoryGraph disabled = await f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path), new(FollowRenames: false));
        Check(normal.Paths.Contains(f.OldPath) && normal.Commits.Any(c => c.Hash == f.Root), "Ordinary rename similarity was not followed.");
        Check(exact.Paths.SequenceEqual([f.Path]) && disabled.Paths.SequenceEqual([f.Path]) && exact.Commits.Count == 2 && disabled.Commits.Count == 2,
            "Exact-only/no-follow must not cross an inexact rename.");
    }
    private static async Task DeletedAndEmptyAsync()
    {
        using Fixture f = await Create(); await Git(f.Repo, "rm", "--", f.Path); string deleted = await Commit(f.Owner, "Delete tracked file", 2005);
        GitFileHistoryGraph graph = await f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path));
        Check(graph.Commits.Select(c => c.Hash).SequenceEqual([deleted, f.Tip, f.Rename, f.Root]) && graph.Paths.Contains(f.OldPath), "HEAD deletion must retain former-name history.");
        f.Owner.Write("never-committed.txt", "untracked\n");
        GitFileHistoryGraph missing = await f.Repo.GetFileHistoryGraphAsync(new(Path: "never-committed.txt"));
        Check(missing.Commits.Count == 0 && missing.Paths.SequenceEqual(["never-committed.txt"]), "A new file must stay genuinely empty without fake HEAD history.");
    }
    private static async Task RepositoryKindsAsync()
    {
        using Fixture f = await Create();
        string bare = Path.Combine(f.Owner.Root, "bare.git"), linked = Path.Combine(f.Owner.Root, "linked");
        await Git(f.Repo, "clone", "--bare", f.Repo.Path, bare); await Git(f.Repo, "worktree", "add", "--detach", linked, "HEAD");
        foreach (string path in new[] { bare, linked })
        {
            GitRepository repo = await GitRepository.OpenAsync(path); string before = Snapshot(f.Owner.Root);
            GitFileHistoryGraph graph = await repo.GetFileHistoryGraphAsync(new(Path: f.Path));
            Check(graph.Commits.Select(c => c.Hash).SequenceEqual([f.Tip, f.Rename, f.Root]) && Snapshot(f.Owner.Root) == before, "Bare/linked history is not an unchanged read.");
        }
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(false);
        Check((await unborn.Repository.GetFileHistoryGraphAsync(new(Path: "new.txt"))).Commits.Count == 0, "Unborn history must be empty without a fabricated commit.");
        string sha = Path.Combine(f.Owner.Root, "sha256"); await Git(f.Repo, "init", "--object-format=sha256", "-b", "main", sha);
        GitRepository shaRepo = new(sha); await RepositoryFixture.ConfigureAsync(shaRepo);
        await File.WriteAllTextAsync(Path.Combine(sha, "old.txt"), "sha256\n"); await shaRepo.StageAllAsync(); await shaRepo.CommitAsync("SHA256 original");
        await Git(shaRepo, "mv", "old.txt", "new.txt"); await shaRepo.CommitAsync("SHA256 rename");
        GitFileHistoryGraph shaGraph = await shaRepo.GetFileHistoryGraphAsync(new(Path: "new.txt"));
        Check(shaGraph.Commits.Count == 2 && shaGraph.Commits.All(c => c.Hash.Length == 64) && shaGraph.Paths.SequenceEqual(["new.txt", "old.txt"]), "SHA256 headers or former names were truncated.");
    }
    private static async Task ReadonlyAndArgumentsAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Create(); string trace = Path.Combine(f.Owner.Root, "arguments"), wrapper = Path.Combine(f.Owner.Root, "record-git");
        File.WriteAllText(wrapper, "#!/bin/sh\nprintf 'RECORD\\0' >> " + Quote(trace) + "\nprintf '%s\\0' \"$@\" >> " + Quote(trace) + "\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        f.Owner.Write(f.Path, "dirty unstaged\n"); f.Owner.Write("untracked.txt", "untracked\n");
        string before = Snapshot(f.Repo.Path); GitRepository recording = new(f.Repo.Path, new GitRunner(wrapper));
        await recording.GetFileHistoryGraphAsync(new(Path: f.Path));
        string[] commands = File.ReadAllText(trace).Split("RECORD\0", StringSplitOptions.RemoveEmptyEntries);
        Check(commands.All(command => command.Contains("--no-optional-locks\0", StringComparison.Ordinal))
            && commands.Count(command => command.Contains("--follow\0", StringComparison.Ordinal)) == 1
            && commands.Count(command => command.Contains("--parents\0", StringComparison.Ordinal)) == 1, "Discovery and graph are not separate readonly native commands.");
        Check(Snapshot(f.Repo.Path) == before, "A readonly graph refreshed index bytes or changed repository content.");
        IReadOnlyList<GitCommit> ordinary = await f.Repo.GetLogAsync(new GitLogQuery(Path: f.Path));
        Check(ordinary.Select(c => c.Hash).SequenceEqual([f.Tip, f.Rename]), "Ordinary GetLog callers acquired the special former-name semantics.");
    }
    private static async Task InvalidAsync()
    {
        using Fixture f = await Create(); string before = Snapshot(f.Repo.Path);
        foreach (string? path in new[] { null, "", "../outside", "bad\0path" })
        { await Expect<ArgumentException>(() => f.Repo.GetFileHistoryGraphAsync(new(Path: path))); }
        await Expect<GitCommandException>(() => f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path, Revision: "missing-private-ref")));
        using CancellationTokenSource token = new(); token.Cancel();
        await Expect<OperationCanceledException>(() => f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path), cancellationToken: token.Token));
        Check(Snapshot(f.Repo.Path) == before, "Invalid/cancelled reads changed repository files.");
    }
    private static async Task OrphanHeadAsync()
    {
        using Fixture f = await Create(); await Git(f.Repo, "checkout", "--orphan", "unborn-with-other-refs");
        string before = Snapshot(f.Repo.Path);
        GitFileHistoryGraph all = await f.Repo.GetFileHistoryGraphAsync(new(Path: f.Path));
        Check(all.Commits.Select(c => c.Hash).SequenceEqual([f.Tip, f.Rename]) && all.Paths.SequenceEqual([f.Path]) && all.PathByCommit.Count == 0,
            "An unborn HEAD does not erase real --all refs, and cannot supply a HEAD-followed rename map.");
        GitFileHistoryGraph branch = await f.Repo.GetFileHistoryGraphAsync(new GitLogQuery(Path: f.Path) { BranchFilter = "main" });
        Check(branch.Commits.Select(c => c.Hash).SequenceEqual([f.Tip, f.Rename]), "An explicit valid branch must survive an orphan checkout.");
        GitFileHistoryGraph current = await f.Repo.GetFileHistoryGraphAsync(new(All: false, Path: f.Path));
        Check(current.Commits.Count == 0 && before == Snapshot(f.Repo.Path), "Only the genuinely absent current HEAD has an empty history; reads must not change orphan/index bytes.");
    }
    private static async Task CancellationAsync(bool discovery)
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Create(); string wrapper = Path.Combine(f.Owner.Root, "gated-git"), marker = Path.Combine(f.Owner.Root, "pids");
        string trigger = discovery ? "--follow" : "--parents";
        File.WriteAllText(wrapper, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = " + Quote(trigger) + " ]; then\n  sleep 60 &\n  child=$!\n  printf '%s %s\\n' \"$$\" \"$child\" > " + Quote(marker) + "\n  wait \"$child\"\n fi\ndone\nexec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using CancellationTokenSource token = new(); GitRepository guarded = new(f.Repo.Path, new GitRunner(wrapper)); string before = Snapshot(f.Repo.Path);
        Task<GitFileHistoryGraph> pending = guarded.GetFileHistoryGraphAsync(new(Path: f.Path), cancellationToken: token.Token);
        try
        {
            Stopwatch timer = Stopwatch.StartNew(); while (!File.Exists(marker) && timer.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
            Check(File.Exists(marker), "The actual native reader did not reach its private barrier.");
            int[] pids = File.ReadAllText(marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(); token.Cancel();
            await Expect<OperationCanceledException>(() => pending);
            Check(pids.All(pid => !LinuxProcessState.IsRunning(pid)) && Snapshot(f.Repo.Path) == before, "Cancellation returned before reader/descendant drainage or changed repository bytes.");
        }
        finally { token.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Snapshot(string root)
        => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
}
