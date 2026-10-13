using System.Diagnostics;
using System.Text.Json;
using GitExtensions.Linux;

internal static partial class RebaseAdvancedScenarios
{
    private static async Task<string> RecoveryBytes(RepositoryFixture fixture)
    {
        string directory = (await fixture.Repository.GetInfoAsync()).GitDirectory;
        List<string> metadata = [];
        foreach (string name in new[] { "rebase-merge", "rebase-apply", "gitextensions-rebase-native", "gitextensions-rebase" })
        {
            string root = System.IO.Path.Combine(directory, name);
            if (!Directory.Exists(root)) { continue; }
            metadata.Add(name);
            metadata.AddRange(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => System.IO.Path.GetRelativePath(directory, file) + ":" + Convert.ToHexString(File.ReadAllBytes(file))));
        }
        return await State(fixture) + "\n" + string.Join("\n", metadata);
    }

    private static Task CodecRefusal(string codec, Func<Task> action)
        => codec == "windows-1252" ? Expect<InvalidOperationException>(action) : Expect<NotSupportedException>(action);

    private static async Task SavedNativeAbortCodecAsync()
    {
        foreach (string backend in new[] { "merge", "apply" })
        foreach (string codec in new[] { "not-an-encoding", "utf-16", "windows-1252" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync();
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "iso-8859-1");
            await ConflictTopic(f); await f.Repository.SetConfigAsync("rebase.backend", backend);
            string head = await f.Repository.ResolveRevisionAsync("HEAD"), tree = await Text(f.Repository, "write-tree");
            string refs = await Text(f.Repository, "for-each-ref");
            await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            await f.Repository.SetConfigAsync("i18n.commitEncoding", codec);
            byte[] config = File.ReadAllBytes(f.File(".git/config")); string before = await RecoveryBytes(f);
            GitRepository reopened = new(f.Repository.Path); GitRebaseState state = await reopened.GetRebaseStateAsync();
            Check(state.IsActive && state.Backend == (backend == "merge" ? GitRebaseBackend.Merge : GitRebaseBackend.Apply)
                && state.EncodingError is null && state.Steps.Count > 0 && state.HasConflicts,
                "Saved native encoding must keep the genuine backend, patches and conflicts readable after a config change.");
            await CodecRefusal(codec, () => reopened.ContinueRebaseAsync());
            await CodecRefusal(codec, () => reopened.SkipRebaseAsync());
            if (backend == "merge") { await CodecRefusal(codec, () => reopened.EditRebaseTodoAsync()); }
            Check(await RecoveryBytes(f) == before, "Codec refusals and readonly recovery must preserve every journal/backend/index/config/ref/worktree byte.");
            await reopened.AbortRebaseAsync();
            Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && await Text(f.Repository, "write-tree") == tree
                && await Text(f.Repository, "for-each-ref") == refs && await Text(f.Repository, "branch", "--show-current") == "topic",
                "Abort with an invalid current codec must restore Git's original checkout, refs and index tree.");
            Check(File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config) && !(await reopened.GetRebaseStateAsync()).IsActive
                && File.ReadAllText(f.File("conflict1.txt")) == "topic\n" && !Directory.Exists(f.File(".git/gitextensions-rebase-native")),
                "Abort must preserve the changed config, restore tracked bytes and clear only its completed journal.");
        }
    }

    private static async Task SavedLegacyAbortCodecAsync()
    {
        foreach (string codec in new[] { "not-an-encoding", "utf-16", "windows-1252" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync();
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "iso-8859-1"); await Topic(f);
            string head = await f.Repository.ResolveRevisionAsync("HEAD"), tree = await Text(f.Repository, "write-tree"), refs = await Text(f.Repository, "for-each-ref");
            GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
            await f.Repository.InteractiveRebaseAsync(plan with { Steps = plan.Steps.Select(step => step with { Action = GitRebaseAction.Edit }).ToArray() }, Environment.ProcessPath!);
            await f.Repository.SetConfigAsync("i18n.commitEncoding", codec);
            byte[] config = File.ReadAllBytes(f.File(".git/config")); string before = await RecoveryBytes(f);
            GitRepository reopened = new(f.Repository.Path); GitRebaseState state = await reopened.GetRebaseStateAsync();
            Check(state.IsActive && state.EncodingError is null && state.Steps.Count > 0, "Legacy saved codec must be selected before invalid current config.");
            await CodecRefusal(codec, () => reopened.ContinueRebaseAsync());
            await CodecRefusal(codec, () => reopened.EditRebaseTodoAsync());
            Check(await RecoveryBytes(f) == before, "Legacy Continue rejection must preserve the pending session and repository exactly.");
            await reopened.AbortRebaseAsync();
            Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && await Text(f.Repository, "write-tree") == tree && await Text(f.Repository, "for-each-ref") == refs
                && File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config) && File.ReadAllText(f.File("topic.txt")) == "topic\n"
                && !Directory.Exists(f.File(".git/gitextensions-rebase")), "Legacy Abort restores Git's state and retains the changed codec config bytes.");
        }
        foreach (string recovery in new[] { "reword", "squash", "skip" })
        {
            bool squash = recovery == "squash";
            using RepositoryFixture f = await RepositoryFixture.CreateAsync();
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "windows-1252"); await Topic(f);
            await Commit(f, "second.txt", "second\n", "Second");
            string explicitEditor = Environment.ProcessPath!, originalEditor = explicitEditor;
            if (OperatingSystem.IsLinux())
            {
                originalEditor = System.IO.Path.Combine(f.Root, "original-legacy-editor");
                File.WriteAllText(originalEditor, "#!/bin/sh\nexec " + ShellQuote(explicitEditor) + " \"$@\"\n");
                File.SetUnixFileMode(originalEditor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
            string message = "Café £ legacy résumé\n\nFrozen business text.\n";
            await f.Repository.InteractiveRebaseAsync(plan with { Steps = [plan.Steps[0] with { Action = GitRebaseAction.Edit },
                plan.Steps[1] with { Action = squash ? GitRebaseAction.Squash : GitRebaseAction.Reword, Message = message }] }, originalEditor);
            GitRebaseState state = await f.Repository.GetRebaseStateAsync();
            Check(state.UsesLegacyPlan && state.IsActive && state.CanContinue && !state.CanEditTodo,
                "The legacy plan must remain continuable while explicitly declaring native todo editing unavailable.");
            string before = await RecoveryBytes(f);
            await Expect<NotSupportedException>(() => f.Repository.EditRebaseTodoAsync(explicitEditor));
            Check(await RecoveryBytes(f) == before && !Directory.Exists(f.File(".git/gitextensions-rebase-native")),
                "A refused native editor must not replace or duplicate the legacy identity/messages.");
            if (recovery == "skip") { await new GitRepository(f.Repository.Path).SkipRebaseAsync(explicitEditor); }
            else { await new GitRepository(f.Repository.Path).ContinueRebaseAsync(explicitEditor); }
            GitBinaryResult raw = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["cat-file", "commit", "HEAD"]);
            int body = raw.StandardOutput.AsSpan().IndexOf(new byte[] { 10, 10 });
            byte[] expected = System.Text.Encoding.GetEncoding("windows-1252").GetBytes(message);
            Check(raw.Success && body >= 0 && raw.StandardOutput.AsSpan(body + 2).SequenceEqual(expected),
                "Explicit editor continuation must retain the frozen reword/squash message bytes in their original CP1252 codec.");
            Check(!Directory.Exists(f.File(".git/gitextensions-rebase")) && !Directory.Exists(f.File(".git/gitextensions-rebase-native"))
                && !(await f.Repository.GetRebaseStateAsync()).IsActive && File.ReadAllText(f.File("second.txt")) == "second\n",
                "Legacy continuation must finish Git, preserve patches, and remove its completed journal without creating a native identity.");
        }
    }

    private static async Task ExternalAbortCodecAsync()
    {
        foreach (string backend in new[] { "merge", "apply" })
        foreach (string codec in new[] { "not-an-encoding", "utf-16" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string[] commits = await ConflictTopic(f);
            string head = await f.Repository.ResolveRevisionAsync("HEAD"), onto = await f.Repository.ResolveRevisionAsync("main"), tree = await Text(f.Repository, "write-tree");
            GitResult start = await new GitRunner().RunAsync(f.Repository.Path, ["rebase", "--" + backend, "main"]);
            Check(!start.Success, "The external Git rebase must stop at its actual conflict.");
            await f.Repository.SetConfigAsync("i18n.commitEncoding", codec);
            byte[] config = File.ReadAllBytes(f.File(".git/config")); string before = await RecoveryBytes(f);
            GitRepository reopened = new(f.Repository.Path); GitRebaseState state = await reopened.GetRebaseStateAsync();
            Check(state.Backend == (backend == "merge" ? GitRebaseBackend.Merge : GitRebaseBackend.Apply) && state.IsActive
                && state.OriginalHead == head && state.OnTo == onto && state.HeadName == "refs/heads/topic" && state.CurrentCommit == commits[0]
                && state.HasConflicts && state.Conflicts.Any(conflict => conflict.Path == "conflict1.txt"), "Minimal projection must retain the real Git identities and conflicts.");
            Check(state.EncodingError is { Length: > 0 } && state.Steps.Count == 0 && state.TodoPath is null && state.TodoText is null
                && !state.CanContinue && !state.CanEditTodo, "Unavailable codec must be explicit and must not invent decoded todo or enable dependent actions.");
            await Expect<NotSupportedException>(() => reopened.ContinueRebaseAsync());
            await Expect<NotSupportedException>(() => reopened.EditRebaseTodoAsync());
            Check(await RecoveryBytes(f) == before && !Directory.Exists(f.File(".git/gitextensions-rebase-native")), "Minimal reads/refusals must not write an editor journal or alter native state.");
            await reopened.AbortRebaseAsync();
            Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && await Text(f.Repository, "write-tree") == tree
                && File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config) && File.ReadAllText(f.File("conflict1.txt")) == "topic\n",
                "External Abort must remain available with invalid config and preserve that config.");
        }
    }

    private static async Task TerminalRebaseLifecycleAsync()
    {
        foreach (bool legacy in new[] { false, true })
        foreach (bool abort in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f);
            if (legacy)
            {
                GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
                await f.Repository.InteractiveRebaseAsync(plan with { Steps = plan.Steps.Select(step => step with { Action = GitRebaseAction.Edit }).ToArray() }, Environment.ProcessPath!);
            }
            else
            {
                using EditorScope editor = new(f, "break");
                await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true }, editor.Executable);
            }
            string journal = f.File(legacy ? ".git/gitextensions-rebase" : ".git/gitextensions-rebase-native");
            Check((await f.Repository.GetRebaseStateAsync()).IsActive && Directory.Exists(journal), "The first real session must be paused with its persistent journal.");
            await Git(f.Repository, "rebase", abort ? "--abort" : "--continue");
            Check(!(await f.Repository.GetRebaseStateAsync()).IsActive && Directory.Exists(journal), "Terminal completion deliberately leaves the application's journal obsolete.");
            await Commit(f, "round-two.txt", "new attempt\n", "Round two");
            await Git(f.Repository, "switch", "main"); await Commit(f, "round-upstream.txt", "new upstream\n", "Next upstream"); await Git(f.Repository, "switch", "topic");
            if (legacy) { await new GitRepository(f.Repository.Path).RebaseAsync(new GitRebaseOptions { BranchName = "main" }); }
            else
            {
                GitRebasePlan next = await f.Repository.GetRebasePlanAsync("main");
                await new GitRepository(f.Repository.Path).InteractiveRebaseAsync(next, Environment.ProcessPath!);
            }
            Check(!(await f.Repository.GetRebaseStateAsync()).IsActive && !Directory.Exists(f.File(".git/gitextensions-rebase-native"))
                && !Directory.Exists(f.File(".git/gitextensions-rebase")) && File.ReadAllText(f.File("round-two.txt")) == "new attempt\n"
                && File.ReadAllText(f.File("round-upstream.txt")) == "new upstream\n", "A new native/legacy attempt must replace only obsolete completed journals and succeed with the real changes.");
        }
        // Apply also leaves a native journal after completion outside the app.
        foreach (bool abort in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await ConflictTopic(f); await f.Repository.SetConfigAsync("rebase.backend", "apply");
            await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            if (abort) { await Git(f.Repository, "rebase", "--abort"); }
            else
            {
                for (int index = 0; index < 3 && (await f.Repository.GetRebaseStateAsync()).IsActive; index++)
                {
                    GitRebaseState before = await f.Repository.GetRebaseStateAsync();
                    GitResult skipped = await new GitRunner().RunAsync(f.Repository.Path, ["rebase", "--skip"]);
                    GitRebaseState after = await f.Repository.GetRebaseStateAsync();
                    Check(after.IsActive ? after.CurrentCommit != before.CurrentCommit : skipped.Success,
                        "Each terminal Skip must advance to a new real conflict or finish successfully.");
                }
                Check(!(await f.Repository.GetRebaseStateAsync()).IsActive, "Three real apply patches must finish after their three terminal skips.");
            }
            if (abort)
            {
                await Expect<GitCommandException>(() => new GitRepository(f.Repository.Path).RebaseAsync(new GitRebaseOptions { BranchName = "refs/heads/main" }));
                Check((await f.Repository.GetRebaseStateAsync()).Options?.BranchName == "refs/heads/main", "The new apply attempt must own its newly recorded options.");
            }
            else { await new GitRepository(f.Repository.Path).RebaseAsync(new GitRebaseOptions { BranchName = "refs/heads/main" }); }
            if ((await f.Repository.GetRebaseStateAsync()).IsActive) { await f.Repository.AbortRebaseAsync(); }
            Check(!Directory.Exists(f.File(".git/gitextensions-rebase-native")), "A stale apply journal must not block the next genuine attempt.");
        }
    }

    private static bool TryHandleLifecycleWorker(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.FirstOrDefault() != "--rebase-lease-contention") { return false; }
        try
        {
            Check(args.Length == 3, "Lease worker requires the exact repository and report paths.");
            try { new GitRepository(args[1]).AbortRebaseAsync().GetAwaiter().GetResult(); throw new InvalidOperationException("Foreign Abort acquired a live lease."); }
            catch (IOException error) { File.WriteAllText(args[2], JsonSerializer.Serialize(new { refused = true, exception = error.GetType().Name })); }
        }
        catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
        return true;
    }

    private static async Task AssertForeignLeaseRefusalAsync(RepositoryFixture f)
    {
        byte[] seed = File.ReadAllBytes(f.File("seed.txt"));
        f.Write("seed.txt", "Tracked change must not be auto-stashed by a competing pull.\n");
        string before = await RecoveryBytes(f);
        await Expect<IOException>(() => new GitRepository(f.Repository.Path).AbortRebaseAsync());
        await Expect<IOException>(() => new GitRepository(f.Repository.Path).RebaseAsync("main"));
        await Expect<IOException>(() => new GitRepository(f.Repository.Path).PullWithOptionsAsync(GitPullMode.Rebase, "origin", null, new GitPullOptions(AutoStash: true)));
        string report = System.IO.Path.Combine(f.Root, "lease-worker-" + Guid.NewGuid().ToString("N") + ".json");
        ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--rebase-lease-contention"); start.ArgumentList.Add(f.Repository.Path); start.ArgumentList.Add(report);
        using Process worker = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the independent lease worker.");
        Task<string> output = worker.StandardOutput.ReadToEndAsync(), error = worker.StandardError.ReadToEndAsync();
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Check(worker.ExitCode == 0 && JsonDocument.Parse(File.ReadAllBytes(report)).RootElement.GetProperty("refused").GetBoolean(),
            "An independent process must refuse the live worktree lease: " + await output + await error);
        Check(await RecoveryBytes(f) == before, "Contention must preserve all live backend/journal/repository bytes.");
        Check((await f.Repository.GetStashesAsync()).Count == 0, "Competing Pull-Rebase must refuse before creating any auto-stash or fetching refs.");
        File.WriteAllBytes(f.File("seed.txt"), seed);
    }

    private static async Task AwaitFileAsync(string path, Task running)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(path) && DateTime.UtcNow < deadline && !running.IsCompleted) { await Task.Delay(25); }
        Check(File.Exists(path), "The real Git/editor did not reach its synchronization gate: " + path);
    }

    private static async Task NativeRebaseLeaseAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f);
        using EditorScope editor = new(f, "block"); using CancellationTokenSource cancel = new();
        Task<GitResult> running = f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true }, editor.Executable, cancel.Token);
        try
        {
            await AwaitFileAsync(System.IO.Path.Combine(editor.DirectoryPath, "editor.pid"), running);
            await AssertForeignLeaseRefusalAsync(f);
            await Expect<IOException>(() => new GitRepository(f.Repository.Path).EditRebaseTodoAsync(editor.Executable));
        }
        finally { cancel.Cancel(); await Expect<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(15))); }
        if ((await f.Repository.GetRebaseStateAsync()).IsActive) { await new GitRepository(f.Repository.Path).AbortRebaseAsync(); }
        Check(!(await f.Repository.GetRebaseStateAsync()).IsActive, "Cancellation releases the lease only after Git/editor cleanup, allowing real Abort.");
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static async Task RebaseStartupLeaseAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This gate uses a private Linux Git wrapper."); }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f);
        string wrapper = System.IO.Path.Combine(f.Root, "git-gated"), marker = System.IO.Path.Combine(f.Root, "before-backend"), release = System.IO.Path.Combine(f.Root, "release");
        File.WriteAllText(wrapper, "#!/bin/sh\nfor argument in \"$@\"; do\n if [ \"$argument\" = rebase ]; then\n  printf ready > " + ShellQuote(marker)
            + "\n  while [ ! -f " + ShellQuote(release) + " ]; do sleep 0.05; done\n  break\n fi\ndone\nexec git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepository gated = new(f.Repository.Path, new GitRunner(wrapper)); using CancellationTokenSource cancel = new();
        Task<GitResult> running = gated.RebaseAsync(new GitRebaseOptions { BranchName = "main" }, cancellationToken: cancel.Token);
        try
        {
            await AwaitFileAsync(marker, running);
            Check(!(await f.Repository.GetRebaseStateAsync()).IsActive && Directory.Exists(f.File(".git/gitextensions-rebase-native")), "The gate must precede backend creation while an owned journal/command is live.");
            await AssertForeignLeaseRefusalAsync(f);
            await Expect<IOException>(() => new GitRepository(f.Repository.Path).RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            Check(Directory.Exists(f.File(".git/gitextensions-rebase-native")), "Another instance must not classify the in-flight journal as obsolete.");
        }
        finally { cancel.Cancel(); await Expect<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(15))); }
        Check(!Directory.Exists(f.File(".git/gitextensions-rebase-native")), "Cancelled startup must clean only its own incomplete operation and release the lease.");
        await new GitRepository(f.Repository.Path).RebaseAsync(new GitRebaseOptions { BranchName = "main" });
        Check(!(await f.Repository.GetRebaseStateAsync()).IsActive, "A later command must acquire the released startup lease.");
    }

    private static async Task RebaseLeaseIsolationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f);
        string worktree = System.IO.Path.Combine(f.Root, "linked"); await Git(f.Repository, "worktree", "add", "-b", "independent", worktree, "main");
        using EditorScope editor = new(f, "block"); using CancellationTokenSource cancel = new();
        Task<GitResult> running = f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true }, editor.Executable, cancel.Token);
        try
        {
            await AwaitFileAsync(System.IO.Path.Combine(editor.DirectoryPath, "editor.pid"), running);
            GitRepository linked = new(worktree); await linked.RebaseAsync(new GitRebaseOptions { BranchName = "main" });
            GitRepositoryInfo info = await linked.GetInfoAsync();
            Check(info.GitDirectory != (await f.Repository.GetInfoAsync()).GitDirectory && File.Exists(System.IO.Path.Combine(info.GitDirectory, "gitextensions-rebase.lock")), "A linked worktree must hold its own independent persistent lease.");
            Check(Directory.Exists(f.File(".git/gitextensions-rebase-native")) && !running.IsCompleted, "Another worktree must not remove the active editor journal.");
        }
        finally { cancel.Cancel(); await Expect<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(15))); }
        if ((await f.Repository.GetRebaseStateAsync()).IsActive) { await f.Repository.AbortRebaseAsync(); }
        using (EditorScope paused = new(f, "break")) { await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true }, paused.Executable); }
        await Git(f.Repository, "rebase", "--abort");
        string journal = f.File(".git/gitextensions-rebase-native"), unknown = System.IO.Path.Combine(journal, "foreign-data");
        File.WriteAllBytes(unknown, [0, 255, 13, 10]); string before = await RecoveryBytes(f);
        await Expect<IOException>(() => new GitRepository(f.Repository.Path).RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
        Check(await RecoveryBytes(f) == before, "Unknown obsolete journal contents must fail closed before any deletion or Git mutation.");
        File.Delete(unknown);
        if (!OperatingSystem.IsWindows())
        {
            string outside = System.IO.Path.Combine(f.Root, "outside.bin"); File.WriteAllBytes(outside, [42, 0, 255]);
            File.CreateSymbolicLink(unknown, outside); before = await RecoveryBytes(f);
            await Expect<IOException>(() => new GitRepository(f.Repository.Path).RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            Check(await RecoveryBytes(f) == before && File.ReadAllBytes(outside).SequenceEqual(new byte[] { 42, 0, 255 }), "A journal symlink must never be followed/deleted by stale cleanup.");
            File.Delete(unknown);
        }
        await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" });
    }
    private static async Task PullRebaseActiveBackendAsync()
    {
        foreach (string backend in new[] { "merge", "apply" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await ConflictTopic(f);
            GitRepository remote = await GitRepository.InitAsync(System.IO.Path.Combine(f.Root, "remote.git"), bare: true);
            await Git(f.Repository, "remote", "add", "origin", remote.Path);
            await Git(f.Repository, "push", "origin", "main", "topic"); await Git(f.Repository, "fetch", "origin", "main");
            // A fetch after suspension would actually change origin/main and FETCH_HEAD.
            await Git(remote, "update-ref", "refs/heads/main", await f.Repository.ResolveRevisionAsync("topic"));
            f.Write("seed.txt", "Existing stash must remain intact.\n"); await Git(f.Repository, "stash", "push", "-m", "pre-existing stash");
            await f.Repository.SetConfigAsync("rebase.backend", backend);
            await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            await f.Repository.ResolveConflictAsync("conflict1.txt", GitConflictResolution.Theirs);
            GitRebaseState pending = await f.Repository.GetRebaseStateAsync();
            Check(pending.IsActive && pending.CanContinue && pending.Backend == (backend == "merge" ? GitRebaseBackend.Merge : GitRebaseBackend.Apply),
                "The genuine rebase must be paused with a staged resolution and its command lease already released.");
            string before = await RecoveryBytes(f), stashes = await Text(f.Repository, "stash", "list", "--format=%gd %H %gs");
            byte[] stashLog = File.ReadAllBytes(f.File(".git/logs/refs/stash")), fetch = File.ReadAllBytes(f.File(".git/FETCH_HEAD"));
            string remoteRefs = await Text(remote, "for-each-ref");
            GitRepository stalePull = new(f.Repository.Path);
            await Expect<InvalidOperationException>(() => stalePull.PullWithOptionsAsync(GitPullMode.Rebase, "origin", "main", new GitPullOptions(AutoStash: true)));
            Check(await RecoveryBytes(f) == before && await Text(f.Repository, "stash", "list", "--format=%gd %H %gs") == stashes
                && File.ReadAllBytes(f.File(".git/logs/refs/stash")).SequenceEqual(stashLog) && File.ReadAllBytes(f.File(".git/FETCH_HEAD")).SequenceEqual(fetch)
                && await Text(remote, "for-each-ref") == remoteRefs,
                "A stale Pull-Rebase must refuse before moving the staged resolution into a stash, fetching refs, or changing any index/journal/worktree/config/ref/FETCH_HEAD bytes.");
            Check((await stalePull.GetRebaseStateAsync()).CanContinue, "The existing resolved session must remain immediately continuable after the refusal.");
            await stalePull.AbortRebaseAsync();
        }
    }

}
