using System.Diagnostics;
using System.Text;
using GitExtensions.Linux;

internal static class RemoteBranchDeletionScenarios
{
    private const string Nested = "origin/team";
    private const string Literal = "feature/été-$(touch-marker);'";
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("remote branch deletion preview excludes symbolic/orphan refs and resolves the longest remote prefix", PreviewAsync),
        ("remote branch deletion uses actual remote/merge tracking and inverses renamed fetch mappings", TrackingAndMappingsAsync),
        ("remote branch deletion excludes ambiguous excluded non-branch and unmapped fetch destinations", InvalidMappingsAsync),
        ("remote branch deletion previews merged state and requires explicit unmerged confirmation", UnmergedAsync),
        ("remote branch deletion deletes literal branches across remotes while retaining local branches and checkout", MultipleRemotesAsync),
        ("remote branch deletion validates every selected row before any push and rejects stale local targets", ValidationAsync),
        ("remote branch deletion leases atomically refuse server branches advanced after preview", LeaseAsync),
        ("remote branch deletion returns partial per-remote failures and retains native rejecting hooks", PartialFailureAsync),
        ("remote branch deletion honors the configured push URL and refuses multiple push destinations", PushUrlsAsync),
        ("remote branch deletion requires atomic support and never falls back to partial deletion", AtomicSupportAsync),
        ("remote branch deletion cancellation retains streamed output and terminates its live hook descendants", CancellationAsync),
        ("remote branch deletion retains successful batches when a later remote is cancelled", PartialCancellationAsync),
        ("remote branch deletion cancellation after server receipt reports uncertainty without inventing success or rollback", CancelAfterReceiptAsync),
        ("remote branch deletion works from bare and unborn repositories without worktree mutations", BareAndUnbornAsync),
    ];

    private static async Task PreviewAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", Literal); await f.PublishAsync(Nested, "topic/été");
        await f.Local.RunAsync(["symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main"]);
        await f.Local.RunAsync(["update-ref", "refs/remotes/no-config/stray", f.Initial]);
        IReadOnlyList<GitRemoteBranchDeletionPreview> preview = await f.Local.GetRemoteBranchDeletionPreviewAsync();
        Check(preview.Count == 4 && preview.All(branch => branch.Reference.SymbolicTarget is null), "Only configured non-symbolic remote refs belong in the preview.");
        GitRemoteBranchDeletionPreview nested = Select(preview, Nested, "topic/été");
        Check(nested.Reference.Name == "refs/remotes/origin/team/topic/été" && nested.DestinationRef == "refs/heads/topic/été"
            && nested.ExpectedHash == f.Initial && nested.IsMerged, "Longest configured remote wins, keeping the exact branch and hash.");
        Check(Select(preview, "origin", Literal).BranchName == Literal, "Unicode and shell metacharacters remain literal refname data.");
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => f.Local.GetRemoteBranchDeletionPreviewAsync(cancelled.Token));
    }

    private static async Task TrackingAndMappingsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync(Nested, "server/été");
        await f.Local.SetConfigAsync("remote." + Nested + ".fetch", "+refs/heads/main:refs/remotes/" + Nested + "/main");
        await f.Local.RunAsync(["config", "--add", "remote." + Nested + ".fetch", "+refs/heads/server/été:refs/remotes/" + Nested + "/renamed"]);
        await f.Local.RunAsync(["config", "--add", "remote." + Nested + ".fetch", "+refs/heads/server/été:refs/remotes/" + Nested + "/server/été"]);
        await f.Local.FetchAsync(Nested, prune: false);
        await f.Local.CreateBranchAsync("tracks/different-name", checkout: false);
        await f.Local.SetConfigAsync("branch.tracks/different-name.remote", Nested);
        await f.Local.SetConfigAsync("branch.tracks/different-name.merge", "refs/heads/server/été");
        await f.Local.CreateBranchAsync("server/été", checkout: false);
        await f.Local.CreateBranchAsync("wrong-remote", checkout: false);
        await f.Local.SetConfigAsync("branch.wrong-remote.remote", "origin");
        await f.Local.SetConfigAsync("branch.wrong-remote.merge", "refs/heads/server/été");
        IReadOnlyList<GitRemoteBranchDeletionPreview> preview = await f.Local.GetRemoteBranchDeletionPreviewAsync();
        GitRemoteBranchDeletionPreview mapped = preview.Single(branch => branch.Reference.Name == "refs/remotes/" + Nested + "/renamed");
        Check(mapped.BranchName == "server/été" && mapped.TrackingLocalBranches.SequenceEqual(["tracks/different-name"]),
            "Tracking uses the actual branch.remote/merge pair, independently of local or displayed upstream names.");
        GitRemoteBranchDeletionPreview alias = preview.Single(branch => branch.Reference.Name == "refs/remotes/" + Nested + "/server/été");
        GitRemoteBranchDeletionResult result = await f.Local.DeleteRemoteBranchesAsync(new([mapped, alias]));
        Check(result.Success && !await HasRefAsync(f.Second, "refs/heads/server/été") && await HasRefAsync(f.Local, "refs/heads/tracks/different-name"),
            "A renamed fetched reference deletes the true server branch, leaving the tracking local branch available for the separate dialog.");
    }

    private static async Task InvalidMappingsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "excluded");
        await f.Local.RunAsync(["config", "--add", "remote.origin.fetch", "^refs/heads/excluded"]);
        await f.Local.RunAsync(["config", "--add", "remote.origin.fetch", "+refs/heads/different:refs/remotes/origin/main"]);
        await f.Local.RunAsync(["update-ref", "refs/remotes/" + Nested + "/unmapped", f.Initial]);
        await f.Local.SetConfigAsync("remote." + Nested + ".fetch", "+refs/tags/*:refs/remotes/" + Nested + "/*");
        IReadOnlyList<GitRemoteBranchDeletionPreview> preview = await f.Local.GetRemoteBranchDeletionPreviewAsync();
        Check(preview.Count == 0, "Explicit negative mappings, multiple source mappings, and tag mappings cannot be guessed as branch destinations.");
        await f.Local.SetConfigAsync("remote." + Nested + ".fetch", "+refs/heads/exact:refs/remotes/" + Nested + "/exact");
        Check((await f.Local.GetRemoteBranchDeletionPreviewAsync()).All(branch => branch.Remote != Nested),
            "An existing stale remote ref without a corresponding configured fetch mapping is omitted rather than guessed.");
    }

    private static async Task UnmergedAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Local.CreateBranchAsync("unmerged", checkout: true);
        string ahead = await CommitAsync(f.Local, "ahead.txt", "Unmerged remote work");
        await f.PublishAsync("origin", "unmerged");
        await f.Local.CheckoutAsync("main");
        GitRemoteBranchDeletionPreview selected = Select(await f.Local.GetRemoteBranchDeletionPreviewAsync(), "origin", "unmerged");
        Check(!selected.IsMerged && selected.ExpectedHash == ahead, "A remote tip not contained in actual HEAD must be marked unmerged.");
        await ExpectAsync<InvalidOperationException>(() => f.Local.DeleteRemoteBranchesAsync(new([selected])));
        Check(await RefAsync(f.First, "refs/heads/unmerged") == ahead, "An unconfirmed unmerged branch is retained.");
        Check((await f.Local.DeleteRemoteBranchesAsync(new([selected], AllowUnmerged: true))).Success, "The explicitly confirmed unmerged branch can be removed.");
        Check(await RefAsync(f.Local, "refs/heads/unmerged") == ahead, "Remote deletion must never delete the local branch.");
    }

    private static async Task MultipleRemotesAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", Literal); await f.PublishAsync("origin", "other"); await f.PublishAsync(Nested, "remote/été");
        await f.Local.CreateBranchAsync("keep-local", checkout: false);
        await f.Local.SetConfigAsync("branch.keep-local.remote", Nested); await f.Local.SetConfigAsync("branch.keep-local.merge", "refs/heads/remote/été");
        await f.Local.CreateTagAsync("unrequested-followed-tag", message: "Must not be pushed during deletion");
        await f.Local.SetConfigAsync("push.followTags", "true");
        string before = await LocalStateAsync(f.Local);
        IReadOnlyList<GitRemoteBranchDeletionPreview> preview = await f.Local.GetRemoteBranchDeletionPreviewAsync();
        Pipes pipes = new();
        GitRemoteBranchDeletionResult result = await f.Local.DeleteRemoteBranchesAsync(new([
            Select(preview, "origin", Literal), Select(preview, Nested, "remote/été"), Select(preview, "origin", "other")]), outputReceived: pipes.Receive);
        Check(result.Success && result.Batches.Count == 2 && result.Batches[0].Branches.Count == 2 && result.Batches[1].Branches.Count == 1,
            "Each remote receives one atomic batch, retaining first selected remote order.");
        Check(!await HasRefAsync(f.First, "refs/heads/" + Literal) && !await HasRefAsync(f.First, "refs/heads/other")
            && !await HasRefAsync(f.Second, "refs/heads/remote/été"), "Every selected branch, including literal names, is deleted from its own remote.");
        Check(await HasRefAsync(f.First, "refs/heads/main") && await HasRefAsync(f.Second, "refs/heads/main")
            && !await HasRefAsync(f.First, "refs/tags/unrequested-followed-tag") && !await HasRefAsync(f.Second, "refs/tags/unrequested-followed-tag"), "No unselected branch or followTags tag is changed.");
        Check(before == await LocalStateAsync(f.Local) && result.Batches[1].Branches[0].TrackingLocalBranches.SequenceEqual(["keep-local"]),
            "Local branches, tracking configuration, files, HEAD and index remain unchanged.");
        Check(pipes.Output.SequenceEqual(result.Batches.SelectMany(batch => batch.Result!.StandardOutputBytes))
            && pipes.Error.SequenceEqual(result.Batches.SelectMany(batch => batch.Result!.StandardErrorBytes)) && pipes.Output.Length > 0,
            "Only actual pushes stream byte-exact native stdout/stderr, matching their retained results.");
        Check(!File.Exists(Path.Combine(f.Local.Path, "touch-marker")), "Refname metacharacters never execute shell commands.");
    }

    private static async Task ValidationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "keep"); await f.PublishAsync(Nested, "keep");
        IReadOnlyList<GitRemoteBranchDeletionPreview> preview = await f.Local.GetRemoteBranchDeletionPreviewAsync();
        GitRemoteBranchDeletionPreview first = Select(preview, "origin", "keep"), second = Select(preview, Nested, "keep");
        string marker = Path.Combine(f.Seed.Root, "unexpected-push");
        await f.InstallHookAsync(f.Local, "pre-push", "echo called > " + Quote(marker) + "\nexit 0\n");
        await ExpectAsync<ArgumentException>(() => f.Local.DeleteRemoteBranchesAsync(new([])));
        await ExpectAsync<ArgumentException>(() => f.Local.DeleteRemoteBranchesAsync(new([first, first])));
        await ExpectAsync<InvalidOperationException>(() => f.Local.DeleteRemoteBranchesAsync(new([first, second with { Remote = "forged" }])));
        await ExpectAsync<InvalidOperationException>(() => f.Local.DeleteRemoteBranchesAsync(new([first, second with { BranchName = ":refs/heads/main" }])));
        await ExpectAsync<InvalidOperationException>(() => f.Local.DeleteRemoteBranchesAsync(new([first, second with { Reference = second.Reference with { Hash = new('0', 40) } }])));
        await ExpectAsync<ArgumentException>(() => f.Local.DeleteRemoteBranchesAsync(new([first, second with { Reference = second.Reference with { Name = "refs/remotes/absent/x" } }])));
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => f.Local.DeleteRemoteBranchesAsync(new([first, second]), cancelled.Token));
        await f.Local.SetRemoteUrlAsync(Nested, f.First.Path, push: true);
        await ExpectAsync<InvalidOperationException>(() => f.Local.DeleteRemoteBranchesAsync(new([first, second])));
        await f.Local.SetRemoteUrlAsync(Nested, f.Second.Path, push: true);
        string advanced = await CommitAsync(f.Local, "later.txt", "Refreshed preview target");
        await f.Local.RunAsync(["update-ref", second.Reference.Name, advanced]);
        await ExpectAsync<InvalidOperationException>(() => f.Local.DeleteRemoteBranchesAsync(new([first, second])));
        Check(!File.Exists(marker) && await HasRefAsync(f.First, first.DestinationRef) && await HasRefAsync(f.Second, second.DestinationRef),
            "Invalid rows late in the selection, cancellation, and local ref changes are detected before any mutation-capable push.");
    }

    private static async Task LeaseAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "stale"); await f.PublishAsync("origin", "also-keep");
        IReadOnlyList<GitRemoteBranchDeletionPreview> preview = await f.Local.GetRemoteBranchDeletionPreviewAsync();
        GitRemoteBranchDeletionPreview stale = Select(preview, "origin", "stale"), keep = Select(preview, "origin", "also-keep");
        GitRepository writer = await GitRepository.CloneAsync(f.First.Path, Path.Combine(f.Seed.Root, "independent writer"));
        await RepositoryFixture.ConfigureAsync(writer);
        string advanced = await CommitAsync(writer, "server.txt", "Advanced since confirmation");
        await writer.PushAsync(new GitPushRequest("origin", Source: "HEAD", DestinationRef: "stale"));
        GitRemoteBranchDeletionResult result = await f.Local.DeleteRemoteBranchesAsync(new([keep, stale]));
        Check(!result.Success && result.Batches.Single().Status == GitRemoteBranchDeletionBatchStatus.Failed
            && result.Batches.Single().Result!.StandardError.Contains("stale", StringComparison.OrdinalIgnoreCase), "An explicit confirmed hash lease rejects server advancement without fetching over the confirmation.");
        Check(await RefAsync(f.First, stale.DestinationRef) == advanced && await RefAsync(f.First, keep.DestinationRef) == f.Initial,
            "The whole remote batch is atomic, including otherwise valid branches selected before the stale one.");
        Check(await RefAsync(f.Local, stale.Reference.Name) == f.Initial, "Failure does not silently refresh and accept a different target.");
    }

    private static async Task PartialFailureAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "allowed"); await f.PublishAsync("origin", "blocked"); await f.PublishAsync(Nested, "delete-me");
        await f.InstallHookAsync(f.First, "update", "echo server-update-hook >&2\nif [ \"$1\" = refs/heads/blocked ]; then exit 1; fi\nexit 0\n");
        IReadOnlyList<GitRemoteBranchDeletionPreview> preview = await f.Local.GetRemoteBranchDeletionPreviewAsync();
        Pipes pipes = new();
        GitRemoteBranchDeletionResult result = await f.Local.DeleteRemoteBranchesAsync(new([
            Select(preview, "origin", "allowed"), Select(preview, Nested, "delete-me"), Select(preview, "origin", "blocked")]), outputReceived: pipes.Receive);
        Check(!result.Success && !result.Cancelled && result.Batches[0].Status == GitRemoteBranchDeletionBatchStatus.Failed
            && result.Batches[1].Success, "One rejected server batch must not hide a later remote's successful deletion.");
        Check(await HasRefAsync(f.First, "refs/heads/allowed") && await HasRefAsync(f.First, "refs/heads/blocked") && !await HasRefAsync(f.Second, "refs/heads/delete-me"),
            "Native server hooks and atomicity preserve the rejected remote's branches; the other remote is deleted.");
        Check(Encoding.UTF8.GetString(pipes.Error).Contains("server-update-hook", StringComparison.Ordinal), "Hook diagnostics are streamed instead of swallowed.");
    }

    private static async Task PushUrlsAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "target"); await f.PublishAsync(Nested, "target");
        await f.Local.SetRemoteUrlAsync("origin", f.Second.Path, push: true);
        GitRemoteBranchDeletionPreview branch = Select(await f.Local.GetRemoteBranchDeletionPreviewAsync(), "origin", "target");
        Check((await f.Local.DeleteRemoteBranchesAsync(new([branch]))).Success && await HasRefAsync(f.First, branch.DestinationRef)
            && !await HasRefAsync(f.Second, branch.DestinationRef), "The configured push URL, rather than the fetch URL, receives the guarded deletion.");
        await f.Local.RunAsync(["config", "--add", "remote.origin.pushurl", f.First.Path]);
        IReadOnlyList<GitRemoteBranchDeletionPreview> preview = await f.Local.GetRemoteBranchDeletionPreviewAsync();
        await ExpectAsync<InvalidOperationException>(() => f.Local.DeleteRemoteBranchesAsync(new([Select(preview, Nested, "main"), Select(preview, "origin", "main")])));
        Check(await HasRefAsync(f.First, "refs/heads/main") && await HasRefAsync(f.Second, "refs/heads/main"),
            "Ambiguous multi-push destinations are refused across the entire selection before any earlier remote can mutate.");
        await f.Local.UnsetConfigAsync("remote.origin.pushurl");
        await f.Local.RunAsync(["config", "--add", "remote.origin.url", f.Second.Path]);
        GitRemoteBranchDeletionPreview firstOnly = Select(await f.Local.GetRemoteBranchDeletionPreviewAsync(), "origin", "main");
        Check(firstOnly.ExpectedPushUrls.SequenceEqual([f.First.Path]), "Multiple fetch URLs retain Git's actual first-URL-only push behavior.");
    }

    private static async Task AtomicSupportAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "keep"); await f.First.SetConfigAsync("receive.advertiseAtomic", "false");
        GitRemoteBranchDeletionPreview branch = Select(await f.Local.GetRemoteBranchDeletionPreviewAsync(), "origin", "keep");
        GitRemoteBranchDeletionResult result = await f.Local.DeleteRemoteBranchesAsync(new([branch]));
        Check(!result.Success && result.Batches.Single().Result!.StandardError.Contains("atomic", StringComparison.OrdinalIgnoreCase)
            && await HasRefAsync(f.First, branch.DestinationRef), "A server without atomic support fails explicitly; deletion is never retried non-atomically.");
    }

    private static async Task CancellationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "keep");
        GitRemoteBranchDeletionPreview branch = Select(await f.Local.GetRemoteBranchDeletionPreviewAsync(), "origin", "keep");
        string marker = Path.Combine(f.Seed.Root, "hook-pids");
        await f.InstallHookAsync(f.Local, "pre-push", "sleep 60 &\nchild=$!\nprintf '%s %s\\n' $$ \"$child\" > " + Quote(marker) + "\necho streaming-before-cancel >&2\nwait\n");
        Pipes pipes = new(); using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(25));
        Task<GitRemoteBranchDeletionResult> pending = f.Local.DeleteRemoteBranchesAsync(new([branch]), cancel.Token, pipes.Receive);
        int[] pids = [];
        try
        {
            await UntilAsync(() => File.Exists(marker) && Encoding.UTF8.GetString(pipes.Error).Contains("streaming-before-cancel", StringComparison.Ordinal), pending);
            pids = File.ReadAllText(marker).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            Check(!pending.IsCompleted && pids.All(LinuxProcessState.IsRunning), "The native client hook and sleeper really remain live while output streams.");
            cancel.Cancel();
            GitRemoteBranchDeletionResult result = await pending.WaitAsync(TimeSpan.FromSeconds(12));
            Check(result.Cancelled && !result.Success && result.Batches.Single().Status == GitRemoteBranchDeletionBatchStatus.Cancelled
                && result.Batches.Single().PartialStandardError.Contains("streaming-before-cancel", StringComparison.Ordinal), "Cancellation returns its attempted batch and retained partial output.");
            Check(pids.All(pid => !LinuxProcessState.IsRunning(pid)) && await HasRefAsync(f.First, branch.DestinationRef),
                "Cancellation joins the Git process family and retains the server branch when interrupted before push.");
            await f.Local.SetConfigAsync("core.hooksPath", "/dev/null");
            Check((await f.Local.DeleteRemoteBranchesAsync(new([branch]))).Success, "The mutation lock and runner remain usable after cleanup.");
        }
        finally { cancel.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }

    private static async Task PartialCancellationAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "first"); await f.PublishAsync(Nested, "second");
        await f.Local.AddRemoteAsync("later", f.First.Path); await f.PublishAsync("later", "third");
        string marker = Path.Combine(f.Seed.Root, "second-hook");
        await f.InstallHookAsync(f.Local, "pre-push", "if [ \"$1\" = " + Quote(Nested) + " ]; then\n echo waiting > " + Quote(marker) + "\n sleep 60\nfi\n");
        IReadOnlyList<GitRemoteBranchDeletionPreview> preview = await f.Local.GetRemoteBranchDeletionPreviewAsync();
        using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(25));
        Task<GitRemoteBranchDeletionResult> pending = f.Local.DeleteRemoteBranchesAsync(new([
            Select(preview, "origin", "first"), Select(preview, Nested, "second"), Select(preview, "later", "third")]), cancel.Token);
        try
        {
            await UntilAsync(() => File.Exists(marker), pending); cancel.Cancel();
            GitRemoteBranchDeletionResult result = await pending.WaitAsync(TimeSpan.FromSeconds(12));
            Check(result.Cancelled && result.Batches[0].Success && result.Batches[1].Status == GitRemoteBranchDeletionBatchStatus.Cancelled
                && result.Batches[2].Status == GitRemoteBranchDeletionBatchStatus.NotAttempted,
                "A later cancellation retains the first remote's proven success for an accurately scoped local tracking dialog.");
            Check(!await HasRefAsync(f.First, "refs/heads/first") && await HasRefAsync(f.Second, "refs/heads/second")
                && await HasRefAsync(f.First, "refs/heads/third"), "The result matches successful, cancelled, and unattempted real remote states.");
        }
        finally { cancel.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }

    private static async Task BareAndUnbornAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "from-bare"); await f.PublishAsync("origin", "from-unborn");
        GitRepository bare = await GitRepository.InitAsync(Path.Combine(f.Seed.Root, "client bare.git"), bare: true);
        await RepositoryFixture.ConfigureAsync(bare); await bare.AddRemoteAsync("origin", f.First.Path); await bare.FetchAsync("origin", prune: false);
        await bare.RunAsync(["update-ref", "refs/heads/main", f.Initial]);
        GitRemoteBranchDeletionPreview branch = Select(await bare.GetRemoteBranchDeletionPreviewAsync(), "origin", "from-bare");
        Check(branch.IsMerged && (await bare.DeleteRemoteBranchesAsync(new([branch]))).Success, "A bare repository reads merge state and pushes deletion without requiring a worktree.");
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(seed: false);
        await unborn.Repository.AddRemoteAsync("origin", f.First.Path); await unborn.Repository.FetchAsync("origin", prune: false);
        GitRemoteBranchDeletionPreview unbornBranch = Select(await unborn.Repository.GetRemoteBranchDeletionPreviewAsync(), "origin", "from-unborn");
        Check(!unbornBranch.IsMerged && (await unborn.Repository.DeleteRemoteBranchesAsync(new([unbornBranch], AllowUnmerged: true))).Success
            && (await unborn.Repository.GetStatusAsync()).IsUnborn, "Unborn HEAD treats remote commits as unmerged and stays unborn after explicitly confirmed deletion.");
    }

    private static async Task CancelAfterReceiptAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.PublishAsync("origin", "received");
        string marker = Path.Combine(f.Seed.Root, "post-receive-started");
        await f.InstallHookAsync(f.First, "post-receive", "echo entered > " + Quote(marker) + "\necho deletion-already-applied >&2\nsleep 60\n");
        GitRemoteBranchDeletionPreview branch = Select(await f.Local.GetRemoteBranchDeletionPreviewAsync(), "origin", "received");
        Pipes pipes = new(); using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(25));
        Task<GitRemoteBranchDeletionResult> pending = f.Local.DeleteRemoteBranchesAsync(new([branch]), cancel.Token, pipes.Receive);
        try
        {
            await UntilAsync(() => File.Exists(marker) && Encoding.UTF8.GetString(pipes.Error).Contains("deletion-already-applied", StringComparison.Ordinal), pending);
            Check(!await HasRefAsync(f.First, branch.DestinationRef), "The actual remote transaction has finished before cancellation is requested.");
            cancel.Cancel();
            GitRemoteBranchDeletionResult result = await pending.WaitAsync(TimeSpan.FromSeconds(12));
            Check(result.Cancelled && !result.Success && !result.Batches.Single().Success && result.Batches.Single().ErrorMessage!.Contains("remote outcome", StringComparison.Ordinal)
                && !await HasRefAsync(f.First, branch.DestinationRef), "An interrupted client reports uncertain outcome and never claims success or rollback after server receipt.");
        }
        finally { cancel.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }

    private static GitRemoteBranchDeletionPreview Select(IReadOnlyList<GitRemoteBranchDeletionPreview> branches, string remote, string name)
        => branches.Single(branch => branch.Remote == remote && branch.BranchName == name);
    private static async Task<string> CommitAsync(GitRepository repository, string file, string message)
    { await File.WriteAllTextAsync(Path.Combine(repository.Path, file), message); await repository.StageAllAsync(); await repository.CommitAsync(message); return await repository.ResolveRevisionAsync("HEAD"); }
    private static Task<string> RefAsync(GitRepository repository, string name) => repository.ResolveRevisionAsync(name);
    private static async Task<bool> HasRefAsync(GitRepository repository, string name) => (await repository.GetReferencesAsync()).Any(reference => reference.Name == name);
    private static async Task<string> LocalStateAsync(GitRepository repository)
        => string.Join('\n', (await repository.GetReferencesAsync()).Where(reference => reference.IsBranch || reference.IsTag).Select(reference => reference.Name + " " + reference.Hash))
        + "\n" + (await repository.RunAsync(["status", "--porcelain=v2", "--branch", "-z"])).StandardOutput
        + "\n" + string.Join('\n', (await repository.GetConfigAsync("^branch\\.")).Select(item => item.Key + "=" + item.Value))
        + "\n" + Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(repository.Path, ".git", "index")));
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> ExpectAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static async Task UntilAsync(Func<bool> condition, Task pending)
    { Stopwatch clock = Stopwatch.StartNew(); while (!condition()) { if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Operation completed before its live hook witness."); } if (clock.Elapsed > TimeSpan.FromSeconds(12)) { throw new TimeoutException("No live hook witness."); } await Task.Delay(25); } }
    private sealed class Pipes
    {
        private readonly MemoryStream _output = new(), _error = new();
        private readonly object _gate = new();
        public byte[] Output { get { lock (_gate) { return _output.ToArray(); } } }
        public byte[] Error { get { lock (_gate) { return _error.ToArray(); } } }
        public void Receive(GitProcessOutput item) { lock (_gate) { (item.Stream == GitProcessOutputStream.StandardOutput ? _output : _error).Write(item.Bytes.Span); } }
    }
    private sealed class Fixture : IDisposable
    {
        private Fixture(RepositoryFixture seed, GitRepository first, GitRepository second, string initial)
        { Seed = seed; First = first; Second = second; Initial = initial; }
        public RepositoryFixture Seed { get; }
        public GitRepository Local => Seed.Repository;
        public GitRepository First { get; }
        public GitRepository Second { get; }
        public string Initial { get; }
        public static async Task<Fixture> CreateAsync()
        {
            RepositoryFixture seed = await RepositoryFixture.CreateAsync();
            try
            {
                GitRepository first = await GitRepository.InitAsync(Path.Combine(seed.Root, "first bare.git"), bare: true);
                GitRepository second = await GitRepository.InitAsync(Path.Combine(seed.Root, "second bare.git"), bare: true);
                await RepositoryFixture.ConfigureAsync(first); await RepositoryFixture.ConfigureAsync(second);
                await seed.Repository.AddRemoteAsync("origin", first.Path);
                // Native remote add refuses nested names, but existing Git configurations can contain them.
                await seed.Repository.SetConfigAsync("remote." + Nested + ".url", second.Path);
                await seed.Repository.SetConfigAsync("remote." + Nested + ".fetch", "+refs/heads/*:refs/remotes/" + Nested + "/*");
                await seed.Repository.PushAsync("origin", "main"); await seed.Repository.PushAsync(Nested, "main");
                return new(seed, first, second, await seed.Repository.ResolveRevisionAsync("HEAD"));
            }
            catch { seed.Dispose(); throw; }
        }
        public async Task PublishAsync(string remote, string branch) => await Local.PushAsync(new GitPushRequest(remote, Source: "HEAD", DestinationRef: branch));
        public async Task InstallHookAsync(GitRepository repository, string name, string body)
        {
            string directory = Path.Combine(Seed.Root, "hooks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, name); await File.WriteAllTextAsync(file, "#!/bin/sh\n" + body);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            await repository.SetConfigAsync("core.hooksPath", directory);
        }
        public void Dispose() => Seed.Dispose();
    }
}
