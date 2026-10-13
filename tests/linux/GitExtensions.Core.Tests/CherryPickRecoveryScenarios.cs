using System.Diagnostics;
using System.Security.Cryptography;
using GitExtensions.Linux;

internal static class CherryPickRecoveryScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("cherry-pick durable no-commit recovery survives new repository and keeps private byte preimages", ReopenAbortAsync),
        ("cherry-pick durable automatic recovery continues or skips the original pick", ReopenAutomaticAsync),
        ("cherry-pick durable resolved no-commit continues without changing HEAD", ReopenContinueAsync),
        ("cherry-pick recovery covers committed and staged directory moves for all Git directory modes", DirectoryMovesAsync),
        ("cherry-pick recovery covers nested and split directory destinations", NestedSplitAsync),
        ("cherry-pick recovery preserves untracked and ignored relocated collision bytes", CollisionAsync),
        ("cherry-pick invalid or incomplete durable recovery blocks another pick without mutation", InvalidJournalAsync),
        ("cherry-pick recovery refuses foreign operation, outside changes and owned index lock unchanged", OwnershipAsync),
        ("cherry-pick durable recovery remains isolated in linked worktrees with split index", LinkedSplitAsync),
        ("cherry-pick process restart restores original staged objects after resolution and prune", ProcessRestartAsync)
    ];

    private static void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T exception) { return exception; } throw new Exception("Expected " + typeof(T).Name); }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task ReopenAbortAsync()
    {
        using Fixture f = await Fixture.Create(); string pick = await f.Conflict(staged: true);
        f.Write("prior.bin", [0, 255, 128, 10]); await f.Git("add", "prior.bin"); f.Write("prior.bin", [42, 0, 129, 13]);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(f.File("base.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); await f.Git("add", "base.txt"); }
        string before = await f.State();
        GitCherryPickResult pending = await f.Repo.CherryPickAsync(new GitCherryPickOptions(pick, AddCommitReference: true));
        Check(!pending.Success, "The real Git pick must conflict.");
        GitCherryPickRecovery recovered = await new GitRepository(f.Path).GetCherryPickRecoveryAsync();
        Check(recovered.State == GitCherryPickRecoveryState.Pending && recovered.Session!.Options == new GitCherryPickOptions(pick, AddCommitReference: true), "Immutable options and revision must be recovered by a new instance.");
        Check(!recovered.CanContinue && recovered.ConflictedPaths.Contains("base.txt"), "Unresolved pending state must prevent Continue.");
        if (!OperatingSystem.IsWindows())
        {
            Check(File.GetUnixFileMode(f.Journal) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Recovery directory must be atomically private 0700.");
            Check(File.GetUnixFileMode(f.Manifest) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "The durable byte snapshot must be private 0600.");
        }
        await new GitRepository(f.Path).ResumeCherryPickAsync(recovered.Session!, GitOperationAction.Abort);
        Check(await f.State() == before && !Directory.Exists(f.Journal), "Abort restores raw index, byte preimages, modes, refs and metadata then removes the journal.");
    }

    private static async Task ReopenAutomaticAsync()
    {
        foreach (GitOperationAction action in new[] { GitOperationAction.Continue, GitOperationAction.Skip, GitOperationAction.Abort })
        {
            using Fixture f = await Fixture.Create(); string pick = await f.Conflict(); string before = await f.State(), head = await f.Text("rev-parse", "HEAD");
            await f.Repo.CherryPickAsync(new GitCherryPickOptions(pick, AutoCommit: true, AddCommitReference: true));
            GitCherryPickRecovery recovered = await new GitRepository(f.Path).GetCherryPickRecoveryAsync();
            Check(recovered.State == GitCherryPickRecoveryState.Pending && recovered.Session!.Options.AutoCommit, "Automatic mode must survive closing its original instance.");
            if (action == GitOperationAction.Continue) { f.Write("base.txt", "resolved\n"); await f.Git("add", "base.txt"); }
            GitCherryPickResult result = await new GitRepository(f.Path).ResumeCherryPickAsync(recovered.Session!, action);
            Check(result.Success && !Directory.Exists(f.Journal), "Successful terminal action must clear its durable recovery.");
            if (action == GitOperationAction.Continue)
            { Check(await f.Text("rev-parse", "HEAD") != head && (await f.Text("log", "-1", "--format=%B")).Contains(pick, StringComparison.Ordinal), "Native continue creates the original referenced cherry-pick commit."); }
            else { Check(await f.State() == before, "Restarted Skip/Abort must restore the exact automatic baseline."); }
        }
    }

    private static async Task ReopenContinueAsync()
    {
        using Fixture f = await Fixture.Create(); string pick = await f.Conflict(staged: true); f.Write("prior.txt", "index bytes\n"); await f.Git("add", "prior.txt"); f.Write("prior.txt", "worktree bytes\n");
        string head = await f.Text("rev-parse", "HEAD"); await f.Repo.CherryPickAsync(new GitCherryPickOptions(pick));
        Check(!File.Exists(f.Metadata("CHERRY_PICK_HEAD")), "This real single -n conflict must have no native cherry-pick marker.");
        f.Write("base.txt", "resolved\n"); await f.Git("add", "base.txt");
        GitCherryPickRecovery recovered = await new GitRepository(f.Path).GetCherryPickRecoveryAsync();
        Check(recovered.CanContinue, "Resolution before reopening must be discovered from the current index.");
        await new GitRepository(f.Path).ResumeCherryPickAsync(recovered.Session!, GitOperationAction.Continue);
        Check(await f.Text("rev-parse", "HEAD") == head && await f.Text("show", ":base.txt") == "resolved" && await f.Text("show", ":prior.txt") == "index bytes"
            && File.ReadAllText(f.File("prior.txt")) == "worktree bytes\n", "Continue keeps resolved/index/worktree bytes without inventing a commit.");
    }

    private static async Task DirectoryMovesAsync()
    {
        foreach (bool staged in new[] { false, true })
        foreach (string mode in new[] { "false", "true", "conflict" })
        foreach (GitOperationAction action in new[] { GitOperationAction.Abort, GitOperationAction.Skip })
        {
            using Fixture f = await Fixture.Create(); await f.Commit("old/seed.txt", "seed\n");
            string pick = await f.DirectoryPick(); await f.Git("mv", "old", "new"); if (!staged) { await f.Git("commit", "-m", "Move directory"); }
            await f.Git("config", "merge.directoryRenames", mode);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(f.File("new"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            string before = await f.State(); GitCherryPickResult pending = await f.Repo.CherryPickAsync(new GitCherryPickOptions(pick));
            Check(!pending.Success && pending.ConflictedPaths.Contains("base.txt"), "The content conflict keeps every directory-mode pick pending.");
            Check(File.Exists(f.File(mode == "false" ? "old/added.txt" : "new/added.txt")), "Actual Git must apply the addition at the expected directory-mode destination.");
            GitCherryPickRecovery recovered = await new GitRepository(f.Path).GetCherryPickRecoveryAsync(); await new GitRepository(f.Path).ResumeCherryPickAsync(recovered.Session!, action);
            Check(await f.State() == before, "Committed/staged directory movement must recover exact file/directory sets, modes, raw index and metadata.");
        }
    }

    private static async Task NestedSplitAsync()
    {
        foreach (bool nested in new[] { false, true })
        {
            using Fixture f = await Fixture.Create(); string old = nested ? "old/deep" : "old";
            await f.Commit(old + "/one.txt", "one\n"); await f.Commit(old + "/two.txt", "two\n"); await f.Commit(old + "/three.txt", "three\n");
            string pick = await f.DirectoryPick(old);
            Directory.CreateDirectory(f.File(nested ? "new/deeper" : "left")); Directory.CreateDirectory(f.File(nested ? "new/deeper" : "right"));
            await f.Git("mv", old + "/one.txt", (nested ? "new/deeper" : "left") + "/one.txt");
            await f.Git("mv", old + "/two.txt", (nested ? "new/deeper" : "left") + "/two.txt");
            await f.Git("mv", old + "/three.txt", (nested ? "new/deeper" : "right") + "/three.txt"); await f.Git("commit", "-m", "Move nested/split directories");
            await f.Git("config", "merge.directoryRenames", "conflict"); string before = await f.State();
            GitCherryPickResult pending = await f.Repo.CherryPickAsync(new GitCherryPickOptions(pick)); Check(!pending.Success, "Native nested/split merge must remain pending.");
            GitCherryPickRecovery recovered = await new GitRepository(f.Path).GetCherryPickRecoveryAsync(); await f.Repo.ResumeCherryPickAsync(recovered.Session!, GitOperationAction.Abort);
            Check(await f.State() == before, "Every possible split/nested destination must have a pre-mutation preimage.");
        }
    }

    private static async Task CollisionAsync()
    {
        foreach (bool ignored in new[] { false, true })
        {
            using Fixture f = await Fixture.Create(); await f.Commit("old/seed.txt", "seed\n"); string pick = await f.DirectoryPick();
            await f.Git("mv", "old", "new"); await f.Git("commit", "-m", "Move directory"); await f.Git("config", "merge.directoryRenames", "conflict");
            if (ignored) { File.AppendAllText(f.Metadata("info/exclude"), "\nnew/added.txt\n"); }
            f.Write("new/added.txt", [0, 255, 13, 128]); f.Write("unrelated-data.bin", [1, 0, 254]); string before = await f.State();
            try { await f.Repo.CherryPickAsync(new GitCherryPickOptions(pick)); } catch (GitCommandException) { }
            GitCherryPickRecovery recovered = await new GitRepository(f.Path).GetCherryPickRecoveryAsync();
            Check(recovered.State == GitCherryPickRecoveryState.Pending, "Native refusal or a conflict must retain its managed preimages.");
            await f.Repo.ResumeCherryPickAsync(recovered.Session!, GitOperationAction.Abort);
            Check(await f.State() == before && File.ReadAllBytes(f.File("new/added.txt")).SequenceEqual(new byte[] { 0, 255, 13, 128 }), "Related nontracked/ignored collision bytes and unrelated data survive exactly.");
        }
    }

    private static async Task InvalidJournalAsync()
    {
        using Fixture f = await Fixture.Create(); string pick = await f.Conflict(); await f.Repo.CherryPickAsync(new GitCherryPickOptions(pick));
        byte[] original = File.ReadAllBytes(f.Manifest);
        foreach (bool missing in new[] { false, true })
        {
            if (missing) { File.Delete(f.Manifest); } else { File.WriteAllText(f.Manifest, "{invalid snapshot}"); }
            string interrupted = await f.State(); GitCherryPickRecovery recovered = await new GitRepository(f.Path).GetCherryPickRecoveryAsync();
            Check(recovered.State == GitCherryPickRecoveryState.Invalid && recovered.Session is null && !recovered.CanContinue, "Missing/corrupt journal must be explicit, never absent.");
            await Expect<InvalidOperationException>(() => f.Repo.CherryPickAsync(new GitCherryPickOptions(pick)));
            Check(await f.State() == interrupted && (missing ? !File.Exists(f.Manifest) : File.ReadAllText(f.Manifest) == "{invalid snapshot}"), "A new pick must not discard invalid recovery or touch repository state.");
            File.WriteAllBytes(f.Manifest, original);
        }
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(f.Manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        GitCherryPickRecovery valid = await f.Repo.GetCherryPickRecoveryAsync(); await f.Repo.ResumeCherryPickAsync(valid.Session!, GitOperationAction.Abort);
    }

    private static async Task OwnershipAsync()
    {
        using Fixture f = await Fixture.Create(); string pick = await f.Conflict(); await f.Repo.CherryPickAsync(new GitCherryPickOptions(pick));
        GitCherryPickSession session = (await f.Repo.GetCherryPickRecoveryAsync()).Session!;
        f.Write("outside-selection.txt", "external\n"); string state = await f.State(), journal = Digest(File.ReadAllBytes(f.Manifest));
        await Expect<InvalidOperationException>(() => f.Repo.ResumeCherryPickAsync(session, GitOperationAction.Abort));
        Check(await f.State() == state && Digest(File.ReadAllBytes(f.Manifest)) == journal, "Unknown paths must still refuse before phase/metadata/file writes."); File.Delete(f.File("outside-selection.txt"));
        File.WriteAllText(f.Metadata("MERGE_HEAD"), pick + "\n"); state = await f.State(); await Expect<InvalidOperationException>(() => f.Repo.ResumeCherryPickAsync(session, GitOperationAction.Skip));
        Check(await f.State() == state && Digest(File.ReadAllBytes(f.Manifest)) == journal, "Foreign native operation must preserve the saved pick and current state."); File.Delete(f.Metadata("MERGE_HEAD"));
        File.WriteAllText(f.Metadata("index.lock"), "external owner\n"); state = await f.State(); await Expect<IOException>(() => f.Repo.ResumeCherryPickAsync(session, GitOperationAction.Abort));
        Check(await f.State() == state && File.ReadAllText(f.Metadata("index.lock")) == "external owner\n", "An owned index lock must not be removed."); File.Delete(f.Metadata("index.lock"));
        await f.Repo.ResumeCherryPickAsync(session, GitOperationAction.Abort);
    }

    private static async Task LinkedSplitAsync()
    {
        using Fixture f = await Fixture.Create(); string pick = await f.Conflict(); string linked = System.IO.Path.Combine(f.Root, "linked λ");
        await f.Git("worktree", "add", "-b", "linked", linked);
        GitRunner runner = new(); async Task Run(params string[] args) { GitResult result = await runner.RunAsync(linked, args); Check(result.Success, result.StandardError); }
        await Run("update-index", "--split-index"); GitRepository repository = await GitRepository.OpenAsync(linked); GitRepositoryInfo info = await repository.GetInfoAsync();
        byte[] original = File.ReadAllBytes(System.IO.Path.Combine(info.GitDirectory, "index"));
        await repository.CherryPickAsync(new GitCherryPickOptions(pick));
        Check((await f.Repo.GetCherryPickRecoveryAsync()).State == GitCherryPickRecoveryState.Absent, "Sibling worktrees sharing common objects must have independent journals.");
        GitRepository reopened = await GitRepository.OpenAsync(linked); GitCherryPickRecovery recovered = await reopened.GetCherryPickRecoveryAsync();
        await reopened.ResumeCherryPickAsync(recovered.Session!, GitOperationAction.Abort);
        Check(File.ReadAllBytes(System.IO.Path.Combine(info.GitDirectory, "index")).SequenceEqual(original), "The linked worktree's exact split index must be restored."); await Run("ls-files", "--stage");
    }

    private static async Task ProcessRestartAsync()
    {
        using Fixture f = await Fixture.Create(); string pick = await f.Conflict(staged: true); string originalObject = await f.Text("rev-parse", ":base.txt"), before = await f.State();
        await f.Repo.CherryPickAsync(new GitCherryPickOptions(pick)); f.Write("base.txt", "resolved and staged\n"); await f.Git("add", "base.txt");
        // add retains the old conflict stages in REUC so checkout -m can undo
        // resolution. GC correctly treats those index objects as reachable.
        string resolveUndo = await f.Text("ls-files", "--resolve-undo", "-z");
        Check(resolveUndo.Contains(originalObject + " 2\tbase.txt\0", StringComparison.Ordinal), "The original staged blob must really be retained by resolve-undo before simulating its expiry.");
        await f.Git("update-index", "--clear-resolve-undo");
        Check(await f.Text("ls-files", "--resolve-undo", "-z") == "", "Native resolve-undo cleanup must remove the remaining index reachability before pruning.");
        await f.Git("gc", "--prune=now"); GitResult missing = await f.Runner.RunAsync(f.Path, ["cat-file", "-e", originalObject]);
        Check(!missing.Success, "The original staged-only blob really must have been pruned after resolution.");
        string executable = Environment.ProcessPath ?? throw new Exception("Core scenarios require their apphost.");
        Check(System.IO.Path.GetFileNameWithoutExtension(executable) != "dotnet", "Restart proof must launch the Core.Tests apphost, not the dotnet DLL host.");
        ProcessStartInfo start = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--cherry-pick-recovery-abort"); start.ArgumentList.Add(f.Path);
        using Process child = Process.Start(start) ?? throw new Exception("Cannot start recovery worker.");
        Task<string> output = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Check(child.ExitCode == 0, "Restarted worker failed: " + await output + "\n" + await error);
        Check(await f.State() == before && await f.Text("show", ":base.txt") == "staged ours", "A separate process restores index/tree/blob bytes after real GC without changing HEAD or worktree baseline.");
    }

    internal static async Task<int> RunRecoveryWorkerAsync(string path)
    {
        GitRepository repository = new(path); GitCherryPickRecovery recovery = await repository.GetCherryPickRecoveryAsync();
        Check(recovery.State == GitCherryPickRecoveryState.Pending && recovery.Session is not null, recovery.Error ?? "No pending recovery after restart.");
        await repository.ResumeCherryPickAsync(recovery.Session!, GitOperationAction.Abort); Console.WriteLine("Recovered original pick " + recovery.Session!.Options.Revision); return 0;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GitRunner Runner = new(); internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-recovery-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "repo"); internal GitRepository Repo => new(Path);
        internal string File(string path) => System.IO.Path.Combine(Path, path); internal string Metadata(string path) => System.IO.Path.Combine(Path, ".git", path);
        internal string Journal => Metadata("gitextensions-cherry-pick"); internal string Manifest => System.IO.Path.Combine(Journal, "recovery.json");
        internal void Write(string path, string text) => Write(path, System.Text.Encoding.UTF8.GetBytes(text));
        internal void Write(string path, byte[] bytes) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File(path))!); System.IO.File.WriteAllBytes(File(path), bytes); }
        internal static async Task<Fixture> Create()
        {
            Fixture f = new(); Directory.CreateDirectory(f.Path); await f.Git("init", "-b", "main"); await f.Git("config", "user.name", "Recovery Test"); await f.Git("config", "user.email", "recovery@example.invalid");
            await f.Git("config", "commit.gpgsign", "false"); await f.Git("config", "core.hooksPath", "/dev/null"); await f.Commit("base.txt", "base\n"); return f;
        }
        internal async Task Git(params string[] args) { GitResult result = await Runner.RunAsync(Path, args, environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" }); Check(result.Success, result.StandardError); }
        internal async Task<string> Text(params string[] args) { GitResult result = await Runner.RunAsync(Path, args, environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" }); Check(result.Success, result.StandardError); return result.StandardOutput.TrimEnd('\r', '\n'); }
        internal async Task Commit(string path, string text) { Write(path, text); await Git("add", "--", path); await Git("commit", "-m", "Commit " + path); }
        internal async Task<string> Conflict(bool staged = false)
        {
            await Git("checkout", "-b", "topic"); await Commit("base.txt", "theirs\n"); string pick = await Text("rev-parse", "HEAD"); await Git("checkout", "main");
            if (staged) { Write("base.txt", "staged ours\n"); await Git("add", "base.txt"); Write("base.txt", "staged ours\n"); }
            else { await Commit("base.txt", "ours\n"); }
            return pick;
        }
        internal async Task<string> DirectoryPick(string old = "old")
        {
            await Git("checkout", "-b", "topic"); Write(old + "/added.txt", "addition\n"); Write("base.txt", "theirs\n"); await Git("add", "."); await Git("commit", "-m", "Pick directory addition");
            string pick = await Text("rev-parse", "HEAD"); await Git("checkout", "main"); await Commit("base.txt", "ours\n"); return pick;
        }
        internal async Task<string> State()
        {
            List<string> state = [await Text("rev-parse", "HEAD"), await Text("symbolic-ref", "HEAD"), await Text("for-each-ref"), await Text("config", "--local", "--list")];
            foreach (string name in new[] { "index", "CHERRY_PICK_HEAD", "MERGE_MSG", "AUTO_MERGE", "MERGE_HEAD" }) { state.Add(name + ":" + (System.IO.File.Exists(Metadata(name)) ? Digest(System.IO.File.ReadAllBytes(Metadata(name))) : "absent")); }
            void Visit(string directory)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    if (entry == Metadata("")) { continue; }
                    FileInfo details = new(entry); string relative = System.IO.Path.GetRelativePath(Path, entry);
                    if (details.LinkTarget is string link) { state.Add("link:" + relative + ":" + link); }
                    else if (Directory.Exists(entry)) { state.Add("directory:" + relative + ":" + (OperatingSystem.IsWindows() ? "" : System.IO.File.GetUnixFileMode(entry).ToString())); Visit(entry); }
                    else { state.Add("file:" + relative + ":" + Digest(System.IO.File.ReadAllBytes(entry)) + ":" + (OperatingSystem.IsWindows() ? "" : System.IO.File.GetUnixFileMode(entry).ToString())); }
                }
            }
            Visit(Path); return string.Join("\n", state);
        }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}
