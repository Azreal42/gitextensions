using System.Diagnostics;
using System.Globalization;
using GitExtensions.Linux;

internal static class PullFetchScenarios
{
    private const string First = "a-first";
    private const string Upstream = "z-upstream";

    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("configured fetch uses the nonfirst upstream without an origin remote", ConfiguredRemoteAsync),
        ("remote and branch fetches update only the requested local remote refs", TargetedFetchAsync),
        ("fetch all updates both local remotes without changing the checkout", FetchAllAsync),
        ("ordinary fetch honors configured pruning and remote tagopt", ConfiguredPruneAndTagsAsync),
        ("fetch and prune all forces rewritten refs and preserves local tags", PruneAllAsync),
        ("single remote prune and branch prune remain scoped to that remote", TargetedPruneAsync),
        ("explicit pull merge overrides pull.rebase and retains two real parents", DivergentMergeAsync),
        ("explicit pull rebase overrides pull.rebase and retains linear history", DivergentRebaseAsync),
        ("fast-forward-only rejects merge while explicit rebase follows native Git precedence", NativeFastForwardPrecedenceAsync),
        ("pull merge respects configured no-fast-forward on a fast-forward history", NoFastForwardAsync),
        ("network command errors and cancelled requests preserve refs index and files", ErrorsAndPreCancellationAsync),
        ("fetch and pull cancellation terminate the process and descendants", RunningCancellationAsync)
    ];

    private static async Task ConfiguredRemoteAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        string first = await AdvanceAsync(fixture.FirstWriter, "first.txt", "First remote advances");
        string upstream = await AdvanceAsync(fixture.UpstreamWriter, "upstream.txt", "Upstream advances");
        Snapshot before = await CaptureAsync(fixture);
        GitRemote[] remotes = (await fixture.Local.GetRemotesAsync()).OrderBy(remote => remote.Name, StringComparer.Ordinal).ToArray();
        Check(remotes.Length == 2 && remotes[0].Name == First && remotes[1].Name == Upstream && remotes.All(remote => remote.Name != "origin"),
            "The fixture has two actual remotes, no origin, and an upstream that is not the first remote.");
        Check((await fixture.Local.GetReferencesAsync()).Single(reference => reference.IsCurrent).Upstream == Upstream + "/main",
            "The current branch genuinely tracks the second remote.");

        await fixture.Local.FetchConfiguredRemoteAsync();

        Check(await RefAsync(fixture.Local, Tracking(Upstream)) == upstream, "A configured fetch reads the current branch's upstream remote.");
        Check(await RefAsync(fixture.Local, Tracking(First)) == fixture.Initial && first != fixture.Initial,
            "It must not fetch the alphabetically first remote or silently fetch all remotes.");
        await CheckCheckoutAsync(fixture, before);
    }

    private static async Task TargetedFetchAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        string first = await AdvanceAsync(fixture.FirstWriter, "first.txt", "First stays outside the selected scope");
        string main = await AdvanceAsync(fixture.UpstreamWriter, "upstream.txt", "Upstream main advances");
        const string branch = "feature/été-180";
        await fixture.UpstreamWriter.RunAsync(["checkout", "-b", branch]);
        string feature = await AdvanceAsync(fixture.UpstreamWriter, "feature.txt", "Explicit branch advances", branch);
        Snapshot before = await CaptureAsync(fixture);

        await fixture.Local.FetchRemoteBranchAsync(Upstream, branch);

        Check(await RefAsync(fixture.Local, Tracking(Upstream, branch)) == feature, "The explicit Unicode/slash branch is fetched literally.");
        Check(await RefAsync(fixture.Local, Tracking(Upstream)) == fixture.Initial, "Fetching one remote branch must not update its main branch.");
        Check(await RefAsync(fixture.Local, Tracking(First)) == fixture.Initial && first != fixture.Initial, "The other remote is not contacted by the targeted operation.");
        Check(File.ReadAllText(fixture.Seed.File(".git/FETCH_HEAD")).StartsWith(feature + "\t", StringComparison.Ordinal), "FETCH_HEAD belongs to the requested branch.");
        await CheckCheckoutAsync(fixture, before);

        await fixture.Local.FetchRemoteAsync(Upstream);
        Check(await RefAsync(fixture.Local, Tracking(Upstream)) == main, "Fetching the selected remote now updates its remaining configured refs.");
        Check(await RefAsync(fixture.Local, Tracking(First)) == fixture.Initial, "A selected remote fetch still does not become fetch all.");
        await CheckCheckoutAsync(fixture, before);
    }

    private static async Task FetchAllAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        string first = await AdvanceAsync(fixture.FirstWriter, "first.txt", "First all-fetch change");
        string upstream = await AdvanceAsync(fixture.UpstreamWriter, "upstream.txt", "Second all-fetch change");
        Snapshot before = await CaptureAsync(fixture);

        await fixture.Local.FetchAllAsync();

        Check(await RefAsync(fixture.Local, Tracking(First)) == first && await RefAsync(fixture.Local, Tracking(Upstream)) == upstream,
            "Fetch all advances both configured local remotes.");
        await CheckCheckoutAsync(fixture, before);
    }

    private static async Task ConfiguredPruneAndTagsAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        await fixture.FirstBare.RunAsync(["update-ref", "refs/heads/stale", fixture.Initial]);
        await fixture.UpstreamBare.RunAsync(["update-ref", "refs/heads/stale", fixture.Initial]);
        await fixture.Local.FetchAllAsync();
        await fixture.FirstBare.RunAsync(["update-ref", "-d", "refs/heads/stale"]);
        await fixture.UpstreamBare.RunAsync(["update-ref", "-d", "refs/heads/stale"]);
        await AdvanceAsync(fixture.FirstWriter, "first.txt", "First tagged change");
        await AdvanceAsync(fixture.UpstreamWriter, "upstream.txt", "Upstream tagged change");
        await TagAndPushAsync(fixture.FirstWriter, "first-auto-tag");
        await TagAndPushAsync(fixture.UpstreamWriter, "upstream-blocked-tag");
        await fixture.Local.SetConfigAsync("fetch.prune", "false");
        await fixture.Local.SetConfigAsync("remote." + Upstream + ".tagopt", "--no-tags");
        Snapshot unpruned = await CaptureAsync(fixture);

        await fixture.Local.FetchRemoteAsync(First);
        Check(await HasRefAsync(fixture.Local, Tracking(First, "stale")), "An ordinary fetch does not force pruning when fetch.prune is false.");
        Check(await HasRefAsync(fixture.Local, "refs/tags/first-auto-tag"), "Default tag following stays active without an imposed --no-tags.");
        await CheckCheckoutAsync(fixture, unpruned);

        await fixture.Local.SetConfigAsync("fetch.prune", "true");
        Snapshot configured = await CaptureAsync(fixture);
        await fixture.Local.FetchConfiguredRemoteAsync();
        Check(!await HasRefAsync(fixture.Local, Tracking(Upstream, "stale")), "Configured fetch preserves fetch.prune=true instead of imposing --no-prune.");
        Check(await HasRefAsync(fixture.Local, Tracking(First, "stale")), "Configured pruning remains limited to the upstream remote.");
        Check(!await HasRefAsync(fixture.Local, "refs/tags/upstream-blocked-tag"), "The upstream remote's tagopt=--no-tags is honored.");
        await CheckCheckoutAsync(fixture, configured);

        await fixture.Local.FetchRemoteAsync(First);
        Check(!await HasRefAsync(fixture.Local, Tracking(First, "stale")), "Explicit remote fetch also honors configured pruning.");
        await CheckCheckoutAsync(fixture, configured);
    }

    private static async Task PruneAllAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        string previous = await AdvanceAsync(fixture.FirstWriter, "first.txt", "First old history");
        await fixture.FirstBare.RunAsync(["update-ref", "refs/heads/stale", fixture.Initial]);
        await fixture.UpstreamBare.RunAsync(["update-ref", "refs/heads/stale", fixture.Initial]);
        await fixture.FirstBare.RunAsync(["tag", "removed-remote-tag", fixture.Initial]);
        await fixture.Local.FetchAllAsync();
        Check(await HasRefAsync(fixture.Local, "refs/tags/removed-remote-tag"), "The removed tag must first exist locally through an actual fetch.");
        await fixture.Local.RunAsync(["tag", "local-only-tag", fixture.Initial]);
        await fixture.FirstBare.RunAsync(["tag", "-d", "removed-remote-tag"]);
        await fixture.FirstBare.RunAsync(["update-ref", "-d", "refs/heads/stale"]);
        await fixture.UpstreamBare.RunAsync(["update-ref", "-d", "refs/heads/stale"]);
        await fixture.FirstWriter.RunAsync(["reset", "--hard", fixture.Initial]);
        string rewritten = await AdvanceAsync(fixture.FirstWriter, "rewrite.txt", "First rewritten history", force: true);
        string upstream = await AdvanceAsync(fixture.UpstreamWriter, "upstream.txt", "Second prune-all change");
        // A non-forced fetch refspec makes the --force behavior observable:
        // the first remote's newly rewritten branch is not a descendant.
        await fixture.Local.SetConfigAsync("remote." + First + ".fetch", "refs/heads/*:refs/remotes/" + First + "/*");
        await fixture.Local.SetConfigAsync("fetch.prune", "false");
        Snapshot before = await CaptureAsync(fixture);
        Check(await RefAsync(fixture.Local, Tracking(First)) == previous, "The local tracking ref still points to the superseded remote history.");

        await fixture.Local.FetchPruneAllAsync();

        Check(await RefAsync(fixture.Local, Tracking(First)) == rewritten && await RefAsync(fixture.Local, Tracking(Upstream)) == upstream,
            "Prune all explicitly forces the rewritten tracking ref and fetches both remotes.");
        Check(!await HasRefAsync(fixture.Local, Tracking(First, "stale")) && !await HasRefAsync(fixture.Local, Tracking(Upstream, "stale")),
            "Stale remote-tracking branches are removed from both remotes even when normal fetch.prune is false.");
        Check(await HasRefAsync(fixture.Local, "refs/tags/removed-remote-tag") && await HasRefAsync(fixture.Local, "refs/tags/local-only-tag"),
            "Prune all must not imply --prune-tags or erase tags missing on the servers.");
        await CheckCheckoutAsync(fixture, before);
    }

    private static async Task TargetedPruneAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        await fixture.FirstBare.RunAsync(["update-ref", "refs/heads/stale", fixture.Initial]);
        await fixture.UpstreamBare.RunAsync(["update-ref", "refs/heads/stale", fixture.Initial]);
        await fixture.Local.FetchAllAsync();
        await fixture.FirstBare.RunAsync(["update-ref", "-d", "refs/heads/stale"]);
        await fixture.UpstreamBare.RunAsync(["update-ref", "-d", "refs/heads/stale"]);
        await fixture.Local.SetConfigAsync("fetch.prune", "false");
        Snapshot before = await CaptureAsync(fixture);

        await fixture.Local.FetchRemoteAsync(First, prune: true);
        Check(!await HasRefAsync(fixture.Local, Tracking(First, "stale")) && await HasRefAsync(fixture.Local, Tracking(Upstream, "stale")),
            "An explicit single-remote prune does not prune a different remote.");
        await AdvanceAsync(fixture.FirstWriter, "outside-selected-scope.txt", "First changes outside the targeted branch fetch");
        await fixture.Local.FetchRemoteBranchAsync(Upstream, "main", prune: true);
        Check(await RefAsync(fixture.Local, Tracking(First)) == fixture.Initial
            && File.ReadAllText(fixture.Seed.File(".git/FETCH_HEAD")).StartsWith(fixture.Initial + "\t", StringComparison.Ordinal),
            "The branch/prune overload fetches the selected remote branch without fetching the other remote's new commit.");
        // Explicit branch refspecs retain Git's own pruning scope. A whole
        // selected-remote prune is the operation that removes all stale refs.
        await fixture.Local.FetchRemoteAsync(Upstream, prune: true);
        Check(!await HasRefAsync(fixture.Local, Tracking(Upstream, "stale")), "Pruning the whole selected remote removes its stale tracking branch.");
        await CheckCheckoutAsync(fixture, before);
    }

    private static async Task DivergentMergeAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        (string local, string remote) = await DivergeAsync(fixture);
        await fixture.Local.SetConfigAsync("pull.rebase", "true");
        await fixture.Local.SetConfigAsync("pull.ff", "true");
        await fixture.Local.PullMergeAsync(Upstream, "main");

        GitCommit merged = (await fixture.Local.GetLogAsync()).First();
        Check(merged.Parents.SequenceEqual(new[] { local, remote }), "The explicitly chosen merge overrides pull.rebase=true and creates the two real parents in Git order.");
        Check(File.ReadAllText(fixture.Seed.File("local.txt")) == "Local divergence\n"
            && File.ReadAllText(fixture.Seed.File("remote.txt")) == "Remote divergence\n", "Merge preserves both independently changed files.");
        Check((await fixture.Local.GetConfigAsync("^pull\\.rebase$")).Single().Value == "true", "A one-off UI mode does not rewrite the user's Git configuration.");
    }

    private static async Task DivergentRebaseAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        (string local, string remote) = await DivergeAsync(fixture);
        await fixture.Local.SetConfigAsync("pull.rebase", "false");
        await fixture.Local.PullRebaseAsync();

        GitCommit rebased = (await fixture.Local.GetLogAsync()).First();
        Check(rebased.Hash != local && rebased.Parents.SequenceEqual(new[] { remote }) && rebased.Subject == "Local divergence",
            "The explicit rebase uses the configured upstream, rewrites the local commit and places it immediately above the fetched remote commit.");
        Check((await fixture.Local.RunAsync(["rev-list", "--min-parents=2", "HEAD"])).StandardOutput.Length == 0, "No merge commit is introduced into the rebased history.");
        Check(File.ReadAllText(fixture.Seed.File("local.txt")) == "Local divergence\n"
            && File.ReadAllText(fixture.Seed.File("remote.txt")) == "Remote divergence\n", "The linear history retains both real file changes.");
        Check((await fixture.Local.GetConfigAsync("^pull\\.rebase$")).Single().Value == "false", "The explicit rebase does not persist a different pull.rebase value.");
    }

    private static async Task NativeFastForwardPrecedenceAsync()
    {
        using (NetworkFixture fixture = await NetworkFixture.CreateAsync())
        {
            await DivergeAsync(fixture);
            await fixture.Local.SetConfigAsync("pull.ff", "only");
            Snapshot before = await CaptureAsync(fixture);
            GitCommandException failure = await ExpectAsync<GitCommandException>(() => fixture.Local.PullMergeAsync(Upstream, "main"));
            Check(failure.Result.ExitCode != 0 && !string.IsNullOrWhiteSpace(failure.Result.StandardError), "Merge surfaces the configured ff-only rejection as a real Git error.");
            await CheckCheckoutAsync(fixture, before);
            Check(!File.Exists(fixture.Seed.File(".git/MERGE_HEAD")) && !Directory.Exists(fixture.Seed.File(".git/rebase-merge"))
                && !Directory.Exists(fixture.Seed.File(".git/rebase-apply")), "The rejected pull must not enter a merge or rebase operation.");
        }

        using NetworkFixture native = await NetworkFixture.CreateAsync();
        using NetworkFixture portable = await NetworkFixture.CreateAsync();
        (string nativeLocal, string nativeRemote) = await DivergeAsync(native);
        (string portableLocal, string portableRemote) = await DivergeAsync(portable);
        await native.Local.SetConfigAsync("pull.ff", "only");
        await portable.Local.SetConfigAsync("pull.ff", "only");
        Snapshot nativeBefore = await CaptureAsync(native), portableBefore = await CaptureAsync(portable);

        // Invoke installed Git independently of PullRebaseAsync. Git versions
        // can give an explicit --rebase precedence over configured pull.ff=only;
        // the portable workflow must preserve that native policy.
        GitResult oracle = await new GitRunner().RunAsync(native.Local.Path,
            ["--no-pager", "-c", "color.ui=false", "pull", "--rebase", "--", Upstream, "main"]);
        if (oracle.Success)
        {
            await portable.Local.PullRebaseAsync(Upstream, "main");
            await VerifyRebasedAsync(native, nativeBefore, nativeLocal, nativeRemote);
            await VerifyRebasedAsync(portable, portableBefore, portableLocal, portableRemote);
        }
        else
        {
            GitCommandException failure = await ExpectAsync<GitCommandException>(() => portable.Local.PullRebaseAsync(Upstream, "main"));
            Check(failure.Result.ExitCode == oracle.ExitCode && !string.IsNullOrWhiteSpace(failure.Result.StandardError),
                "If installed Git refuses explicit rebase, the portable workflow surfaces the same native refusal.");
            await CheckCheckoutAsync(native, nativeBefore);
            await CheckCheckoutAsync(portable, portableBefore);
        }

        static async Task VerifyRebasedAsync(NetworkFixture fixture, Snapshot before, string previousLocal, string remote)
        {
            GitCommit rebased = (await fixture.Local.GetLogAsync()).First();
            Check(rebased.Hash != previousLocal && rebased.Parents.SequenceEqual(new[] { remote }) && rebased.Subject == "Local divergence",
                "Successful explicit rebase rewrites the local commit directly above its own remote history.");
            Check((await fixture.Local.RunAsync(["rev-list", "--min-parents=2", "HEAD"])).StandardOutput.Length == 0,
                "The native and portable successful results both have linear history without a merge commit.");
            Check(File.ReadAllText(fixture.Seed.File("local.txt")) == "Local divergence\n"
                && File.ReadAllText(fixture.Seed.File("remote.txt")) == "Remote divergence\n"
                && File.ReadAllBytes(fixture.Seed.File("seed.txt")).AsSpan().SequenceEqual(before.Files["seed.txt"]),
                "Rebase preserves the original file and both real changes.");
            Check((await fixture.Local.GetStatusAsync()).Changes.Count == 0, "Successful native and portable rebases leave a clean index and worktree.");
            Check(File.ReadAllBytes(fixture.Seed.File(".git/config")).AsSpan().SequenceEqual(before.Config),
                "Rebase precedence does not rewrite pull.ff=only or any other configured value.");
        }
    }

    private static async Task NoFastForwardAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        string remote = await AdvanceAsync(fixture.UpstreamWriter, "remote.txt", "Remote fast-forward change");
        await fixture.Local.SetConfigAsync("pull.ff", "false");
        await fixture.Local.PullMergeAsync();
        GitCommit merged = (await fixture.Local.GetLogAsync()).First();
        Check(merged.Parents.SequenceEqual(new[] { fixture.Initial, remote }), "Configured pull.ff=false creates a merge commit even when fast-forwarding was possible.");
        Check((await fixture.Local.GetConfigAsync("^pull\\.ff$")).Single().Value == "false", "The explicit merge mode preserves the configured fast-forward policy.");
    }

    private static async Task ErrorsAndPreCancellationAsync()
    {
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        fixture.Seed.Write("seed.txt", "Locally modified and staged\n");
        await fixture.Local.StageAsync(["seed.txt"]);
        fixture.Seed.Write("seed.txt", "Locally modified again outside the index\n");
        Snapshot before = await CaptureAsync(fixture);
        foreach (Func<Task<GitResult>> operation in new Func<Task<GitResult>>[]
        {
            () => fixture.Local.FetchRemoteAsync("missing-remote"),
            () => fixture.Local.FetchRemoteBranchAsync(Upstream, "missing-branch"),
            () => fixture.Local.PullMergeAsync("missing-remote", "main"),
            () => fixture.Local.PullRebaseAsync("missing-remote", "main")
        })
        {
            GitCommandException failure = await ExpectAsync<GitCommandException>(() => operation());
            Check(failure.Result.ExitCode != 0 && failure.Result.StandardError.Length > 0, "Unavailable remote/ref errors are retained for the user.");
            await CheckCheckoutAsync(fixture, before, checkRefs: true);
        }
        await ExpectAsync<ArgumentException>(() => fixture.Local.PullMergeAsync(branch: "main"));
        await ExpectAsync<ArgumentException>(() => fixture.Local.PullRebaseAsync(branch: "main"));

        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        foreach (Func<Task<GitResult>> operation in new Func<Task<GitResult>>[]
        {
            () => fixture.Local.FetchConfiguredRemoteAsync(cancelled.Token),
            () => fixture.Local.FetchRemoteAsync(First, cancellationToken: cancelled.Token),
            () => fixture.Local.FetchRemoteBranchAsync(Upstream, "main", cancellationToken: cancelled.Token),
            () => fixture.Local.FetchAllAsync(cancelled.Token),
            () => fixture.Local.FetchPruneAllAsync(cancelled.Token),
            () => fixture.Local.PullMergeAsync(Upstream, "main", cancelled.Token),
            () => fixture.Local.PullRebaseAsync(Upstream, "main", cancelled.Token)
        })
        {
            await ExpectAsync<OperationCanceledException>(() => operation());
            await CheckCheckoutAsync(fixture, before, checkRefs: true);
        }
    }

    private static async Task RunningCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The live network cancellation scenario verifies Linux procfs descendants."); }
        using NetworkFixture fixture = await NetworkFixture.CreateAsync();
        Snapshot before = await CaptureAsync(fixture);
        foreach (string command in new[] { "fetch", "pull" })
        {
            string executable = Path.Combine(fixture.Seed.Root, "slow-" + command + "-git");
            string parentFile = Path.Combine(fixture.Seed.Root, command + ".pid"), childFile = Path.Combine(fixture.Seed.Root, command + ".child");
            // Repository reads delegate to installed Git. The requested network
            // command stops before I/O, with a real child process to terminate.
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nfor arg in \"$@\"; do\n if [ \"$arg\" = " + Quote(command)
                + " ]; then\n  printf '%s' \"$$\" > " + Quote(parentFile) + "\n  sleep 60 &\n  printf '%s' \"$!\" > " + Quote(childFile)
                + "\n  wait\n  exit 1\n fi\ndone\nexec git \"$@\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GitRepository gated = new(fixture.Local.Path, new GitRunner(executable));
            using CancellationTokenSource cancel = new();
            Task<GitResult> pending = command == "fetch" ? gated.FetchAllAsync(cancel.Token) : gated.PullRebaseAsync(Upstream, "main", cancel.Token);
            try
            {
                Stopwatch waiting = Stopwatch.StartNew();
                while ((!File.Exists(childFile) || new FileInfo(childFile).Length == 0) && !pending.IsCompleted && waiting.Elapsed < TimeSpan.FromSeconds(10))
                { await Task.Delay(10); }
                Check(File.Exists(childFile) && new FileInfo(childFile).Length > 0, "The " + command + " command actually reached the running process gate.");
                int parent = int.Parse(await File.ReadAllTextAsync(parentFile), CultureInfo.InvariantCulture);
                int child = int.Parse(await File.ReadAllTextAsync(childFile), CultureInfo.InvariantCulture);
                cancel.Cancel();
                await ExpectAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                waiting.Restart();
                while ((Running(parent) || Running(child)) && waiting.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(10); }
                Check(!Running(parent) && !Running(child), "Cancelling " + command + " terminates Git and its descendant.");
                await CheckCheckoutAsync(fixture, before, checkRefs: true);
                Check(await gated.IsRepositoryAsync(), "The repository runner remains usable after cancelling its network command.");
            }
            finally
            {
                cancel.Cancel();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task<string> AdvanceAsync(GitRepository writer, string path, string message, string branch = "main", bool force = false)
    {
        await File.WriteAllTextAsync(Path.Combine(writer.Path, path), message + "\n");
        await writer.StageAsync([path]);
        await writer.CommitAsync(message);
        await writer.RunAsync(force ? ["push", "--force", "origin", branch] : ["push", "origin", branch]);
        return await writer.ResolveRevisionAsync("HEAD");
    }

    private static async Task TagAndPushAsync(GitRepository writer, string name)
    {
        await writer.RunAsync(["tag", name]);
        await writer.RunAsync(["push", "origin", "refs/tags/" + name]);
    }

    private static async Task<(string Local, string Remote)> DivergeAsync(NetworkFixture fixture)
    {
        fixture.Seed.Write("local.txt", "Local divergence\n");
        await fixture.Local.StageAsync(["local.txt"]);
        await fixture.Local.CommitAsync("Local divergence");
        string local = await fixture.Local.ResolveRevisionAsync("HEAD");
        string remote = await AdvanceAsync(fixture.UpstreamWriter, "remote.txt", "Remote divergence");
        return (local, remote);
    }

    private sealed record Snapshot(string Head, byte[] Index, byte[] Config, IReadOnlyDictionary<string, byte[]> Files, string References);
    private static async Task<Snapshot> CaptureAsync(NetworkFixture fixture)
        => new(await fixture.Local.ResolveRevisionAsync("HEAD"), File.ReadAllBytes(fixture.Seed.File(".git/index")),
            File.ReadAllBytes(fixture.Seed.File(".git/config")), WorktreeFiles(fixture.Local), await ReferencesAsync(fixture.Local));

    private static async Task CheckCheckoutAsync(NetworkFixture fixture, Snapshot before, bool checkRefs = false)
    {
        Check(await fixture.Local.ResolveRevisionAsync("HEAD") == before.Head, "The current checkout's HEAD remains unchanged.");
        Check(File.ReadAllBytes(fixture.Seed.File(".git/index")).AsSpan().SequenceEqual(before.Index), "All index entries remain byte-exact.");
        Check(File.ReadAllBytes(fixture.Seed.File(".git/config")).AsSpan().SequenceEqual(before.Config), "Network operations do not rewrite configuration.");
        IReadOnlyDictionary<string, byte[]> files = WorktreeFiles(fixture.Local);
        Check(files.Count == before.Files.Count && before.Files.All(file => files.TryGetValue(file.Key, out byte[]? contents)
            && contents.AsSpan().SequenceEqual(file.Value)), "Every local file retains its exact pathname and content.");
        if (checkRefs) { Check(await ReferencesAsync(fixture.Local) == before.References, "Failure/cancellation does not change any local or remote-tracking ref."); }
    }

    private static IReadOnlyDictionary<string, byte[]> WorktreeFiles(GitRepository repository)
        => Directory.EnumerateFiles(repository.Path, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(repository.Path, file).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(file => Path.GetRelativePath(repository.Path, file), File.ReadAllBytes, StringComparer.Ordinal);

    private static async Task<string> ReferencesAsync(GitRepository repository)
        => string.Join('\n', (await repository.GetReferencesAsync()).OrderBy(reference => reference.Name, StringComparer.Ordinal)
            .Select(reference => reference.Name + "\t" + reference.Hash + "\t" + reference.Upstream));
    private static string Tracking(string remote, string branch = "main") => "refs/remotes/" + remote + "/" + branch;
    private static Task<string> RefAsync(GitRepository repository, string reference) => repository.ResolveRevisionAsync(reference);
    private static async Task<bool> HasRefAsync(GitRepository repository, string reference)
        => (await repository.GetReferencesAsync()).Any(item => item.Name == reference);
    private static bool Running(int pid) => LinuxProcessState.IsRunning(pid);
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> ExpectAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class NetworkFixture : IDisposable
    {
        private NetworkFixture(RepositoryFixture seed, GitRepository firstBare, GitRepository upstreamBare,
            GitRepository firstWriter, GitRepository upstreamWriter, string initial)
        { Seed = seed; FirstBare = firstBare; UpstreamBare = upstreamBare; FirstWriter = firstWriter; UpstreamWriter = upstreamWriter; Initial = initial; }

        public RepositoryFixture Seed { get; }
        public GitRepository Local => Seed.Repository;
        public GitRepository FirstBare { get; }
        public GitRepository UpstreamBare { get; }
        public GitRepository FirstWriter { get; }
        public GitRepository UpstreamWriter { get; }
        public string Initial { get; }

        public static async Task<NetworkFixture> CreateAsync()
        {
            RepositoryFixture seed = await RepositoryFixture.CreateAsync();
            try
            {
                GitRepository firstBare = await GitRepository.InitAsync(Path.Combine(seed.Root, "first remote.git"), bare: true);
                GitRepository upstreamBare = await GitRepository.InitAsync(Path.Combine(seed.Root, "upstream remote.git"), bare: true);
                await seed.Repository.AddRemoteAsync(First, firstBare.Path);
                await seed.Repository.AddRemoteAsync(Upstream, upstreamBare.Path);
                await seed.Repository.PushAsync(First, "main");
                await seed.Repository.PushAsync(Upstream, "main", setUpstream: true);
                GitRepository firstWriter = await GitRepository.CloneAsync(firstBare.Path, Path.Combine(seed.Root, "first writer"));
                GitRepository upstreamWriter = await GitRepository.CloneAsync(upstreamBare.Path, Path.Combine(seed.Root, "upstream writer"));
                await RepositoryFixture.ConfigureAsync(firstWriter);
                await RepositoryFixture.ConfigureAsync(upstreamWriter);
                return new(seed, firstBare, upstreamBare, firstWriter, upstreamWriter, await seed.Repository.ResolveRevisionAsync("HEAD"));
            }
            catch { seed.Dispose(); throw; }
        }

        public void Dispose() => Seed.Dispose();
    }
}
