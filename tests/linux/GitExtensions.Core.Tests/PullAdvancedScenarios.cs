using System.Globalization;
using System.Text.Json;
using GitExtensions.Linux;

internal static class PullAdvancedScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("advanced fetch follows remote tagopt and explicit no-tags overrides reachable tags", TagChoicesAsync),
        ("advanced fetch all-tags reaches unrelated tags and explicit prune-tags removes local-only tags", PruneTagsAsync),
        ("download full history fetch removes a real file-transport shallow boundary", UnshallowFetchAsync),
        ("download full history pull retains explicit mode and original follow-tag default", UnshallowPullAsync),
        ("invalid advanced options and pre-cancellation preserve refs index files config and stash", ValidationAsync),
        ("manual pull auto-stash saves tracked staged and unstaged changes without popping", TrackedAutoStashAsync),
        ("auto-stash includes untracked files only when its independent preference requests it", IncludeUntrackedAsync),
        ("untracked-only work does not trigger auto-stash even when inclusion is requested", UntrackedOnlyAsync),
        ("dirty submodule work does not trigger the original tracked auto-stash decision", DirtySubmoduleAsync),
        ("failed pull retains its saved stash and useful Git error without changing HEAD", FailedPullAsync),
        ("rebase conflicts retain auto-stash for recovery and never automatically apply it", RebaseConflictAsync),
        ("cancellation after saving local work stops pull and retains the recoverable stash", CancellationAfterStashAsync)
    ];

    private static async Task TagChoicesAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Git(f.Writer, "tag", "reachable"); await Git(f.Writer, "push", "origin", "--tags");
        await f.Local.SetConfigAsync("remote.origin.tagopt", "--no-tags");
        byte[] config = File.ReadAllBytes(f.Seed.File(".git/config"));
        await f.Local.FetchRemoteAsync("origin", new GitFetchOptions());
        Check(!await HasRef(f.Local, "refs/tags/reachable"), "Follow must honor remote.origin.tagopt without imposing a flag.");
        await f.Local.SetConfigAsync("remote.origin.tagopt", "--tags");
        await f.Local.FetchRemoteAsync("origin", new GitFetchOptions(GitFetchTags.None));
        Check(!await HasRef(f.Local, "refs/tags/reachable"), "Explicit no-tags overrides the remote's tagopt.");
        await f.Local.FetchRemoteAsync("origin", new GitFetchOptions());
        Check(await HasRef(f.Local, "refs/tags/reachable"), "Follow honors the changed tagopt and fetches the tag.");
        await f.Local.SetConfigAsync("remote.origin.tagopt", "--no-tags");
        Check(File.ReadAllBytes(f.Seed.File(".git/config")).AsSpan().SequenceEqual(config), "Fetch never rewrites parallelism, tag or prune settings.");
        await Git(f.Local, "tag", "--delete", "reachable");
        GitPullExecutionResult explicitPull = await f.Local.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new(GitFetchTags.All));
        Check(explicitPull.Result.Success && explicitPull.Arguments.Contains("--tags") && await HasRef(f.Local, "refs/tags/reachable"), "The original public PullCmd accepts an explicit all-tags request even though its Merge/Rebase UI disables that choice.");
    }

    private static async Task PruneTagsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Git(f.Writer, "checkout", "--orphan", "unrelated");
        await Git(f.Writer, "rm", "-rf", ".");
        await Commit(f.Writer, "unrelated.txt", "unreachable tag content\n", "Unrelated tag");
        await Git(f.Writer, "tag", "unreachable"); await Git(f.Writer, "push", "origin", "refs/tags/unreachable");
        await Git(f.Writer, "checkout", "main");
        await f.Local.FetchRemoteAsync("origin", new GitFetchOptions());
        Check(!await HasRef(f.Local, "refs/tags/unreachable"), "Default tag following does not fetch an unrelated history.");
        await f.Local.FetchAllAsync(new GitFetchOptions(GitFetchTags.All));
        Check(await HasRef(f.Local, "refs/tags/unreachable"), "All tags reaches unrelated tag objects.");
        await Git(f.Local, "tag", "local-only");
        await f.Local.FetchAllAsync(new GitFetchOptions(GitFetchTags.All, Prune: true));
        Check(await HasRef(f.Local, "refs/tags/local-only"), "Branch pruning alone preserves local tags.");
        await f.Local.FetchAllAsync(new GitFetchOptions(GitFetchTags.All, Prune: true, PruneTags: true));
        Check(!await HasRef(f.Local, "refs/tags/local-only") && await HasRef(f.Local, "refs/tags/unreachable"), "Explicit prune-tags removes only tags absent from the selected remotes.");
        await Git(f.Local, "tag", "local-after-no-tags");
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private Linux argument recording fixture."); }
        string trace = Path.Combine(f.Seed.Root, "fetch-arguments.jsonl"), helper = trace + ".py";
        File.WriteAllText(helper, "#!/usr/bin/python3\nimport json,os,sys\nwith open(" + JsonSerializer.Serialize(trace) + ",'a') as f: f.write(json.dumps(sys.argv[1:])+'\\n')\nos.execvp('git',['git']+sys.argv[1:])\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository recorded = new(f.Local.Path, new GitRunner(helper));
        await recorded.FetchRemoteAsync("origin", new GitFetchOptions(GitFetchTags.None, PruneTags: true));
        string[] actual = File.ReadAllLines(trace).Select(line => JsonSerializer.Deserialize<string[]>(line)!).Single(arguments => arguments.Contains("fetch"));
        Check(actual.Contains("--no-tags") && actual.Contains("--prune") && actual.Contains("--force") && actual.Contains("--prune-tags") && !actual.Contains("--tags"), "PruneTags alone implies prune/force but must preserve a NoTags override in the real ArgumentList.");
        Check(!await HasRef(f.Local, "refs/tags/local-after-no-tags") && await HasRef(f.Local, "refs/tags/unreachable"), "Native prune-tags works with a subsequent NoTags choice and itself implies branch pruning, as the original argument builder does.");
    }

    private static async Task UnshallowFetchAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.AdvanceAsync("remote1.txt"); await f.AdvanceAsync("remote2.txt");
        GitRepository shallow = await f.ShallowAsync();
        Check(await shallow.IsShallowRepositoryAsync(), "The fixture must have a real shallow boundary.");
        string head = await shallow.ResolveRevisionAsync("HEAD");
        await shallow.FetchRemoteAsync("origin", new GitFetchOptions(Unshallow: true));
        Check(!await shallow.IsShallowRepositoryAsync(), "--unshallow downloads the complete history.");
        Check((await Git(shallow, "rev-list", "--count", "HEAD")).StandardOutput.Trim() == "3", "All three commits must be readable.");
        Check(await shallow.ResolveRevisionAsync("HEAD") == head, "Fetch does not move the checkout.");
    }

    private static async Task UnshallowPullAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.AdvanceAsync("remote1.txt"); GitRepository shallow = await f.ShallowAsync();
        string remote = await f.AdvanceAsync("remote2.txt");
        GitPullExecutionResult result = await shallow.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new(Unshallow: true));
        Check(result.Result.Success && !await shallow.IsShallowRepositoryAsync(), result.Result.StandardError);
        Check(await shallow.ResolveRevisionAsync("HEAD") == remote, "Pull advances HEAD and downloads its ancestors.");
        Check(result.Arguments.Contains("--unshallow") && !result.Arguments.Contains("--tags") && !result.Arguments.Contains("--no-tags"), "Follow-tag pull must never gain an accidental tag override.");
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        f.Seed.Write("seed.txt", "dirty tracked\n"); f.Seed.Write("untracked.txt", "untouched\n");
        Snapshot before = await Capture(f.Local);
        await Expect<InvalidOperationException>(() => f.Local.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new(Unshallow: true, AutoStash: true)));
        await Expect<ArgumentOutOfRangeException>(() => f.Local.FetchRemoteAsync("origin", new GitFetchOptions((GitFetchTags)999)));
        await Expect<ArgumentOutOfRangeException>(() => f.Local.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new((GitFetchTags)999, AutoStash: true)));
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Expect<OperationCanceledException>(() => f.Local.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new(AutoStash: true), cancelled.Token));
        await Same(f.Local, before);
        Check((await f.Local.GetStashesAsync()).Count == 0, "Validation/cancellation must precede the stash mutation.");
    }

    private static async Task TrackedAutoStashAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string remote = await f.AdvanceAsync("remote.txt");
        f.Seed.Write("seed.txt", "staged local\n"); await f.Local.StageAsync(["seed.txt"]);
        f.Seed.Write("seed.txt", "staged local\nunstaged local\n"); f.Seed.Write("untracked.txt", "keep me\n");
        await f.Local.SetConfigAsync("fetch.parallel", "3"); await f.Local.SetConfigAsync("submodule.fetchjobs", "2");
        byte[] config = File.ReadAllBytes(f.Seed.File(".git/config"));
        GitPullExecutionResult result = await f.Local.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new(AutoStash: true));
        Check(result.Result.Success && result.AutoStash is not null, result.Result.StandardError);
        Check(await f.Local.ResolveRevisionAsync("HEAD") == remote, "The pull completes before an optional pop.");
        Check(File.ReadAllText(f.Seed.File("seed.txt")) == "seed\n" && File.ReadAllText(f.Seed.File("untracked.txt")) == "keep me\n", "Saved tracked work is absent until the host offers pop; untracked bytes remain.");
        Check((await Git(f.Local, "show", result.AutoStash!.Hash + ":seed.txt")).StandardOutput.Contains("unstaged local", StringComparison.Ordinal), "The saved stash retains the whole worktree change.");
        Check((await Git(f.Local, "show", result.AutoStash.Hash + "^2:seed.txt")).StandardOutput == "staged local\n", "The stash also retains the original index version.");
        Check(!result.Arguments.Contains("--autostash") && File.ReadAllBytes(f.Seed.File(".git/config")).AsSpan().SequenceEqual(config), "The original manual workflow must not override Git configuration.");
        await f.Local.ApplyStashAsync(result.AutoStash, pop: true);
        Check(File.ReadAllText(f.Seed.File("seed.txt")).Contains("unstaged local", StringComparison.Ordinal) && (await f.Local.GetStashesAsync()).Count == 0, "Explicit pop restores the saved work only after approval.");
    }

    private static async Task IncludeUntrackedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); f.Seed.Write("seed.txt", "tracked\n");
        const string literal = "[untracked]* Ω.txt"; f.Seed.Write(literal, "literal untracked\n");
        GitPullExecutionResult result = await f.Local.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new(AutoStash: true, IncludeUntrackedInAutoStash: true));
        Check(result.Result.Success && result.AutoStash is not null && !File.Exists(f.Seed.File(literal)), "The independent inclusion flag stores untracked work when tracked work triggers a stash.");
        Check((await Git(f.Local, "show", result.AutoStash!.Hash + "^3:" + literal)).StandardOutput == "literal untracked\n", "Untracked literal filename and bytes survive in the stash's third parent.");
        await f.Local.ApplyStashAsync(result.AutoStash, pop: true);
        Check(File.ReadAllText(f.Seed.File(literal)) == "literal untracked\n", "Pop restores that literal file.");
    }

    private static async Task UntrackedOnlyAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); f.Seed.Write("untracked.txt", "untouched\n");
        GitPullExecutionResult result = await f.Local.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new(AutoStash: true, IncludeUntrackedInAutoStash: true));
        Check(result.Result.Success && result.AutoStash is null && (await f.Local.GetStashesAsync()).Count == 0, "Untracked-only status must not trigger auto-stash.");
        Check(File.ReadAllText(f.Seed.File("untracked.txt")) == "untouched\n", "The untracked file remains byte-exact.");
    }

    private static async Task DirtySubmoduleAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string modulePath = Path.Combine(f.Seed.Root, "module"); GitRepository module = await GitRepository.InitAsync(modulePath);
        await RepositoryFixture.ConfigureAsync(module); await Commit(module, "file.txt", "module\n", "Module initial");
        await Git(f.Local, "-c", "protocol.file.allow=always", "submodule", "add", "--", modulePath, "modules/literal [one]");
        await f.Local.StageAllAsync(); await f.Local.CommitAsync("Add module"); await f.Local.PushAsync("origin", "main");
        string path = f.Seed.File("modules/literal [one]/file.txt"); File.WriteAllText(path, "dirty module\n");
        GitPullExecutionResult result = await f.Local.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new(AutoStash: true));
        Check(result.Result.Success && result.AutoStash is null && (await f.Local.GetStashesAsync()).Count == 0, result.Result.StandardError);
        Check(File.ReadAllText(path) == "dirty module\n", "Dirty submodule work is ignored by the auto-stash decision and retained.");
    }

    private static async Task FailedPullAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); f.Seed.Write("seed.txt", "recover tracked\n");
        string head = await f.Local.ResolveRevisionAsync("HEAD");
        GitPullExecutionResult result = await f.Local.PullWithOptionsAsync(GitPullMode.Merge, "origin", "missing-private-branch", new(AutoStash: true));
        Check(!result.Result.Success && result.AutoStash is not null && result.Result.StandardError.Length > 0, "A pull error returns useful Git output and its saved stash.");
        Check(await f.Local.ResolveRevisionAsync("HEAD") == head && (await f.Local.GetStashesAsync()).Single().Hash == result.AutoStash!.Hash, "Failure retains HEAD and the recoverable stash.");
        Check(File.ReadAllText(f.Seed.File("seed.txt")) == "seed\n", "Failed pull must not silently apply the stash.");
    }

    private static async Task RebaseConflictAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Commit(f.Local, "seed.txt", "local divergent\n", "Local divergence");
        await Commit(f.Writer, "seed.txt", "remote divergent\n", "Remote divergence"); await f.Writer.PushAsync("origin", "main");
        f.Seed.Write("saved.txt", "saved staged work\n"); await f.Local.StageAsync(["saved.txt"]);
        GitPullExecutionResult result = await f.Local.PullWithOptionsAsync(GitPullMode.Rebase, "origin", "main", new(AutoStash: true));
        Check(!result.Result.Success && result.AutoStash is not null && (await f.Local.GetStatusAsync()).Changes.Any(change => change.IsConflict), "Conflicting rebase keeps its operation and stash available for recovery.");
        Check((await f.Local.GetStashesAsync()).Single().Hash == result.AutoStash!.Hash && !File.Exists(f.Seed.File("saved.txt")), "No automatic pop may mix saved work into a conflicting rebase.");
        await f.Local.ManageOperationAsync("rebase", GitOperationAction.Abort);
    }

    private static async Task CancellationAfterStashAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Linux private executable cancellation scenario."); }
        using Fixture f = await Fixture.CreateAsync(); f.Seed.Write("seed.txt", "recover cancellation\n");
        string marker = Path.Combine(f.Seed.Root, "pull-started"); string script = marker + ".py";
        File.WriteAllText(script, "#!/usr/bin/python3\nimport os,pathlib,sys,time\nif 'pull' in sys.argv[1:]:\n pathlib.Path(" + JsonSerializer.Serialize(marker) + ").write_text(str(os.getpid()))\n time.sleep(30)\nos.execvp('git',['git']+sys.argv[1:])\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository slow = new(f.Local.Path, new GitRunner(script));
        string head = await f.Local.ResolveRevisionAsync("HEAD"); string refs = (await Git(f.Local, "for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput;
        using CancellationTokenSource cancellation = new();
        Task operation = slow.PullWithOptionsAsync(GitPullMode.Merge, "origin", "main", new(AutoStash: true), cancellation.Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker)) { await Task.Delay(20, timeout.Token); }
        int pid = int.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture); cancellation.Cancel();
        await Expect<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        Check(!LinuxProcessState.IsRunning(pid), "Cancelled pull must terminate its Git process.");
        Check(await f.Local.ResolveRevisionAsync("HEAD") == head && (await Git(f.Local, "for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput == refs + (await StashRefSuffix(f.Local)), "Cancellation must retain the checkout and only the newly saved stash ref.");
        GitStash stash = (await f.Local.GetStashesAsync()).Single();
        Check((await Git(f.Local, "show", stash.Hash + ":seed.txt")).StandardOutput == "recover cancellation\n", "Cancelled work remains recoverable in the stash.");
    }

    private static async Task<string> StashRefSuffix(GitRepository repository)
        => (await Git(repository, "for-each-ref", "--format=%(refname) %(objectname)", "refs/stash")).StandardOutput;
    private sealed record Snapshot(string Head, string Index, byte[] Config, string Refs, Dictionary<string, byte[]> Files);
    private static async Task<Snapshot> Capture(GitRepository repo) => new(await repo.ResolveRevisionAsync("HEAD"), (await Git(repo, "ls-files", "--stage", "-z")).StandardOutput,
        File.ReadAllBytes(Path.Combine(repo.Path, ".git/config")), (await Git(repo, "for-each-ref", "--format=%(refname) %(objectname)")).StandardOutput,
        Directory.EnumerateFiles(repo.Path, "*", SearchOption.AllDirectories).Where(path => !Path.GetRelativePath(repo.Path, path).StartsWith(".git/", StringComparison.Ordinal)).ToDictionary(path => Path.GetRelativePath(repo.Path, path), File.ReadAllBytes, StringComparer.Ordinal));
    private static async Task Same(GitRepository repo, Snapshot before)
    {
        Snapshot after = await Capture(repo);
        Check(after.Head == before.Head && after.Index == before.Index && after.Refs == before.Refs && after.Config.AsSpan().SequenceEqual(before.Config), "Validation preserves checkout, index, refs and config.");
        Check(after.Files.Count == before.Files.Count && before.Files.All(file => after.Files.TryGetValue(file.Key, out byte[]? bytes) && bytes.AsSpan().SequenceEqual(file.Value)), "Validation preserves every worktree filename and byte.");
    }
    private static async Task<bool> HasRef(GitRepository repo, string name) => (await repo.GetReferencesAsync()).Any(reference => reference.Name == name);
    private static async Task<GitResult> Git(GitRepository repo, params string[] args) { GitResult result = await repo.RunAsync(args); Check(result.Success, result.StandardError); return result; }
    private static async Task Commit(GitRepository repo, string path, string contents, string message)
    { string file = Path.Combine(repo.Path, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, contents); await repo.StageAllAsync(); await repo.CommitAsync(message); }
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static async Task Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Fixture : IDisposable
    {
        private Fixture(RepositoryFixture seed, GitRepository server, GitRepository writer) { Seed = seed; Server = server; Writer = writer; }
        internal RepositoryFixture Seed { get; }
        internal GitRepository Local => Seed.Repository;
        internal GitRepository Server { get; }
        internal GitRepository Writer { get; }
        internal static async Task<Fixture> CreateAsync()
        {
            RepositoryFixture seed = await RepositoryFixture.CreateAsync();
            try
            {
                GitRepository server = await GitRepository.InitAsync(Path.Combine(seed.Root, "private remote.git"), bare: true);
                await seed.Repository.AddRemoteAsync("origin", server.Path); await seed.Repository.PushAsync("origin", "main", setUpstream: true);
                GitRepository writer = await GitRepository.CloneAsync(server.Path, Path.Combine(seed.Root, "private writer")); await RepositoryFixture.ConfigureAsync(writer);
                return new(seed, server, writer);
            }
            catch { seed.Dispose(); throw; }
        }
        internal async Task<string> AdvanceAsync(string name) { await Commit(Writer, name, "remote " + name + "\n", "Remote " + name); await Writer.PushAsync("origin", "main"); return await Writer.ResolveRevisionAsync("HEAD"); }
        internal async Task<GitRepository> ShallowAsync()
        {
            string path = Path.Combine(Seed.Root, "shallow " + Guid.NewGuid().ToString("N"));
            await Git(Local, "clone", "--depth=1", new Uri(Server.Path).AbsoluteUri, path);
            GitRepository shallow = new(path); await RepositoryFixture.ConfigureAsync(shallow); return shallow;
        }
        public void Dispose() => Seed.Dispose();
    }
}
