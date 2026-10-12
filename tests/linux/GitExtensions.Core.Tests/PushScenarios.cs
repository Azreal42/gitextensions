using System.Diagnostics;
using System.Globalization;
using GitExtensions.Linux;

internal static class PushScenarios
{
    private const string First = "a-first";
    private const string Upstream = "z-upstream";

    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("push separates local source, remote destination branch and destination repository", ExplicitMappingsAsync),
        ("push replaces tracking only when selected and retains native push configuration", TrackingAndConfigurationAsync),
        ("push HEAD follows actual detached checkout and works from bare repositories", DetachedAndBareAsync),
        ("push single and all tags disambiguates branch names and confirms force in its options", TagsAsync),
        ("push all branches retains tags and establishes each selected upstream", AllBranchesAsync),
        ("push multiple maps branches and limits force and deletion to individual rows", MultipleAsync),
        ("push with lease rejects a stale remote and succeeds after its tracking ref is refreshed", ForceWithLeaseAsync),
        ("push honors rejecting client and server hooks without changing repository state", HooksAsync),
        ("push errors pre-cancellation and invalid requests leave local and remote state intact", ErrorsAndValidationAsync),
        ("push cancellation terminates its real pre-push hook and descendants before remote updates", RunningCancellationAsync),
        ("push checks missing submodule commits and on-demand pushes their local remote first", RecursiveSubmodulesAsync)
    ];

    private static async Task ExplicitMappingsAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        string actualHead = await CommitAsync(fixture.Local, "local.txt", "Actual checked-out HEAD");
        await fixture.Local.CreateBranchAsync("other", fixture.Initial, checkout: false);
        Snapshot before = await CaptureAsync(fixture);
        GitResult result = await fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "HEAD", DestinationRef: "published/été-180"));
        Check(result.Success && result.StandardError.Contains("published/été-180", StringComparison.Ordinal), "Successful push retains useful native progress/ref output.");
        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/published/été-180") == actualHead,
            "The destination receives the actual HEAD, independently of an older branch/revision selected elsewhere.");
        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/main") == fixture.Initial && await RefAsync(fixture.FirstBare, "refs/heads/main") == fixture.Initial,
            "Neither the upstream's main branch nor the first configured remote is accidentally pushed.");
        await CheckCheckoutAsync(fixture, before);

        await fixture.Local.PushAsync(new GitPushRequest(fixture.FirstBare.Path, Source: "refs/heads/other:refs/heads/different"));
        Check(await RefAsync(fixture.FirstBare, "refs/heads/different") == fixture.Initial, "An explicit refspec and local URL are passed literally and separately.");
        Check(!await HasRefAsync(fixture.UpstreamBare, "refs/heads/different"), "A direct URL destination does not silently become the selected remote.");
        await CheckCheckoutAsync(fixture, before);

        await fixture.Local.SetRemoteUrlAsync(Upstream, fixture.FirstBare.Path, push: true);
        Snapshot configured = await CaptureAsync(fixture);
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "HEAD", DestinationRef: "push-url-target"));
        Check(await RefAsync(fixture.FirstBare, "refs/heads/push-url-target") == actualHead && !await HasRefAsync(fixture.UpstreamBare, "refs/heads/push-url-target"),
            "Selecting a remote retains its configured push URL instead of using its fetch URL.");
        await CheckCheckoutAsync(fixture, configured);
    }

    private static async Task TrackingAndConfigurationAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main", DestinationRef: "tracking/été", SetUpstream: true));
        Check((await fixture.Local.GetReferencesAsync()).Single(reference => reference.IsCurrent).Upstream == Upstream + "/tracking/été",
            "Explicit replace tracking establishes the distinct destination branch as upstream.");
        Check((await fixture.Local.GetConfigAsync("^branch\\.main\\.merge$")).Single().Value == "refs/heads/tracking/été", "The tracking merge ref is the actual pushed destination.");
        await fixture.Local.SetConfigAsync("push.default", "upstream");
        await fixture.Local.SetConfigAsync("push.followTags", "true");
        string head = await CommitAsync(fixture.Local, "tracked.txt", "Configured upstream push");
        await fixture.Local.CreateTagAsync("followed-tag", message: "Annotated tag followed by native Git");
        Snapshot before = await CaptureAsync(fixture);

        await fixture.Local.PushAsync(new GitPushRequest(Upstream));

        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/tracking/été") == head && await RefAsync(fixture.UpstreamBare, "refs/heads/main") == fixture.Initial,
            "An omitted refspec uses native push.default=upstream, including an upstream with a different name.");
        Check(await HasRefAsync(fixture.UpstreamBare, "refs/tags/followed-tag"), "Native push.followTags remains effective.");
        await CheckCheckoutAsync(fixture, before);

        await fixture.Local.SetConfigAsync("remote." + Upstream + ".push", "refs/heads/main:refs/heads/configured-push");
        Snapshot mapped = await CaptureAsync(fixture);
        await fixture.Local.PushAsync(new GitPushRequest(Upstream));
        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/configured-push") == head, "A configured remote.push refspec remains effective when none is entered.");
        await CheckCheckoutAsync(fixture, mapped);
    }

    private static async Task DetachedAndBareAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        await fixture.Local.CheckoutAsync(fixture.Initial, detached: true);
        string detached = await CommitAsync(fixture.Local, "detached.txt", "Detached actual HEAD");
        Snapshot before = await CaptureAsync(fixture);
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, DestinationRef: "detached/published"));
        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/detached/published") == detached, "An omitted source with a destination maps actual detached HEAD, as the original workflow does.");
        Check(await RefAsync(fixture.Local, "refs/heads/main") == fixture.Initial && (await fixture.Local.GetStatusAsync()).IsDetached,
            "Pushing detached HEAD neither changes nor attaches the current checkout.");
        await CheckCheckoutAsync(fixture, before);

        Check((await fixture.UpstreamBare.GetInfoAsync()).IsBare, "The source really is a bare repository.");
        await fixture.UpstreamBare.PushAsync(new GitPushRequest(fixture.FirstBare.Path, Source: "HEAD", DestinationRef: "bare/published"));
        Check(await RefAsync(fixture.FirstBare, "refs/heads/bare/published") == fixture.Initial, "A bare source can push its symbolic HEAD without a worktree operation.");
        Check(!Directory.Exists(Path.Combine(fixture.UpstreamBare.Path, ".git")), "Pushing did not create a worktree inside the bare repository.");
    }

    private static async Task TagsAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        await fixture.Local.CreateTagAsync("shared", fixture.Initial);
        string head = await CommitAsync(fixture.Local, "tagged.txt", "New tag content");
        await fixture.Local.CreateBranchAsync("shared", head, checkout: false);
        Snapshot before = await CaptureAsync(fixture);
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, GitPushMode.Tag, "shared"));
        Check(await RefAsync(fixture.UpstreamBare, "refs/tags/shared") == fixture.Initial && !await HasRefAsync(fixture.UpstreamBare, "refs/heads/shared"),
            "Tag mode names refs/tags explicitly even when a branch has the same name.");
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "shared", DestinationRef: "branch-shared"));
        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/branch-shared") == head,
            "A selected short local branch is qualified against its actual ref and cannot accidentally push a colliding tag.");
        await fixture.Local.CreateTagAsync("second", head);
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, GitPushMode.AllTags));
        Check(await RefAsync(fixture.UpstreamBare, "refs/tags/second") == head && await RefAsync(fixture.UpstreamBare, "refs/heads/main") == fixture.Initial,
            "All-tags mode pushes every tag while leaving all branch tips intact.");
        await CheckCheckoutAsync(fixture, before, checkLocalRefs: false);

        await fixture.Local.RunAsync(["tag", "--force", "shared", head]);
        Snapshot rewritten = await CaptureAsync(fixture);
        GitCommandException rejected = await ExpectAsync<GitCommandException>(() => fixture.Local.PushAsync(new GitPushRequest(Upstream, GitPushMode.Tag, "shared")));
        Check(rejected.Result.StandardError.Contains("[rejected]", StringComparison.Ordinal), "A tag replacement without force preserves the native rejection.");
        await CheckUnchangedAsync(fixture, rewritten);
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, GitPushMode.Tag, "shared", Force: GitPushForce.Force));
        Check(await RefAsync(fixture.UpstreamBare, "refs/tags/shared") == head, "Explicit force can replace an existing tag.");
        await CheckCheckoutAsync(fixture, rewritten);
    }

    private static async Task AllBranchesAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        string head = await CommitAsync(fixture.Local, "all.txt", "All branch tip");
        await fixture.Local.CreateBranchAsync("other/été", fixture.Initial, checkout: false);
        await fixture.Local.CreateTagAsync("local-only", head);
        byte[] index = File.ReadAllBytes(fixture.Seed.File(".git/index"));
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, GitPushMode.AllBranches, SetUpstream: true));
        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/main") == head && await RefAsync(fixture.UpstreamBare, "refs/heads/other/été") == fixture.Initial,
            "All-branches mode pushes actual local branch refs.");
        Check(!await HasRefAsync(fixture.UpstreamBare, "refs/tags/local-only"), "All-branches mode does not silently push local tags.");
        Check((await fixture.Local.GetReferencesAsync()).Where(reference => reference.IsBranch).All(reference => reference.Upstream == Upstream + "/" + reference.ShortName),
            "Selected tracking replacement establishes upstreams for every successfully pushed local branch.");
        Check(await RefAsync(fixture.FirstBare, "refs/heads/main") == fixture.Initial && !await HasRefAsync(fixture.FirstBare, "refs/heads/other/été"), "All branches remains scoped to the chosen remote.");
        Check(await fixture.Local.ResolveRevisionAsync("HEAD") == head && File.ReadAllBytes(fixture.Seed.File(".git/index")).AsSpan().SequenceEqual(index),
            "Pushing all branches preserves the actual checkout and index.");
    }

    private static async Task MultipleAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        string remote = await CommitAsync(fixture.Writer, "remote.txt", "Remote history to overwrite");
        await fixture.Writer.PushAsync(new GitPushRequest("origin", Source: "HEAD", DestinationRef: "force-target"));
        await fixture.UpstreamBare.RunAsync(["update-ref", "refs/heads/delete-only", fixture.Initial]);
        await fixture.UpstreamBare.RunAsync(["update-ref", "refs/heads/untouched", fixture.Initial]);
        string local = await CommitAsync(fixture.Local, "safe.txt", "Safe ordinary row");
        await fixture.Local.CreateBranchAsync("safe", local, checkout: false);
        await fixture.Local.CreateTagAsync("safe", fixture.Initial);
        Snapshot before = await CaptureAsync(fixture);
        Check(remote != fixture.Initial && !await HasRefAsync(fixture.Local, "refs/heads/delete-only"), "The force row is a real non-fast-forward and the deletion row has no local branch.");
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, GitPushMode.Multiple, Actions:
        [
            new("safe", "published/safe"),
            new("main", "force-target", Force: true),
            new(null, "delete-only", Delete: true)
        ]));
        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/published/safe") == local && await RefAsync(fixture.UpstreamBare, "refs/heads/force-target") == local,
            "Ordinary and per-row forced mappings both reach their distinct destinations, even with a branch/tag source collision.");
        Check(!await HasRefAsync(fixture.UpstreamBare, "refs/heads/delete-only") && await RefAsync(fixture.UpstreamBare, "refs/heads/untouched") == fixture.Initial,
            "Only the explicitly selected remote-only branch is deleted.");
        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/main") == fixture.Initial, "Individual row force does not become a global force or an all-branches push.");
        await CheckCheckoutAsync(fixture, before);
    }

    private static async Task ForceWithLeaseAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        string local = await CommitAsync(fixture.Local, "local.txt", "Local replacement");
        string remote = await CommitAsync(fixture.Writer, "remote.txt", "Unseen remote advancement");
        await fixture.Writer.PushAsync(new GitPushRequest("origin", Source: "main"));
        Check(await RefAsync(fixture.Local, "refs/remotes/" + Upstream + "/main") == fixture.Initial, "The lease's tracking expectation is genuinely stale.");
        Snapshot before = await CaptureAsync(fixture);
        GitCommandException plain = await ExpectAsync<GitCommandException>(() => fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main", DestinationRef: "main")));
        Check(plain.Result.StandardError.Contains("[rejected]", StringComparison.Ordinal), "Divergence produces a native non-fast-forward rejection.");
        await CheckUnchangedAsync(fixture, before);
        GitCommandException stale = await ExpectAsync<GitCommandException>(() => fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main", DestinationRef: "main", Force: GitPushForce.ForceWithLease)));
        Check(stale.Result.StandardError.Contains("stale info", StringComparison.Ordinal) && await RefAsync(fixture.UpstreamBare, "refs/heads/main") == remote,
            "Force-with-lease refuses to overwrite a remote change that has not been fetched.");
        await CheckUnchangedAsync(fixture, before);
        await fixture.Local.FetchRemoteAsync(Upstream);
        Snapshot refreshed = await CaptureAsync(fixture);
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main", DestinationRef: "main", Force: GitPushForce.ForceWithLease));
        Check(await RefAsync(fixture.UpstreamBare, "refs/heads/main") == local, "The same lease succeeds only after the tracking expectation matches the current remote.");
        await CheckCheckoutAsync(fixture, refreshed);
    }

    private static async Task HooksAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        await CommitAsync(fixture.Local, "hook.txt", "A push rejected by hooks");
        string clientMarker = Path.Combine(fixture.Seed.Root, "client-hook-ran");
        string clientHooks = Path.Combine(fixture.Seed.Root, "client hooks");
        WriteHook(clientHooks, "pre-push", "printf ran > " + Quote(clientMarker) + "\nprintf 'client denies push\\n' >&2\nexit 1\n");
        await fixture.Local.SetConfigAsync("core.hooksPath", clientHooks);
        Snapshot client = await CaptureAsync(fixture);
        GitCommandException clientFailure = await ExpectAsync<GitCommandException>(() => fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main")));
        Check(File.Exists(clientMarker) && clientFailure.Result.StandardError.Contains("client denies push", StringComparison.Ordinal), "The local pre-push hook actually runs and its useful error is retained.");
        await CheckUnchangedAsync(fixture, client);

        await fixture.Local.SetConfigAsync("core.hooksPath", "/dev/null");
        string serverMarker = Path.Combine(fixture.Seed.Root, "server-hook-ran");
        string serverHooks = Path.Combine(fixture.Seed.Root, "server hooks");
        WriteHook(serverHooks, "pre-receive", "printf ran > " + Quote(serverMarker) + "\nprintf 'server denies push\\n' >&2\nexit 1\n");
        await fixture.UpstreamBare.SetConfigAsync("core.hooksPath", serverHooks);
        Snapshot server = await CaptureAsync(fixture);
        GitCommandException serverFailure = await ExpectAsync<GitCommandException>(() => fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main")));
        Check(File.Exists(serverMarker) && serverFailure.Result.StandardError.Contains("server denies push", StringComparison.Ordinal)
            && serverFailure.Result.StandardError.Contains("[remote rejected]", StringComparison.Ordinal), "Server hook rejection stays distinguishable from a recoverable non-fast-forward rejection.");
        await CheckUnchangedAsync(fixture, server);
    }

    private static async Task ErrorsAndValidationAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        fixture.Seed.Write("seed.txt", "Staged local contents\n"); await fixture.Local.StageAsync(["seed.txt"]);
        fixture.Seed.Write("seed.txt", "Unstaged local contents\n");
        fixture.Seed.Write("untracked.txt", "Untracked contents\n");
        Snapshot before = await CaptureAsync(fixture);
        foreach (GitPushRequest request in new[]
        {
            new GitPushRequest("missing-remote", Source: "main"),
            new GitPushRequest(Upstream, Source: "missing-local-source", DestinationRef: "missing-destination"),
            new GitPushRequest(Upstream, Source: "main", DestinationRef: "invalid..name")
        })
        {
            GitCommandException failure = await ExpectAsync<GitCommandException>(() => fixture.Local.PushAsync(request));
            Check(failure.Result.ExitCode != 0 && failure.Result.StandardError.Length > 0, "Git source/destination errors preserve native exit code and diagnostic output.");
            await CheckUnchangedAsync(fixture, before);
        }
        foreach (GitPushRequest request in new[]
        {
            new GitPushRequest("--mirror", Source: "main"),
            new GitPushRequest(Upstream, Source: "--all"),
            new GitPushRequest(Upstream, Source: "HEAD:refs/heads/x", DestinationRef: "y"),
            new GitPushRequest(Upstream, GitPushMode.AllBranches, Source: "main"),
            new GitPushRequest(Upstream, GitPushMode.Multiple, Actions: []),
            new GitPushRequest(Upstream, GitPushMode.Multiple, Actions: [new("main", "valid"), new(null, "invalid")]),
            new GitPushRequest(Upstream, GitPushMode.Multiple, Actions: [new("main", "invalid", Delete: true)]),
            new GitPushRequest(Upstream, GitPushMode.Multiple, Actions: [new(null, "invalid", Force: true, Delete: true)]),
            new GitPushRequest(Upstream, (GitPushMode)99)
        })
        {
            await ExpectAsync<ArgumentException>(() => fixture.Local.PushAsync(request));
            await CheckUnchangedAsync(fixture, before);
        }
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        foreach (GitPushMode mode in Enum.GetValues<GitPushMode>())
        {
            await ExpectAsync<OperationCanceledException>(() => fixture.Local.PushAsync(new GitPushRequest(Upstream, mode, mode is GitPushMode.Tag ? "missing" : null,
                Actions: mode is GitPushMode.Multiple ? [new("main", "main")] : null), cancelled.Token));
            await CheckUnchangedAsync(fixture, before);
        }
    }

    private static async Task RunningCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Push process cancellation is verified using Linux procfs."); }
        using PushFixture fixture = await PushFixture.CreateAsync();
        await CommitAsync(fixture.Local, "cancelled.txt", "Candidate cancelled before transfer");
        string parentFile = Path.Combine(fixture.Seed.Root, "pre-push.pid"), childFile = Path.Combine(fixture.Seed.Root, "pre-push.child");
        string hooks = Path.Combine(fixture.Seed.Root, "slow hooks");
        WriteHook(hooks, "pre-push", "printf '%s' \"$$\" > " + Quote(parentFile) + "\nsleep 60 &\nprintf '%s' \"$!\" > " + Quote(childFile) + "\nwait\n");
        await fixture.Local.SetConfigAsync("core.hooksPath", hooks);
        Snapshot before = await CaptureAsync(fixture);
        using CancellationTokenSource cancel = new();
        Task<GitResult> pending = fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main"), cancel.Token);
        try
        {
            Stopwatch waiting = Stopwatch.StartNew();
            while ((!File.Exists(childFile) || new FileInfo(childFile).Length == 0) && !pending.IsCompleted && waiting.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); }
            Check(File.Exists(childFile) && new FileInfo(childFile).Length > 0, "The actual Git process reaches the live pre-push hook before cancellation.");
            int parent = int.Parse(await File.ReadAllTextAsync(parentFile), CultureInfo.InvariantCulture);
            int child = int.Parse(await File.ReadAllTextAsync(childFile), CultureInfo.InvariantCulture);
            cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            waiting.Restart();
            while ((Running(parent) || Running(child)) && waiting.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(10); }
            Check(!Running(parent) && !Running(child), "Cancelling push terminates both the hook and its sleep descendant.");
            await CheckUnchangedAsync(fixture, before);
            Check(await fixture.Local.IsRepositoryAsync(), "The mutation lock and Git runner remain usable after cancellation.");
        }
        finally
        {
            cancel.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    private static async Task RecursiveSubmodulesAsync()
    {
        using PushFixture fixture = await PushFixture.CreateAsync();
        using RepositoryFixture subSeed = await RepositoryFixture.CreateAsync();
        GitRepository subBare = await GitRepository.InitAsync(Path.Combine(fixture.Seed.Root, "submodule remote.git"), bare: true);
        await RepositoryFixture.ConfigureAsync(subBare);
        await subSeed.Repository.AddRemoteAsync("origin", subBare.Path);
        await subSeed.Repository.PushAsync(new GitPushRequest("origin", Source: "main", SetUpstream: true));
        await fixture.Local.AddSubmoduleAsync(subBare.Path, "module with spaces");
        GitRepository module = await GitRepository.OpenAsync(Path.Combine(fixture.Local.Path, "module with spaces"));
        await RepositoryFixture.ConfigureAsync(module);
        // Native on-demand push passes the selected superproject remote name
        // to each submodule, so that name must also exist in the real fixture.
        await module.AddRemoteAsync(Upstream, subBare.Path);
        await module.FetchRemoteAsync(Upstream);
        await fixture.Local.CommitAsync("Add initialized submodule");
        await fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main"));
        string previous = await fixture.UpstreamBare.ResolveRevisionAsync("main");
        string subInitial = await subBare.ResolveRevisionAsync("main");
        string subHead = await CommitAsync(module, "sub-change.txt", "Submodule commit not yet on its remote");
        await fixture.Local.StageAsync(["module with spaces"]);
        await fixture.Local.CommitAsync("Point superproject at unpushed submodule commit");
        string superHead = await fixture.Local.ResolveRevisionAsync("HEAD");
        Snapshot before = await CaptureAsync(fixture);
        GitCommandException check = await ExpectAsync<GitCommandException>(() => fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main", Recursion: GitPushRecursion.Check)));
        Check(check.Result.StandardError.Contains("not be found on any remote", StringComparison.Ordinal), "Check mode actually detects the unpushed submodule revision.");
        Check(await subBare.ResolveRevisionAsync("main") == subInitial && await fixture.UpstreamBare.ResolveRevisionAsync("main") == previous,
            "A rejected submodule check leaves both physical local remotes unchanged.");
        await CheckUnchangedAsync(fixture, before);
        await fixture.Local.SetConfigAsync("push.recurseSubmodules", "check");
        Snapshot configured = await CaptureAsync(fixture);
        await ExpectAsync<GitCommandException>(() => fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main")));
        await CheckUnchangedAsync(fixture, configured);

        await fixture.Local.PushAsync(new GitPushRequest(Upstream, Source: "main", Recursion: GitPushRecursion.OnDemand));
        Check(await subBare.ResolveRevisionAsync("main") == subHead && await fixture.UpstreamBare.ResolveRevisionAsync("main") == superHead,
            "On-demand mode pushes the submodule commit before publishing its superproject gitlink.");
        await CheckCheckoutAsync(fixture, configured);
    }

    private static async Task<string> CommitAsync(GitRepository repository, string path, string contents)
    {
        await File.WriteAllTextAsync(Path.Combine(repository.Path, path), contents + "\n");
        await repository.StageAsync([path]); await repository.CommitAsync(contents);
        return await repository.ResolveRevisionAsync("HEAD");
    }

    private sealed record Snapshot(string Head, byte[] Index, byte[] Config, IReadOnlyDictionary<string, byte[]> Files, string LocalRefs, string FirstRefs, string UpstreamRefs);
    private static async Task<Snapshot> CaptureAsync(PushFixture fixture)
        => new(await fixture.Local.ResolveRevisionAsync("HEAD"), File.ReadAllBytes(fixture.Seed.File(".git/index")), File.ReadAllBytes(fixture.Seed.File(".git/config")),
            WorktreeFiles(fixture.Local), await ReferencesAsync(fixture.Local), await ReferencesAsync(fixture.FirstBare), await ReferencesAsync(fixture.UpstreamBare));

    private static async Task CheckCheckoutAsync(PushFixture fixture, Snapshot before, bool checkLocalRefs = true)
    {
        Check(await fixture.Local.ResolveRevisionAsync("HEAD") == before.Head, "Push preserves actual HEAD.");
        Check(File.ReadAllBytes(fixture.Seed.File(".git/index")).AsSpan().SequenceEqual(before.Index), "Push preserves every index entry byte-exactly.");
        Check(File.ReadAllBytes(fixture.Seed.File(".git/config")).AsSpan().SequenceEqual(before.Config), "Push does not rewrite configuration without replace tracking.");
        IReadOnlyDictionary<string, byte[]> files = WorktreeFiles(fixture.Local);
        Check(files.Count == before.Files.Count && before.Files.All(file => files.TryGetValue(file.Key, out byte[]? content) && content.AsSpan().SequenceEqual(file.Value)),
            "Push preserves every staged, unstaged and untracked file.");
        if (checkLocalRefs)
        {
            string branchesAndTags = string.Join('\n', (await fixture.Local.GetReferencesAsync()).Where(reference => reference.IsBranch || reference.IsTag)
                .OrderBy(reference => reference.Name, StringComparer.Ordinal).Select(reference => reference.Name + "\t" + reference.Hash + "\t" + reference.Upstream));
            string prior = string.Join('\n', before.LocalRefs.Split('\n').Where(line => line.StartsWith("refs/heads/", StringComparison.Ordinal) || line.StartsWith("refs/tags/", StringComparison.Ordinal)));
            Check(branchesAndTags == prior, "Push preserves local branches, tags and pre-existing tracking configuration.");
        }
    }

    private static async Task CheckUnchangedAsync(PushFixture fixture, Snapshot before)
    {
        await CheckCheckoutAsync(fixture, before);
        Check(await ReferencesAsync(fixture.Local) == before.LocalRefs, "A rejected, invalid or cancelled push preserves every local/tracking ref.");
        Check(await ReferencesAsync(fixture.FirstBare) == before.FirstRefs && await ReferencesAsync(fixture.UpstreamBare) == before.UpstreamRefs,
            "A rejected, invalid or cancelled push preserves both physical remote repositories.");
    }

    private static IReadOnlyDictionary<string, byte[]> WorktreeFiles(GitRepository repository)
        => Directory.EnumerateFiles(repository.Path, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(repository.Path, file).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(file => Path.GetRelativePath(repository.Path, file), File.ReadAllBytes, StringComparer.Ordinal);
    private static async Task<string> ReferencesAsync(GitRepository repository)
        => string.Join('\n', (await repository.GetReferencesAsync()).OrderBy(reference => reference.Name, StringComparer.Ordinal)
            .Select(reference => reference.Name + "\t" + reference.Hash + "\t" + reference.Upstream));
    private static Task<string> RefAsync(GitRepository repository, string reference) => repository.ResolveRevisionAsync(reference);
    private static async Task<bool> HasRefAsync(GitRepository repository, string reference) => (await repository.GetReferencesAsync()).Any(item => item.Name == reference);
    private static void WriteHook(string directory, string name, string body)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static bool Running(int pid) => LinuxProcessState.IsRunning(pid);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> ExpectAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T exception) { return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class PushFixture : IDisposable
    {
        private PushFixture(RepositoryFixture seed, GitRepository firstBare, GitRepository upstreamBare, GitRepository writer, string initial)
        { Seed = seed; FirstBare = firstBare; UpstreamBare = upstreamBare; Writer = writer; Initial = initial; }
        public RepositoryFixture Seed { get; }
        public GitRepository Local => Seed.Repository;
        public GitRepository FirstBare { get; }
        public GitRepository UpstreamBare { get; }
        public GitRepository Writer { get; }
        public string Initial { get; }
        public static async Task<PushFixture> CreateAsync()
        {
            RepositoryFixture seed = await RepositoryFixture.CreateAsync();
            try
            {
                GitRepository firstBare = await GitRepository.InitAsync(Path.Combine(seed.Root, "first remote.git"), bare: true);
                GitRepository upstreamBare = await GitRepository.InitAsync(Path.Combine(seed.Root, "upstream remote.git"), bare: true);
                await RepositoryFixture.ConfigureAsync(firstBare); await RepositoryFixture.ConfigureAsync(upstreamBare);
                await seed.Repository.AddRemoteAsync(First, firstBare.Path); await seed.Repository.AddRemoteAsync(Upstream, upstreamBare.Path);
                await seed.Repository.PushAsync(First, "main"); await seed.Repository.PushAsync(Upstream, "main", setUpstream: true);
                GitRepository writer = await GitRepository.CloneAsync(upstreamBare.Path, Path.Combine(seed.Root, "upstream writer"));
                await RepositoryFixture.ConfigureAsync(writer);
                return new(seed, firstBare, upstreamBare, writer, await seed.Repository.ResolveRevisionAsync("HEAD"));
            }
            catch { seed.Dispose(); throw; }
        }
        public void Dispose() => Seed.Dispose();
    }
}
