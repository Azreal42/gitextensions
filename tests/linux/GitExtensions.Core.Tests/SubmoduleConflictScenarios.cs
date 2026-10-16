using System.Diagnostics;
using System.Text;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Linux;

internal static class SubmoduleConflictScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("submodule conflict snapshots expose real ordinary add-add and delete-modify stages without requiring gitmodules", StagesAsync),
        ("submodule conflict Current refresh confirms an arbitrary real child commit before one parent gitlink stage", RefreshStageAsync),
        ("submodule conflict branches intersect both sides exclude symbolic refs and checkout a selected local branch", BranchesAsync),
        ("submodule conflict remote checkout preserves shared branch creation tracking stash and reset choices", RemoteCheckoutAsync),
        ("submodule conflict checkout refuses moved branches forged hashes noncontaining refs and mismatched options", BranchGuardsAsync),
        ("submodule conflict operations refuse selected or unrelated stale index entries without overwriting them", StaleIndexAsync),
        ("submodule conflict missing or uninitialized children cannot fall back to the parent or stage deletion", MissingAsync),
        ("submodule conflict child replacement parent aliases and symbolic traversal are refused before mutation", ReplacedAsync),
        ("submodule conflict preserves literal wildcard Unicode TAB LF trailing CR and backslash paths", LiteralAsync),
        ("submodule conflict native add failure streams diagnostics and executes add exactly once", StageFailureAsync),
        ("submodule conflict verifies the actual staged gitlink and reports a concurrent native HEAD change without rollback", StageVerificationAsync),
        ("submodule conflict shared checkout preserves native dirty-file refusal and the unresolved parent index", DirtyAsync),
        ("submodule conflict shared checkout retains a failing native hook and its partial child checkout", HookAsync),
        ("submodule conflict cancellation joins a live checkout hook and permits Current refresh after partial checkout", CheckoutCancellationAsync),
        ("submodule conflict cancellation before native add preserves all unresolved index bytes and joins the process", StageCancellationAsync),
        ("submodule conflict cancellation during post-add verification retains native stage zero and drains the blocked read", PostStageCancellationAsync),
        ("submodule conflict linked and descendant repositories use their own index and reject bare precancelled or foreign selections", LinkedBareAsync),
    ];

    private static async Task StagesAsync()
    {
        foreach (Layout layout in Enum.GetValues<Layout>())
        {
            using Fixture f = await Fixture.CreateAsync(layout); GitSubmoduleConflictSnapshot s = await f.Snapshot();
            Check(s.Base == (layout == Layout.AddAdd ? null : f.Base) && s.Local == (layout == Layout.LocalDeleted ? null : f.Local)
                && s.Remote == (layout == Layout.RemoteDeleted ? null : f.Remote), "Each actual merge exposes only the gitlink stages present in its index.");
            Check(s.Current == await f.Child.ResolveRevisionAsync("HEAD") && s.CanOpen && s.CanStageCurrent
                && s.CanCheckoutBranch == (s.Local is not null && s.Remote is not null), "Current is read from the exact child and checkout needs both sides, including add/add.");
            if (layout is Layout.LocalDeleted or Layout.RemoteDeleted)
            {
                Check(!File.ReadAllText(f.File(".gitmodules")).Contains("[submodule", StringComparison.Ordinal), "Deletion/modify is usable without a .gitmodules registration.");
                GitSubmoduleConflictStageResult stage = await f.Repository.StageSubmoduleConflictCurrentAsync(s);
                Check(stage.IsResolved && stage.StagedCommit == s.Current, "An existing child commit can resolve a deletion/modify conflict without deleting the gitlink.");
            }
        }
    }

    private static async Task RefreshStageAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleConflictSnapshot original = await f.Snapshot(); ParentWitness before = await f.Witness();
        await f.Child.CheckoutAsync(f.Arbitrary, detached: true);
        await Expect<InvalidOperationException>(() => f.Repository.StageSubmoduleConflictCurrentAsync(original));
        await Expect<InvalidOperationException>(() => f.Repository.OpenSubmoduleConflictRepositoryAsync(original));
        GitSubmoduleConflictSnapshot fresh = await f.Repository.RefreshSubmoduleConflictAsync(original);
        Check(fresh.Current == f.Arbitrary && fresh.Local == original.Local && fresh.Remote == original.Remote, "Refresh Current changes only the displayed actual child commit.");
        GitResult ancestor = await Native(f.Child.Path, "merge-base", "--is-ancestor", f.Local, f.Arbitrary);
        Check(ancestor.ExitCode == 1, "The chosen child commit deliberately does not contain the local conflict side.");
        GitSubmoduleConflictStageResult stage = await f.Repository.StageSubmoduleConflictCurrentAsync(fresh);
        Check(stage.Result.Success && stage.IsResolved && stage.StagedCommit == f.Arbitrary
            && (await Native(f.Repository.Path, "ls-files", "--unmerged", "-z")).StandardOutput.Length == 0
            && (await f.Witness()) == before, "One native add stages that exact arbitrary child commit, without committing or continuing the parent merge.");
        await Expect<InvalidOperationException>(() => f.Repository.StageSubmoduleConflictCurrentAsync(fresh));
    }

    private static async Task BranchesAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await f.Child.RunAsync(["branch", "both-local", f.Merged]);
        await f.Child.RunAsync(["symbolic-ref", "refs/heads/local-alias", "refs/heads/both-local"]);
        await f.Child.RunAsync(["symbolic-ref", "refs/remotes/origin/alias", "refs/remotes/origin/both"]);
        await f.Child.RunAsync(["symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/both"]);
        GitSubmoduleConflictSnapshot s = await f.Snapshot(); byte[] index = await f.Index();
        IReadOnlyList<GitReference> refs = await f.Repository.GetSubmoduleConflictBranchesAsync(s);
        Check(refs.Select(r => r.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "refs/heads/both-local", "refs/remotes/origin/both" })
            && refs.All(r => r.SymbolicTarget is null && r.Hash == f.Merged), "Only concrete local and remote refs containing both sides remain.");
        GitReference selected = refs.Single(r => r.IsBranch);
        GitCheckoutResult checkout = await f.Repository.CheckoutSubmoduleConflictBranchAsync(s, selected, new(selected.Name));
        Check(checkout.Result.Success && await f.Child.ResolveRevisionAsync("HEAD") == f.Merged && Bytes(index, await f.Index()), "Checkout uses the shared child engine without staging the parent.");
        GitSubmoduleConflictSnapshot current = await f.Repository.RefreshSubmoduleConflictAsync(s);
        Check(current.Current == f.Merged && (await f.Repository.StageSubmoduleConflictCurrentAsync(current)).IsResolved, "Explicit refresh and stage complete the selected resolution.");
    }

    private static async Task RemoteCheckoutAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleConflictSnapshot s = await f.Snapshot();
        GitReference remote = (await f.Repository.GetSubmoduleConflictBranchesAsync(s)).Single(r => r.Name == "refs/remotes/origin/both");
        await File.WriteAllTextAsync(f.ChildFile("pending.txt"), "untracked pending data\n");
        GitCheckoutOptions options = new(remote.Name, GitCheckoutKind.RemoteBranch, CheckoutNewBranchMode.Create, "resolved-local", LocalChangesAction.Stash);
        GitCheckoutResult result = await f.Repository.CheckoutSubmoduleConflictBranchAsync(s, remote, options);
        Check(result.Result.Success && result.CreatedStash is not null && !File.Exists(f.ChildFile("pending.txt"))
            && Line((await Native(f.Child.Path, "config", "--get", "branch.resolved-local.remote")).StandardOutput) == "origin", "Shared remote checkout creates tracking and retains its exact stash behavior.");
        s = await f.Repository.RefreshSubmoduleConflictAsync(s);
        Check(s.Current == f.Merged && s.CanStageCurrent, "A legitimate checkout tracking-config write does not invalidate the child repository identity.");
        await f.Child.CheckoutAsync(f.Local, detached: true); s = await f.Repository.RefreshSubmoduleConflictAsync(s);
        options = options with { NewBranchMode = CheckoutNewBranchMode.Reset, Changes = LocalChangesAction.DontChange };
        Check((await f.Repository.CheckoutSubmoduleConflictBranchAsync(s, remote, options)).Result.Success
            && await f.Child.ResolveRevisionAsync("refs/heads/resolved-local") == f.Merged, "Explicit shared reset mode remains available for a containing remote branch.");
    }

    private static async Task BranchGuardsAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleConflictSnapshot s = await f.Snapshot();
        GitReference selected = (await f.Repository.GetSubmoduleConflictBranchesAsync(s)).Single(r => r.Name == "refs/remotes/origin/both");
        byte[] index = await f.Index();
        await Expect<ArgumentException>(() => f.Repository.CheckoutSubmoduleConflictBranchAsync(s, selected, new("HEAD", GitCheckoutKind.Revision)));
        await Expect<ArgumentException>(() => f.Repository.CheckoutSubmoduleConflictBranchAsync(s, selected, new(selected.Name, GitCheckoutKind.LocalBranch)));
        await Expect<InvalidOperationException>(() => f.Repository.CheckoutSubmoduleConflictBranchAsync(s, selected with { Hash = f.Local }, new(selected.Name, GitCheckoutKind.RemoteBranch)));
        await f.Child.RunAsync(["update-ref", selected.Name, f.Local]);
        await Expect<InvalidOperationException>(() => f.Repository.CheckoutSubmoduleConflictBranchAsync(s, selected, new(selected.Name, GitCheckoutKind.RemoteBranch)));
        GitReference noncontaining = (await f.Child.GetReferencesAsync()).Single(r => r.Name == selected.Name);
        await Expect<InvalidOperationException>(() => f.Repository.CheckoutSubmoduleConflictBranchAsync(s, noncontaining, new(noncontaining.Name, GitCheckoutKind.RemoteBranch)));
        Check(await f.Child.ResolveRevisionAsync("HEAD") == f.Local && Bytes(index, await f.Index()), "Rejected target changes never checkout or stage anything.");
    }

    private static async Task StaleIndexAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleConflictSnapshot s = await f.Snapshot();
        f.Root.Write("unrelated.txt", "external stage\n"); await f.Repository.StageAsync(["unrelated.txt"]); byte[] index = await f.Index();
        await Refused(f.Repository, s);
        Check(Bytes(index, await f.Index()), "Changes elsewhere in the confirmed conflict index are preserved and require reopening.");
        s = await f.Snapshot(); await f.Repository.RunAsync(["--literal-pathspecs", "add", "--", f.Path]); index = await f.Index();
        await Refused(f.Repository, s);
        await Expect<InvalidOperationException>(() => f.Repository.GetSubmoduleConflictAsync(f.Path));
        Check(Bytes(index, await f.Index()), "An externally resolved gitlink cannot be overwritten using old conflict stages.");
    }

    private static async Task MissingAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string held = System.IO.Path.Combine(f.Root.Root, "held-child"); Directory.Move(f.Child.Path, held);
        Directory.CreateDirectory(f.Child.Path); byte[] index = await f.Index(); GitSubmoduleConflictSnapshot s = await f.Snapshot();
        Check(!s.CanOpen && !s.CanStageCurrent && s.Current is null && s.ValidationError is not null,
            "An empty uninitialized child is not replaced by Git's ascending parent discovery.");
        await Expect<InvalidOperationException>(() => f.Repository.StageSubmoduleConflictCurrentAsync(s));
        await Expect<InvalidOperationException>(() => f.Repository.OpenSubmoduleConflictRepositoryAsync(s));
        foreach (GitConflictResolution resolution in new[] { GitConflictResolution.Manual, GitConflictResolution.Base, GitConflictResolution.Ours, GitConflictResolution.Theirs })
        { await Expect<NotSupportedException>(() => f.Repository.ResolveConflictAsync(f.Path, resolution)); }
        await Expect<NotSupportedException>(() => f.Repository.RunMergeToolAsync(f.Path));
        Directory.Delete(f.Child.Path); s = await f.Snapshot();
        await Expect<InvalidOperationException>(() => f.Repository.StageSubmoduleConflictCurrentAsync(s));
        await Expect<NotSupportedException>(() => f.Repository.ResolveConflictAsync(f.Path, GitConflictResolution.Manual));
        await Expect<NotSupportedException>(() => f.Repository.RunMergeToolAsync(f.Path));
        Check(!s.CanOpen && Bytes(index, await f.Index()), "A missing path cannot silently stage deletion, even though native add would allow it.");
        Check((await f.Repository.ResolveConflictAsync(f.Path, GitConflictResolution.Delete)).Success,
            "Explicit Delete remains an available native resolution for a gitlink after the user chooses deletion.");
    }

    private static async Task ReplacedAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleConflictSnapshot original = await f.Snapshot(); byte[] index = await f.Index();
        string held = System.IO.Path.Combine(f.Root.Root, "held-child"); Directory.Move(f.Child.Path, held); Directory.CreateDirectory(f.Child.Path);
        await File.WriteAllTextAsync(f.ChildFile(".git"), await File.ReadAllTextAsync(System.IO.Path.Combine(held, ".git")));
        await Refused(f.Repository, original);
        await File.WriteAllTextAsync(f.ChildFile(".git"), "gitdir: " + f.File(".git") + "\n");
        GitSubmoduleConflictSnapshot fake = await f.Snapshot();
        Check(!fake.CanOpen && fake.Current is null, "An explicit pointer to the parent common directory is not a child repository.");
        await Expect<InvalidOperationException>(() => f.Repository.StageSubmoduleConflictCurrentAsync(fake));
        Directory.Delete(f.Child.Path, recursive: true); Directory.CreateSymbolicLink(f.Child.Path, held);
        await Expect<InvalidOperationException>(() => f.Snapshot());
        Check(Bytes(index, await f.Index()), "All replacement and alias refusal paths leave the conflict index intact.");
    }

    private static async Task LiteralAsync()
    {
        using Fixture f = await Fixture.CreateAsync(path: "mods/été [a]*\tand\nline\\literal\r");
        GitSubmoduleConflictSnapshot s = await f.Snapshot();
        Check(s.Path == f.Path && s.Local == f.Local && s.Remote == f.Remote, "NUL-delimited index parsing preserves the exact difficult path.");
        ParentWitness before = await f.Witness(); GitSubmoduleConflictStageResult result = await f.Repository.StageSubmoduleConflictCurrentAsync(s);
        Check(result.IsResolved && result.StagedCommit == f.Local && (await f.Witness()) == before, "Literal staging touches the intended gitlink and leaves parent commits unchanged.");
    }

    private static async Task StageFailureAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string count = System.IO.Path.Combine(f.Root.Root, "add-count");
        GitRepository repository = await f.Wrapped("for arg in \"$@\"; do if [ \"$arg\" = add ]; then echo add >> " + Quote(count) + "; fi; done\nexec git \"$@\"\n");
        GitSubmoduleConflictSnapshot s = await repository.GetSubmoduleConflictAsync(f.Path); byte[] index = await f.Index();
        string indexPath = await f.IndexPath(); await File.WriteAllTextAsync(indexPath + ".lock", "held lock\n"); Pipes pipes = new();
        try
        {
            GitSubmoduleConflictStageResult result = await repository.StageSubmoduleConflictCurrentAsync(s, outputReceived: pipes.Receive);
            Check(!result.Result.Success && !result.IsResolved && result.StagedCommit is null && Bytes(index, await f.Index())
                && File.ReadAllLines(count).Length == 1 && Bytes(pipes.Error, result.Result.StandardErrorBytes), "Native lock failure preserves bytes and exact streamed diagnostics from one add invocation.");
        }
        finally { File.Delete(indexPath + ".lock"); }
    }

    private static async Task StageVerificationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); ParentWitness before = await f.Witness();
        GitRepository repository = await f.Wrapped("for arg in \"$@\"; do if [ \"$arg\" = add ]; then git -C " + Quote(f.Child.Path)
            + " checkout --detach " + Quote(f.Arbitrary) + " >/dev/null 2>&1 || exit 91; break; fi; done\nexec git \"$@\"\n");
        GitSubmoduleConflictStageResult result = await repository.StageSubmoduleConflictCurrentAsync(await repository.GetSubmoduleConflictAsync(f.Path));
        Check(result.Result.Success && !result.IsResolved && result.StagedCommit == f.Arbitrary && result.ValidationError is not null
            && (await f.Witness()) == before, "A native HEAD race after the final guard is detected by the exact stage-0 verification; its real effects are retained without a fake successful resolution.");
    }

    private static async Task DirtyAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await File.WriteAllTextAsync(f.ChildFile("remote.txt"), "precious untracked\n");
        GitSubmoduleConflictSnapshot s = await f.Snapshot(); GitReference branch = (await f.Repository.GetSubmoduleConflictBranchesAsync(s)).Single(r => r.Name == "refs/remotes/origin/both"); byte[] index = await f.Index();
        GitCommandException error = await Expect<GitCommandException>(() => f.Repository.CheckoutSubmoduleConflictBranchAsync(s, branch, new(branch.Name, GitCheckoutKind.RemoteBranch)));
        Check(!error.Result.Success && await File.ReadAllTextAsync(f.ChildFile("remote.txt")) == "precious untracked\n"
            && await f.Child.ResolveRevisionAsync("HEAD") == f.Local && Bytes(index, await f.Index()), "Keep retains the shared native checkout refusal for obstructing untracked files.");
    }

    private static async Task HookAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); await f.Hook("echo conflict-checkout-hook >&2\nexit 23\n"); GitSubmoduleConflictSnapshot s = await f.Snapshot();
        GitReference branch = (await f.Repository.GetSubmoduleConflictBranchesAsync(s)).Single(r => r.Name == "refs/remotes/origin/both"); byte[] index = await f.Index();
        GitCommandException error = await Expect<GitCommandException>(() => f.Repository.CheckoutSubmoduleConflictBranchAsync(s, branch, new(branch.Name, GitCheckoutKind.RemoteBranch)));
        Check(error.Result.StandardError.Contains("conflict-checkout-hook", StringComparison.Ordinal) && await f.Child.ResolveRevisionAsync("HEAD") == f.Merged
            && (await f.Repository.RefreshSubmoduleConflictAsync(s)).Current == f.Merged && Bytes(index, await f.Index()), "A failing post-checkout hook exposes native output and leaves its completed child checkout available to Refresh.");
    }

    private static async Task CheckoutCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string pidFile = System.IO.Path.Combine(f.Root.Root, "checkout-hook-pid");
        await f.Hook("echo $$ > " + Quote(pidFile) + "\necho live-conflict-checkout-hook >&2\nsleep 60\n");
        GitSubmoduleConflictSnapshot s = await f.Snapshot(); GitReference branch = (await f.Repository.GetSubmoduleConflictBranchesAsync(s)).Single(r => r.Name == "refs/remotes/origin/both");
        byte[] index = await f.Index(); using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20));
        Task<GitCheckoutResult> pending = f.Repository.CheckoutSubmoduleConflictBranchAsync(s, branch, new(branch.Name, GitCheckoutKind.RemoteBranch), cancel.Token);
        int pid = await CancelLive(pending, cancel, pidFile);
        Check(!LinuxProcessState.IsRunning(pid) && (await f.Repository.RefreshSubmoduleConflictAsync(s)).Current == f.Merged && Bytes(index, await f.Index()),
            "Abort drains a real native hook, keeps the partial child checkout and never stages the parent implicitly.");
    }

    private static async Task StageCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string pidFile = System.IO.Path.Combine(f.Root.Root, "add-wrapper-pid");
        GitRepository repository = await f.Wrapped("for arg in \"$@\"; do if [ \"$arg\" = add ]; then echo $$ > " + Quote(pidFile) + "; echo live-add >&2; sleep 60; fi; done\nexec git \"$@\"\n");
        GitSubmoduleConflictSnapshot s = await repository.GetSubmoduleConflictAsync(f.Path); byte[] index = await f.Index(); Pipes pipes = new(); using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20));
        Task<GitSubmoduleConflictStageResult> pending = repository.StageSubmoduleConflictCurrentAsync(s, cancel.Token, pipes.Receive);
        int pid = await CancelLive(pending, cancel, pidFile);
        Check(!LinuxProcessState.IsRunning(pid) && Bytes(index, await f.Index()) && pipes.Error.Length > 0,
            "Cancel does not invent a stage result, joins the blocked native process and retains every unresolved index byte.");
    }

    private static async Task PostStageCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string added = System.IO.Path.Combine(f.Root.Root, "added"), pidFile = System.IO.Path.Combine(f.Root.Root, "post-add-read-pid");
        GitRepository repository = await f.Wrapped("for arg in \"$@\"; do\n  if [ \"$arg\" = add ]; then git \"$@\"; result=$?; touch " + Quote(added) + "; exit $result; fi\n"
            + "  if [ \"$arg\" = ls-files ] && [ -f " + Quote(added) + " ]; then echo $$ > " + Quote(pidFile) + "; sleep 60; fi\ndone\nexec git \"$@\"\n");
        GitSubmoduleConflictSnapshot s = await repository.GetSubmoduleConflictAsync(f.Path); ParentWitness before = await f.Witness(); using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20));
        Task<GitSubmoduleConflictStageResult> pending = repository.StageSubmoduleConflictCurrentAsync(s, cancel.Token);
        int pid = await CancelLive(pending, cancel, pidFile);
        GitResult after = await Native(f.Repository.Path, "ls-files", "--stage", "-z");
        Check(!LinuxProcessState.IsRunning(pid) && after.StandardOutput.Contains("160000 " + f.Local + " 0\t" + f.Path + "\0", StringComparison.Ordinal)
            && (await f.Witness()) == before, "Abort during verification drains its Git process and preserves the native add already published, without rollback or parent commit.");
    }

    private static async Task LinkedBareAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); GitSubmoduleConflictSnapshot original = await f.Snapshot();
        string linked = System.IO.Path.Combine(f.Root.Root, "linked"); await f.Repository.AddWorktreeAsync(linked, "HEAD", "linked"); GitRepository repository = await GitRepository.OpenAsync(linked);
        await repository.RunAsync(["submodule", "update", "--init", "--", f.Path]);
        GitResult merge = await Native(linked, "merge", "--no-edit", "theirs"); Check(merge.ExitCode == 1, "The linked worktree has its own real merge conflict.");
        GitSubmoduleConflictSnapshot s = await repository.GetSubmoduleConflictAsync(f.Path);
        Check(s.Local == f.Local && s.Remote == f.Remote && s.CanOpen, "The linked root binds its own child and index.");
        await Expect<ArgumentException>(() => repository.OpenSubmoduleConflictRepositoryAsync(original));
        Directory.CreateDirectory(System.IO.Path.Combine(linked, "descendant")); GitRepository descendant = new(System.IO.Path.Combine(linked, "descendant"));
        Check((await descendant.GetSubmoduleConflictAsync(f.Path)).Local == f.Local, "Descendant callers normalize to the same actual linked root.");
        byte[] mainIndex = await f.Index();
        Check((await descendant.StageSubmoduleConflictCurrentAsync(s)).IsResolved && Bytes(mainIndex, await f.Index()), "Staging the linked conflict leaves the main worktree index byte-for-byte unchanged.");
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Expect<OperationCanceledException>(() => f.Repository.GetSubmoduleConflictAsync(f.Path, cancel.Token));
        await Expect<OperationCanceledException>(() => f.Repository.StageSubmoduleConflictCurrentAsync(original, cancel.Token));
        GitRepository bare = await GitRepository.InitAsync(System.IO.Path.Combine(f.Root.Root, "bare.git"), bare: true);
        await Expect<InvalidOperationException>(() => bare.GetSubmoduleConflictAsync(f.Path));
    }

    private static async Task Refused(GitRepository repository, GitSubmoduleConflictSnapshot selected)
    {
        await Expect<InvalidOperationException>(() => repository.RefreshSubmoduleConflictAsync(selected));
        await Expect<InvalidOperationException>(() => repository.OpenSubmoduleConflictRepositoryAsync(selected));
        await Expect<InvalidOperationException>(() => repository.GetSubmoduleConflictBranchesAsync(selected));
        await Expect<InvalidOperationException>(() => repository.StageSubmoduleConflictCurrentAsync(selected));
    }
    private static async Task<int> CancelLive(Task pending, CancellationTokenSource cancel, string marker)
    {
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!File.Exists(marker) || new FileInfo(marker).Length == 0)
            {
                if (pending.IsCompleted) { await pending; throw new InvalidOperationException("The expected native process did not stay live."); }
                if (clock.Elapsed > TimeSpan.FromSeconds(10)) { throw new TimeoutException("No live process witness."); }
                await Task.Delay(20);
            }
            int pid = int.Parse(await File.ReadAllTextAsync(marker)); cancel.Cancel();
            await Expect<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10))); return pid;
        }
        finally { cancel.Cancel(); try { await pending; } catch (OperationCanceledException) { } }
    }
    private static Task<GitResult> Native(string cwd, params string[] arguments) => new GitRunner().RunAsync(cwd, arguments);
    private static string Line(string value) => value.EndsWith('\n') ? value[..^1] : value;
    private static bool Bytes(byte[] one, byte[] two) => one.AsSpan().SequenceEqual(two);
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Executable(string path, string text)
    { await File.WriteAllTextAsync(path, text); if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } }
    private sealed class Pipes
    {
        private readonly MemoryStream _error = new(); private readonly object _gate = new();
        public byte[] Error { get { lock (_gate) { return _error.ToArray(); } } }
        public void Receive(GitProcessOutput output) { if (output.Stream == GitProcessOutputStream.StandardError) { lock (_gate) { _error.Write(output.Bytes.Span); } } }
    }
    private sealed record ParentWitness(string Head, string MergeHead, string CommitCount);
    private enum Layout { Ordinary, AddAdd, LocalDeleted, RemoteDeleted }
    private sealed class Fixture : IDisposable
    {
        private Fixture(RepositoryFixture root, RepositoryFixture source, string path, string @base, string local, string remote, string merged, string arbitrary)
        { Root = root; Source = source; Path = path; Base = @base; Local = local; Remote = remote; Merged = merged; Arbitrary = arbitrary; }
        public RepositoryFixture Root { get; }
        public RepositoryFixture Source { get; }
        public GitRepository Repository => Root.Repository;
        public GitRepository Child => new(ChildFile(""));
        public string Path { get; }
        public string Base { get; }
        public string Local { get; }
        public string Remote { get; }
        public string Merged { get; }
        public string Arbitrary { get; }
        public string File(string path) => Root.File(path);
        public string ChildFile(string path) => System.IO.Path.Combine(File(Path), path);
        public Task<GitSubmoduleConflictSnapshot> Snapshot() => Repository.GetSubmoduleConflictAsync(Path);
        public async Task<string> IndexPath() => Line((await Native(Repository.Path, "rev-parse", "--path-format=absolute", "--git-path", "index")).StandardOutput);
        public async Task<byte[]> Index() => await System.IO.File.ReadAllBytesAsync(await IndexPath());
        public async Task<ParentWitness> Witness() => new(await Repository.ResolveRevisionAsync("HEAD"),
            Line((await Native(Repository.Path, "rev-parse", "MERGE_HEAD")).StandardOutput), Line((await Native(Repository.Path, "rev-list", "--count", "--all")).StandardOutput));
        public async Task<GitRepository> Wrapped(string body)
        {
            string wrapper = System.IO.Path.Combine(Root.Root, "git-wrapper-" + Guid.NewGuid().ToString("N"));
            await Executable(wrapper, "#!/bin/sh\n" + body); return new(Repository.Path, new GitRunner(wrapper));
        }
        public async Task Hook(string body)
        {
            string hooks = System.IO.Path.Combine(Root.Root, "hooks"); Directory.CreateDirectory(hooks);
            await Executable(System.IO.Path.Combine(hooks, "post-checkout"), "#!/bin/sh\n" + body); await Child.SetConfigAsync("core.hooksPath", hooks);
        }
        public static async Task<Fixture> CreateAsync(Layout layout = Layout.Ordinary, string path = "modules/child (space)")
        {
            RepositoryFixture root = await RepositoryFixture.CreateAsync(), source = await RepositoryFixture.CreateAsync();
            string initialBranch = Line((await Native(source.Repository.Path, "symbolic-ref", "--short", "HEAD")).StandardOutput);
            string @base = await source.Repository.ResolveRevisionAsync("HEAD"), emptyParent = await root.Repository.ResolveRevisionAsync("HEAD");
            await source.Repository.RunAsync(["checkout", "-b", "ours", @base]); source.Write("local.txt", "local\n"); await source.Repository.StageAllAsync(); await source.Repository.CommitAsync("Local B"); string local = await source.Repository.ResolveRevisionAsync("HEAD");
            await source.Repository.RunAsync(["checkout", "-b", "theirs", @base]); source.Write("remote.txt", "remote\n"); await source.Repository.StageAllAsync(); await source.Repository.CommitAsync("Remote C"); string remote = await source.Repository.ResolveRevisionAsync("HEAD");
            await source.Repository.RunAsync(["checkout", "-b", "both", local]); await source.Repository.RunAsync(["merge", "--no-ff", "--message", "Merge both", remote]); string merged = await source.Repository.ResolveRevisionAsync("HEAD");
            await source.Repository.RunAsync(["checkout", "-b", "arbitrary", @base]); source.Write("arbitrary.txt", "arbitrary\n"); await source.Repository.StageAllAsync(); await source.Repository.CommitAsync("Arbitrary D"); string arbitrary = await source.Repository.ResolveRevisionAsync("HEAD");
            await source.Repository.CheckoutAsync(initialBranch);
            await root.Repository.RunAsync(["submodule", "add", "--name", "child", "--", source.Repository.Path, path]); await root.Repository.CommitAsync("Initial module A");
            string parentBase = await root.Repository.ResolveRevisionAsync("HEAD"); GitRepository child = new(root.File(path)); await RepositoryFixture.ConfigureAsync(child);
            await root.Repository.RunAsync(["checkout", "-b", "ours"]);
            if (layout == Layout.LocalDeleted) { await DeleteGitlink(root.Repository, path); }
            else { await child.CheckoutAsync(local, detached: true); await root.Repository.RunAsync(["--literal-pathspecs", "add", "--", path]); }
            await root.Repository.CommitAsync("Parent local");
            await root.Repository.RunAsync(["checkout", "-b", "theirs", layout == Layout.AddAdd ? emptyParent : parentBase]);
            if (layout == Layout.AddAdd) { await root.Repository.RunAsync(["submodule", "add", "--force", "--name", "child", "--", source.Repository.Path, path]); }
            if (layout == Layout.RemoteDeleted) { await DeleteGitlink(root.Repository, path); }
            else { await child.CheckoutAsync(remote, detached: true); await root.Repository.RunAsync(["--literal-pathspecs", "add", "--", path]); }
            await root.Repository.CommitAsync("Parent remote");
            await root.Repository.CheckoutAsync("ours"); await child.CheckoutAsync(local, detached: true);
            GitResult merge = await Native(root.Repository.Path, "merge", "--no-edit", "theirs");
            Check(merge.ExitCode == 1 && (await Native(root.Repository.Path, "ls-files", "--unmerged", "-z")).StandardOutput.Contains("160000 ", StringComparison.Ordinal), "Fixture creates a real native gitlink merge conflict.");
            return new(root, source, path, @base, local, remote, merged, arbitrary);
        }
        private static async Task DeleteGitlink(GitRepository repository, string path)
        {
            await repository.RunAsync(["--literal-pathspecs", "rm", "--cached", "--", path]);
            await repository.RunAsync(["config", "--file", ".gitmodules", "--remove-section", "submodule.child"]);
            await repository.StageAsync([".gitmodules"]);
        }
        public void Dispose() { Root.Dispose(); Source.Dispose(); }
    }
}
