using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using GitExtensions.Linux;

internal static class CherryPickScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("cherry-pick default no-commit preserves existing index and literal paths", NoCommitAsync),
        ("cherry-pick automatic commit preserves author and optional origin reference", AutoCommitAsync),
        ("cherry-pick string compatibility still commits", CompatibilityAsync),
        ("cherry-pick merge parent validation precedes mutation and both parents work", MergeAsync),
        ("cherry-pick freezes every selected revision in the supplied order", SelectionAsync),
        ("cherry-pick automatic conflict continues through native Git", AutoConflictAsync),
        ("cherry-pick no-commit conflict completes without creating a commit", NoCommitConflictAsync),
        ("cherry-pick abort and skip restore preexisting staged and unstaged data", RecoveryAsync),
        ("cherry-pick abort and skip restore renamed checkout and pre-staged rename targets", RenameRecoveryAsync),
        ("cherry-pick recovery restores the conservatively captured directory relocation", DirectoryCoverageAsync),
        ("cherry-pick recovery refuses external branch switches and child commits", ExternalHeadAsync),
        ("cherry-pick rejects symlink parent swaps and unexpected directories", PathSafetyAsync),
        ("cherry-pick respects concurrent locks and split-index recovery", IndexSafetyAsync),
        ("cherry-pick works in detached and linked worktrees and rejects bare or unborn", WorktreesAsync),
        ("cherry-pick rejects pending operations and invalid revisions unchanged", InvalidAsync),
        ("cherry-pick cancellation preserves state and terminates descendants", CancellationAsync),
        ("cherry-pick cancellation after an unproven commit fails closed", CommittedCancellationAsync)
    ];
    private static void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }
    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T expected) { return expected; } throw new Exception("Expected " + typeof(T).Name); }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static async Task NoCommitAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        string path = "literal λ [*] '$()\nfile.txt";
        string pick = await f.TopicAsync(path, "picked\n");
        f.Write("staged.txt", "already staged\n"); await f.Git("add", "--", "staged.txt"); f.Write("staged.txt", "separate worktree bytes\n");
        f.Write("base.txt", "unstaged base\n"); string refs = await f.Text("for-each-ref");
        GitCherryPickResult result = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick));
        Check(result.Success && result.Session.Completed && !result.Session.Options.AutoCommit, "The original default must be --no-commit.");
        Check(await f.Head() == f.Initial && await f.Text("for-each-ref") == refs, "A no-commit pick must leave every ref unchanged.");
        Check(File.ReadAllText(f.File(path)) == "picked\n" && await f.Text("show", ":" + path) == "picked", "Literal path must reach worktree and index.");
        Check(await f.Text("show", ":staged.txt") == "already staged" && File.ReadAllText(f.File("staged.txt")) == "separate worktree bytes\n", "Existing staged and separate unstaged data must both survive.");
        Check(File.ReadAllText(f.File("base.txt")) == "unstaged base\n", "Unrelated worktree edits must survive.");
    }
    private static async Task AutoCommitAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string pick = await f.TopicAsync("picked.txt", "picked\n", author: true);
        GitCherryPickResult result = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick, AutoCommit: true, AddCommitReference: true));
        string head = await f.Head(); Check(result.Success && head != f.Initial, "Automatic option creates a new commit.");
        Check(await f.Text("show", "-s", "--format=%P", "HEAD") == f.Initial, "The new commit belongs to the checkout, not the graph selection.");
        Check(await f.Text("show", "-s", "--format=%an <%ae>", "HEAD") == "Original Author <original@example.invalid>", "Original author must be preserved.");
        Check((await f.Text("show", "-s", "--format=%B", "HEAD")).Contains("(cherry picked from commit " + pick + ")", StringComparison.Ordinal), "-x must add the full original identity.");
        Check(await f.Text("status", "--porcelain") == "", "Successful automatic pick leaves a clean index.");
    }
    private static async Task CompatibilityAsync()
    { using Fixture f = await Fixture.CreateAsync(); string pick = await f.TopicAsync("picked.txt", "picked\n"); Check((await f.Repository.CherryPickAsync(pick)).Success && await f.Head() != f.Initial, "The preexisting string overload must retain automatic commit behavior."); }
    private static async Task MergeAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await f.Git("checkout", "-b", "left"); await f.Commit("left.txt", "left\n", "Left parent"); string left = await f.Head();
        await f.Git("checkout", "-b", "right", f.Initial); await f.Commit("right.txt", "right\n", "Right parent"); string right = await f.Head();
        await f.Git("checkout", "left"); await f.Git("merge", "--no-ff", "right", "-m", "Merge"); string merge = await f.Head(); await f.Git("checkout", "main");
        GitCherryPickRevision revision = await f.Repository.GetCherryPickRevisionAsync(merge);
        Check(revision.Parents.Select(parent => parent.Hash).SequenceEqual(new[] { left, right }), "Parent numbers must follow native merge order.");
        string snapshot = await f.Snapshot();
        foreach (int? invalid in new int?[] { null, 0, -1, 3 })
        { await ExpectAsync<ArgumentException>(() => f.Repository.CherryPickAsync(new GitCherryPickOptions(merge, MainlineParent: invalid))); Check(await f.Snapshot() == snapshot, "Invalid parent must precede all mutation."); }
        await ExpectAsync<ArgumentException>(() => f.Repository.CherryPickAsync(new GitCherryPickOptions(left, MainlineParent: 1))); Check(await f.Snapshot() == snapshot, "Nonmerge parent option must reject unchanged.");
        Check((await f.Repository.CherryPickAsync(new GitCherryPickOptions(merge, MainlineParent: 1))).Success, "First parent pick must work.");
        Check(File.Exists(f.File("right.txt")) && !File.Exists(f.File("left.txt")), "Parent1 means only the change relative to first parent."); await f.Git("reset", "--hard", f.Initial);
        Check((await f.Repository.CherryPickAsync(new GitCherryPickOptions(merge, MainlineParent: 2))).Success, "Second parent pick must work.");
        Check(File.Exists(f.File("left.txt")) && !File.Exists(f.File("right.txt")), "Parent2 must use the second parent's diff.");
    }
    private static async Task SelectionAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string one = await f.TopicAsync("one.txt", "one\n");
        await f.Git("checkout", "topic"); await f.Commit("two.txt", "two\n", "Second"); string two = await f.Head(); await f.Git("checkout", "main");
        string snapshot = await f.Snapshot(); await ExpectAsync<GitCommandException>(() => f.Repository.PrepareCherryPickSelectionAsync([one, "missing-revision"]));
        Check(await f.Snapshot() == snapshot, "A bad later revision must reject before any selected commit is applied.");
        IReadOnlyList<GitCherryPickRevision> prepared = await f.Repository.PrepareCherryPickSelectionAsync([two, one]);
        Check(prepared.Select(revision => revision.Commit.Hash).SequenceEqual(new[] { two, one }), "The caller's original grid order must be retained.");
        await f.Git("update-ref", "refs/heads/topic", f.Initial); Check(prepared[0].Commit.Hash == two, "Resolved commits must not follow later branch moves.");
    }
    private static async Task AutoConflictAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync();
        GitCherryPickResult result = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick, AutoCommit: true, AddCommitReference: true));
        Check(!result.Success && result.ConflictedPaths.SequenceEqual(new[] { "base.txt" }) && File.Exists(f.Metadata("CHERRY_PICK_HEAD")), "Automatic conflict exposes native pending state.");
        await ExpectAsync<InvalidOperationException>(() => f.Repository.ResumeCherryPickAsync(result.Session, GitOperationAction.Continue));
        f.Write("base.txt", "resolved\n"); await f.Git("add", "base.txt"); result = await f.Repository.ResumeCherryPickAsync(result.Session, GitOperationAction.Continue);
        Check(result.Success && result.Session.Completed && !File.Exists(f.Metadata("CHERRY_PICK_HEAD")), "Resolved automatic pick must finish native sequencer.");
        Check(await f.Text("show", "HEAD:base.txt") == "resolved" && (await f.Text("show", "-s", "--format=%B", "HEAD")).Contains(pick, StringComparison.Ordinal), "Resolution and -x identity must be committed.");
    }
    private static async Task NoCommitConflictAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync(); string head = await f.Head(); f.Write("prior.txt", "prior\n"); await f.Git("add", "prior.txt");
        GitCherryPickResult result = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick)); Check(!result.Success && !File.Exists(f.Metadata("CHERRY_PICK_HEAD")), "Single -n conflict has no native CHERRY_PICK_HEAD.");
        f.Write("base.txt", "resolved\n"); await f.Git("add", "base.txt"); result = await f.Repository.ResumeCherryPickAsync(result.Session, GitOperationAction.Continue);
        Check(result.Success && result.Session.Completed && await f.Head() == head, "No-commit recovery must accept the resolved index without creating a commit.");
        Check(await f.Text("show", ":base.txt") == "resolved" && await f.Text("show", ":prior.txt") == "prior", "Resolution and preexisting staged bytes must coexist.");
    }
    private static async Task RecoveryAsync()
    {
        foreach (bool automatic in new[] { false, true }) foreach (GitOperationAction action in new[] { GitOperationAction.Abort, GitOperationAction.Skip })
        {
            using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync();
            if (!automatic) { f.Write("prior.txt", "prior staged\n"); await f.Git("add", "prior.txt"); f.Write("prior.txt", "prior worktree\n"); }
            string before = await f.Snapshot(); GitCherryPickResult result = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick, AutoCommit: automatic));
            Check(!result.Success, "Fixture must really conflict."); await f.Repository.ResumeCherryPickAsync(result.Session, action);
            Check(result.Session.Aborted && await f.Snapshot() == before, "Abort/skip must restore exact original refs, raw index, metadata and worktree bytes.");
        }
    }
    private static async Task ExternalHeadAsync()
    {
        foreach (bool child in new[] { false, true })
        {
            using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync(); GitCherryPickResult pending = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick));
            if (child) { f.Write("base.txt", "externally resolved\n"); await f.Git("add", "base.txt"); await f.Git("commit", "-m", "External child commit"); }
            else { await f.Git("update-ref", "refs/heads/external", await f.Head()); await f.Git("symbolic-ref", "HEAD", "refs/heads/external"); }
            string external = await f.Snapshot(); await ExpectAsync<InvalidOperationException>(() => f.Repository.ResumeCherryPickAsync(pending.Session, GitOperationAction.Abort));
            Check(await f.Snapshot() == external && !pending.Session.Aborted, "Recovery must fail closed without overwriting an external branch or child commit.");
        }
    }

    private static async Task RenameRecoveryAsync()
    {
        foreach (bool committedRename in new[] { false, true })
        foreach (bool automatic in committedRename ? new[] { false, true } : new[] { false })
        foreach (GitOperationAction action in new[] { GitOperationAction.Abort, GitOperationAction.Skip })
        {
            using Fixture f = await Fixture.CreateAsync(); await f.Commit("old.txt", "line1\nline2\nline3\n", "Rename base");
            string pick = await f.TopicAsync("old.txt", "line1\ntheirs\nline3\n");
            await f.Git("mv", "old.txt", "new.txt");
            if (committedRename) { await f.Git("commit", "-m", "Rename checkout"); }
            f.Write("new.txt", "line1\nours\nline3\n");
            if (committedRename) { await f.Git("commit", "-am", "Checkout modification"); }
            else { await f.Git("add", "new.txt"); }
            if (!automatic) { f.Write("prior.txt", "preexisting staged\n"); await f.Git("add", "prior.txt"); f.Write("prior.txt", "preexisting worktree\n"); }
            // A large sparse untracked dataset is a name baseline, never a byte snapshot.
            string dataset = f.File("untracked-data.bin"); using (FileStream stream = new(dataset, FileMode.CreateNew)) { stream.SetLength(8L * 1024 * 1024 * 1024); }
            string index = Digest(File.ReadAllBytes(f.Metadata("index"))), head = await f.Head(), refs = await f.Text("for-each-ref");
            GitCherryPickResult pending = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick, AutoCommit: automatic));
            Check(!pending.Success && pending.ConflictedPaths.SequenceEqual(new[] { "new.txt" }), "The actual native conflict must be redirected to the checkout rename.");
            Check(File.ReadAllText(f.File("new.txt")).Contains("<<<<<<<", StringComparison.Ordinal), "The renamed worktree really must contain conflict markers before recovery.");
            GitCherryPickResult restored = await f.Repository.ResumeCherryPickAsync(pending.Session, action);
            Check(restored.Success && pending.Session.Aborted && File.ReadAllText(f.File("new.txt")) == "line1\nours\nline3\n" && !File.Exists(f.File("old.txt")), "Abort and Skip must restore the real renamed worktree target.");
            Check(Digest(File.ReadAllBytes(f.Metadata("index"))) == index && await f.Head() == head && await f.Text("for-each-ref") == refs, "Raw original index and every ref must be restored exactly.");
            if (!automatic) { Check(await f.Text("show", ":prior.txt") == "preexisting staged" && File.ReadAllText(f.File("prior.txt")) == "preexisting worktree\n", "Preexisting staged and separate worktree bytes survive renamed recovery."); }
            Check(new FileInfo(dataset).Length == 8L * 1024 * 1024 * 1024, "Unrelated untracked data remains intact without copying its bytes.");
        }
    }

    private static async Task DirectoryCoverageAsync()
    {
        foreach (GitOperationAction action in new[] { GitOperationAction.Abort, GitOperationAction.Skip })
        {
            using Fixture f = await Fixture.CreateAsync(); await f.Commit("old-directory/seed.txt", "directory seed\n", "Directory base");
            string pick = await f.TopicAsync("old-directory/added.txt", "source addition\n");
            await f.Git("mv", "old-directory", "new-directory"); await f.Git("commit", "-m", "Move checkout directory");
            await f.Git("config", "merge.directoryRenames", "conflict");
            f.Write("prior.txt", "preexisting staged\n"); await f.Git("add", "prior.txt");
            string before = await f.Snapshot();
            GitCherryPickResult pending = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick));
            Check(!pending.Success && pending.ConflictedPaths.Contains("new-directory/added.txt"), "Native location conflict must create the unexpected relocated target.");
            GitCherryPickResult restored = await new GitRepository(f.Repository.Path).ResumeCherryPickAsync(pending.Session, action);
            Check(restored.Success && pending.Session.Aborted && await f.Snapshot() == before && !File.Exists(f.Metadata("index.lock")), "Directory relocation recovery must restore exact refs/index/metadata/worktree bytes from its pre-mutation snapshot.");
        }
    }
    private static async Task PathSafetyAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        foreach (bool directory in new[] { false, true })
        {
            using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync("folder/file.txt");
            GitCherryPickResult pending = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick));
            string outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "file.txt"), "outside sentinel\n");
            if (directory) { File.Delete(f.File("folder/file.txt")); Directory.CreateDirectory(f.File("folder/file.txt")); }
            else { Directory.Move(f.File("folder"), f.File("saved-folder")); Directory.CreateSymbolicLink(f.File("folder"), outside); }
            string index = Digest(File.ReadAllBytes(f.Metadata("index"))), marker = File.ReadAllText(f.Metadata("MERGE_MSG")); string head = await f.Head();
            await ExpectAsync<IOException>(() => f.Repository.ResumeCherryPickAsync(pending.Session, GitOperationAction.Abort));
            Check(Digest(File.ReadAllBytes(f.Metadata("index"))) == index && File.ReadAllText(f.Metadata("MERGE_MSG")) == marker && await f.Head() == head, "Unsafe filesystem recovery must stop before any metadata/index/ref write.");
            Check(File.ReadAllText(Path.Combine(outside, "file.txt")) == "outside sentinel\n", "A parent symlink must never redirect writes outside the worktree.");
        }
        using (Fixture f = await Fixture.CreateAsync())
        {
            string pick = await f.TopicAsync("folder/file.txt", "pick\n"); string outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "file.txt"), "outside\n"); Directory.CreateSymbolicLink(f.File("folder"), outside);
            byte[] index = File.ReadAllBytes(f.Metadata("index")); await ExpectAsync<IOException>(() => f.Repository.CherryPickAsync(new GitCherryPickOptions(pick)));
            Check(File.ReadAllBytes(f.Metadata("index")).SequenceEqual(index) && await f.Head() == f.Initial && File.ReadAllText(Path.Combine(outside, "file.txt")) == "outside\n", "Unsafe initial capture must also reject unchanged.");
        }
        using (Fixture f = await Fixture.CreateAsync())
        {
            string pick = await f.TopicAsync("picked.txt", "pick\n"); string alias = Path.Combine(f.Root, "alias"); Directory.CreateSymbolicLink(alias, f.Root);
            GitRepository repository = new(Path.Combine(alias, "repo")); byte[] index = File.ReadAllBytes(f.Metadata("index"));
            await ExpectAsync<IOException>(() => repository.CherryPickAsync(new GitCherryPickOptions(pick)));
            Check(File.ReadAllBytes(f.Metadata("index")).SequenceEqual(index) && await f.Head() == f.Initial && !File.Exists(f.File("picked.txt")), "A symbolic-link ancestor of a lexical repository root must be rejected before mutation.");
            Directory.Delete(alias);
        }
    }
    private static async Task IndexSafetyAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string pick = await f.ConflictAsync();
        f.Write("prior.txt", "staged\n"); await f.Git("add", "prior.txt"); await f.Git("update-index", "--split-index");
        string shared = Path.GetFullPath(await f.Text("rev-parse", "--shared-index-path"), f.Repository.Path); byte[] sharedBytes = File.ReadAllBytes(shared); string before = await f.Snapshot();
        GitCherryPickResult pending = await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick));
        File.WriteAllText(f.Metadata("index.lock"), "another operation owns this\n"); string conflicted = await f.Snapshot();
        await ExpectAsync<IOException>(() => f.Repository.ResumeCherryPickAsync(pending.Session, GitOperationAction.Abort));
        Check(File.ReadAllText(f.Metadata("index.lock")) == "another operation owns this\n" && await f.Snapshot() == conflicted, "Recovery must not remove another operation's index lock or mutate state.");
        File.Delete(f.Metadata("index.lock")); File.Delete(shared); await f.Repository.ResumeCherryPickAsync(pending.Session, GitOperationAction.Abort);
        Check(File.ReadAllBytes(shared).SequenceEqual(sharedBytes) && await f.Snapshot() == before, "Exact split index and referenced shared index must be restored.");
        Check((await f.Text("ls-files", "--stage")).Contains("prior.txt", StringComparison.Ordinal), "Git must be able to read the restored split index.");
        File.WriteAllText(f.Metadata("index.lock"), "owned\n"); byte[] index = File.ReadAllBytes(f.Metadata("index"));
        await ExpectAsync<IOException>(() => f.Repository.CherryPickAsync(new GitCherryPickOptions(pick))); Check(File.ReadAllText(f.Metadata("index.lock")) == "owned\n" && File.ReadAllBytes(f.Metadata("index")).SequenceEqual(index), "Initial snapshot must respect an already owned index lock."); File.Delete(f.Metadata("index.lock"));
    }
    private static async Task WorktreesAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string pick = await f.TopicAsync("picked.txt", "pick\n"); await f.Git("checkout", "--detach", f.Initial);
        Check((await f.Repository.CherryPickAsync(new GitCherryPickOptions(pick))).Success && await f.Head() == f.Initial, "Detached no-commit pick leaves detached HEAD intact."); await f.Git("reset", "--hard", f.Initial);
        string linked = Path.Combine(f.Root, "linked"); await f.Git("worktree", "add", "--detach", linked, f.Initial); GitRepository repository = new(linked);
        Check((await repository.CherryPickAsync(new GitCherryPickOptions(pick))).Success && File.ReadAllText(Path.Combine(linked, "picked.txt")) == "pick\n", "Linked worktree uses its own index/metadata.");
        string bare = Path.Combine(f.Root, "bare.git"); await new GitRunner().RunAsync(f.Root, ["clone", "--bare", f.Repository.Path, bare]);
        await ExpectAsync<InvalidOperationException>(() => new GitRepository(bare).CherryPickAsync(new GitCherryPickOptions(pick)));
        string unborn = Path.Combine(f.Root, "unborn"); Directory.CreateDirectory(unborn); GitRunner runner = new(); await runner.RunAsync(unborn, ["init"]); await runner.RunAsync(unborn, ["fetch", f.Repository.Path, "topic"]);
        await ExpectAsync<InvalidOperationException>(() => new GitRepository(unborn).CherryPickAsync(new GitCherryPickOptions(pick)));
    }
    private static async Task InvalidAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string pick = await f.TopicAsync("picked.txt", "pick\n"); string before = await f.Snapshot();
        await ExpectAsync<GitCommandException>(() => f.Repository.CherryPickAsync(new GitCherryPickOptions("unknown-revision"))); Check(await f.Snapshot() == before, "Bad revision must never mutate state.");
        foreach (string marker in new[] { "MERGE_HEAD", "REVERT_HEAD", "CHERRY_PICK_HEAD" })
        { File.WriteAllText(f.Metadata(marker), f.Initial + "\n"); byte[] index = File.ReadAllBytes(f.Metadata("index")); await ExpectAsync<InvalidOperationException>(() => f.Repository.CherryPickAsync(new GitCherryPickOptions(pick))); Check(File.ReadAllBytes(f.Metadata("index")).SequenceEqual(index) && File.ReadAllText(f.Metadata(marker)) == f.Initial + "\n", "Existing operation must remain intact."); File.Delete(f.Metadata(marker)); }
        foreach (string directory in new[] { "sequencer", "rebase-merge", "rebase-apply" })
        { Directory.CreateDirectory(f.Metadata(directory)); await ExpectAsync<InvalidOperationException>(() => f.Repository.CherryPickAsync(new GitCherryPickOptions(pick))); Check(Directory.Exists(f.Metadata(directory)) && await f.Head() == f.Initial, "Pending sequencer must remain intact."); Directory.Delete(f.Metadata(directory)); }
    }
    private static async Task CancellationAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string pick = await f.TopicAsync("picked.txt", "pick\n"); f.Write("prior.txt", "prior\n"); await f.Git("add", "prior.txt"); string before = await f.Snapshot();
        using (CancellationTokenSource already = new()) { already.Cancel(); await ExpectAsync<OperationCanceledException>(() => f.Repository.CherryPickAsync(new GitCherryPickOptions(pick), already.Token)); } Check(await f.Snapshot() == before, "Pre-cancelled request must preserve state.");
        if (!OperatingSystem.IsLinux()) { return; }
        string executable = await f.GateAsync(commitFirst: false); GitRepository gated = new(f.Repository.Path, new GitRunner(executable)); using CancellationTokenSource cancel = new();
        Task<GitCherryPickResult> pending = gated.CherryPickAsync(new GitCherryPickOptions(pick), cancel.Token); await f.WaitGateAsync(pending); int parent = int.Parse(System.IO.File.ReadAllText(Path.Combine(f.Root, "gate.pid")), CultureInfo.InvariantCulture), child = int.Parse(System.IO.File.ReadAllText(Path.Combine(f.Root, "gate.child")), CultureInfo.InvariantCulture);
        cancel.Cancel(); await ExpectAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Stopwatch wait = Stopwatch.StartNew(); while ((LinuxProcessState.IsRunning(parent) || LinuxProcessState.IsRunning(child)) && wait.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(10); }
        Check(!LinuxProcessState.IsRunning(parent) && !LinuxProcessState.IsRunning(child), "Running Git process and descendant must both terminate."); Check(await f.Snapshot() == before, "Cancelled gate must restore index/files/refs exactly.");
    }
    private static async Task CommittedCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); string pick = await f.TopicAsync("picked.txt", "pick\n"); string executable = await f.GateAsync(commitFirst: true); GitRepository gated = new(f.Repository.Path, new GitRunner(executable)); using CancellationTokenSource cancel = new();
        Task<GitCherryPickResult> pending = gated.CherryPickAsync(new GitCherryPickOptions(pick, AutoCommit: true), cancel.Token); await f.WaitGateAsync(pending); string committed = await f.Snapshot(); Check(await f.Head() != f.Initial, "The real Git process must have committed before cancellation.");
        cancel.Cancel(); AggregateException error = await ExpectAsync<AggregateException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Check(error.InnerExceptions.Any(exception => exception is OperationCanceledException) && error.InnerExceptions.Any(exception => exception is InvalidOperationException), "Interrupted command with changed HEAD must report explicit unprovable recovery.");
        Check(await f.Snapshot() == committed, "Cancellation must not roll back a child commit whose ownership cannot be proven.");
    }
    private sealed class Fixture : IDisposable
    {
        private readonly GitRunner _runner = new(); public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-cherry-core-" + Guid.NewGuid().ToString("N"));
        public GitRepository Repository => new(Path.Combine(Root, "repo")); public string Initial { get; private set; } = "";
        public string File(string path) => Path.Combine(Repository.Path, path); public string Metadata(string path) => Path.Combine(Repository.Path, ".git", path);
        public void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(File(path))!); System.IO.File.WriteAllText(File(path), text); }
        public static async Task<Fixture> CreateAsync()
        {
            Fixture f = new(); Directory.CreateDirectory(f.Repository.Path); await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Cherry Test"); await f.Git("config", "user.email", "cherry@example.invalid"); await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null"); await f.Commit("base.txt", "base\n", "Initial"); f.Initial = await f.Head(); return f;
        }
        public async Task<GitResult> Git(params string[] args) { GitResult result = await _runner.RunAsync(Repository.Path, args, environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" }); Check(result.Success, result.StandardError); return result; }
        public async Task<string> Text(params string[] args) => (await Git(args)).StandardOutput.TrimEnd('\r', '\n'); public Task<string> Head() => Text("rev-parse", "HEAD");
        public async Task Commit(string path, string text, string message) { Write(path, text); await Git("add", "--", path); await Git("commit", "-m", message); }
        public async Task<string> TopicAsync(string path, string text, bool author = false)
        { await Git("checkout", "-b", "topic"); Write(path, text); await Git("add", "--", path); await Git(author ? ["commit", "--author=Original Author <original@example.invalid>", "-m", "Original subject\n\nOriginal body"] : ["commit", "-m", "Topic"]); string hash = await Head(); await Git("checkout", "main"); return hash; }
        public async Task<string> ConflictAsync(string path = "base.txt")
        { if (path != "base.txt") { await Commit(path, "base\n", "Base file"); Initial = await Head(); } string pick = await TopicAsync(path, "theirs\n"); await Commit(path, "ours\n", "Checkout change"); return pick; }
        public async Task<string> Snapshot()
        {
            List<string> values = [await Head(), (await _runner.RunAsync(Repository.Path, ["symbolic-ref", "-q", "HEAD"])).StandardOutput.TrimEnd('\r', '\n'), await Text("for-each-ref"), await Text("config", "--local", "--list")];
            foreach (string name in new[] { "index", "CHERRY_PICK_HEAD", "MERGE_MSG", "AUTO_MERGE" }) { values.Add(name + ":" + (System.IO.File.Exists(Metadata(name)) ? Digest(System.IO.File.ReadAllBytes(Metadata(name))) : "absent")); }
            foreach (string file in Directory.EnumerateFiles(Repository.Path, "*", SearchOption.AllDirectories).Where(file => !file.StartsWith(Metadata(""), StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            { values.Add(Path.GetRelativePath(Repository.Path, file) + ":" + (new FileInfo(file).LinkTarget ?? Digest(System.IO.File.ReadAllBytes(file)))); }
            return string.Join("\n", values);
        }
        public async Task<string> GateAsync(bool commitFirst)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Executable process gate requires Linux."); }
            string executable = Path.Combine(Root, "gate-git"); string script = "#!/bin/sh\npick=0\nquit=0\nfor arg in \"$@\"; do [ \"$arg\" = cherry-pick ] && pick=1; [ \"$arg\" = --quit ] && quit=1; done\nif [ \"$pick\" = 1 ] && [ \"$quit\" = 0 ]; then\n";
            if (commitFirst) { script += " git \"$@\" || exit $?\n"; }
            script += " printf '%s' \"$$\" > " + Quote(Path.Combine(Root, "gate.pid")) + "\n sleep 60 &\n printf '%s' \"$!\" > " + Quote(Path.Combine(Root, "gate.child")) + "\n wait\n exit 1\nfi\nexec git \"$@\"\n";
            await System.IO.File.WriteAllTextAsync(executable, script); System.IO.File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return executable;
        }
        public async Task WaitGateAsync(Task pending)
        { Stopwatch timer = Stopwatch.StartNew(); string child = Path.Combine(Root, "gate.child"); while ((!System.IO.File.Exists(child) || new FileInfo(child).Length == 0) && !pending.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(10); } Check(System.IO.File.Exists(child) && new FileInfo(child).Length > 0, "Command must reach live gate."); }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
