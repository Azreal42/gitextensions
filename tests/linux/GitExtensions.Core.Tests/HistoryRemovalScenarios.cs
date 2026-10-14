using System.Text;
using GitExtensions.Linux;

internal static class HistoryRemovalScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("history removal rewrites the union once across branches/light tags and prunes selected-only commits", Guard(UnionAsync)),
        ("history removal NUL paths preserve quotes newlines shell syntax and literal pathspec magic", Guard(LiteralPathsAsync)),
        ("history removal preserves annotated tag targets native warnings and reachable shared blobs", Guard(AnnotatedTagsAsync)),
        ("history removal validates all paths and pre-cancellation before any native mutation", Guard(ValidationAsync)),
        ("history removal retains native dirty unborn and preexisting-backup refusals without purge", Guard(NativeRefusalsAsync)),
        ("history removal supports native bare rewriting without a worktree", Guard(BareAsync)),
        ("history removal linked worktrees retain native peer index and shared-ref behavior", Guard(LinkedAsync)),
        ("history removal uses configured absolute relative and simple Git wrappers for the index filter", Guard(ConfiguredGitAsync)),
        ("history removal compare-and-delete preserves a concurrently changed backup and stops cleanup", Guard(BackupRaceAsync)),
        ("history removal stops on native backup-enumeration and reflog failures and retains partial effects", Guard(CleanupFailuresAsync)),
        ("history removal cancellation retains streamed native output and rewritten refs without starting purge", Guard(CancellationAsync)),
        ("history removal observer and launch failures retain steps without invented exits or later cleanup", Guard(ProcessFailuresAsync)),
    ];

    private static Func<Task> Guard(Func<Task> scenario) => async () =>
    {
        string? warning = Environment.GetEnvironmentVariable("FILTER_BRANCH_SQUELCH_WARNING");
        // Avoid Git's ten-second advisory delay only inside private test fixtures.
        Environment.SetEnvironmentVariable("FILTER_BRANCH_SQUELCH_WARNING", "1");
        try { await scenario(); } finally { Environment.SetEnvironmentVariable("FILTER_BRANCH_SQUELCH_WARNING", warning); }
    };

    private static async Task UnionAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string seed = await Head(fixture.Repository);
        await CommitFile(fixture, "first.bin", "first payload", "Only first selected file");
        await fixture.Repository.CreateBranchAsync("feature", checkout: false);
        await CommitFile(fixture, "second.bin", "second payload", "Only second selected file");
        string oldMain = await Head(fixture.Repository);
        string removedBlob = (await Git(fixture.Repository, ["rev-parse", "HEAD:second.bin"])).StandardOutput.TrimEnd('\n');
        await Git(fixture.Repository, ["tag", "light-main"]);
        await fixture.Repository.CheckoutAsync("feature");
        await CommitFile(fixture, "seed.txt", "feature retained\n", "Retained feature change");
        string oldFeature = await Head(fixture.Repository);
        await Git(fixture.Repository, ["tag", "light-feature"]);
        await fixture.Repository.CheckoutAsync("main");
        GitHistoryRemovalResult result = await fixture.Repository.RemovePathsFromHistoryAsync(["first.bin", "second.bin", "first.bin"]);
        Check(result.Success && result.MutationStarted && !result.Cancelled && result.Paths.SequenceEqual(["first.bin", "second.bin"]), "The ordinal union is snapshotted and rewritten successfully.");
        AssertSuccessfulSequence(result);
        Check(result.Steps.Count(step => step.Step == GitHistoryRemovalStep.RewriteHistory) == 1, "Exactly one filter-branch handles every selected path.");
        string[] arguments = result.Steps[0].Arguments.ToArray();
        Check(arguments[0] == "filter-branch" && arguments[^3..].SequenceEqual(["--prune-empty", "--", "--all"])
            && !arguments.Contains("-f") && !arguments.Contains("--tag-name-filter"), "The original native options remain explicit, without force or tag-name rewriting.");
        Check(await Head(fixture.Repository) == seed && await Revision(fixture.Repository, "light-main") == seed, "Prune-empty removes both selected-only main commits and rewrites the lightweight tag.");
        string feature = await Revision(fixture.Repository, "feature");
        Check(feature != oldFeature && feature != oldMain && await Revision(fixture.Repository, "light-feature") == feature, "The retained feature change and its lightweight tag point at rewritten history.");
        await AssertNoPaths(fixture.Repository, ["first.bin", "second.bin"]);
        Check(!(await Git(fixture.Repository, ["cat-file", "-e", removedBlob], false)).Success, "An unreferenced selected blob is actually pruned.");
        Check((await fixture.Repository.GetStatusAsync()).Changes.Count == 0 && !File.Exists(fixture.File("first.bin")) && !File.Exists(fixture.File("second.bin"))
            && (await Git(fixture.Repository, ["ls-files", "-z"])).StandardOutput == "seed.txt\0", "Native final read-tree updates the current index/worktree without leaving staged deletions.");
        Check((await Git(fixture.Repository, ["for-each-ref", "refs/original/"])).StandardOutput.Length == 0
            && (await Git(fixture.Repository, ["reflog", "show", "--all"])).StandardOutput.Length == 0, "Successful cleanup removes original backups and expires all reflogs.");
    }

    private static async Task LiteralPathsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string[] selected = ["quote'\";$(touch canary)", "line\nbreak", "carriage\rreturn", "*.tmp", ":(glob)*.txt", "-danger", " leading trailing ", "α λ.bin", "back\\slash", "Case.bin", "case.bin"];
        foreach (string path in selected) { fixture.Write(path, "selected " + path); }
        foreach (string path in new[] { "keep.tmp", "keep.txt", "canary-safe", "CASE.bin" }) { fixture.Write(path, "retained " + path); }
        await fixture.Repository.StageAllAsync();
        await fixture.Repository.CommitAsync("Literal path selection");
        GitHistoryRemovalResult result = await fixture.Repository.RemovePathsFromHistoryAsync([.. selected, fixture.File("Case.bin")]);
        Check(result.Success && result.Paths.Count == selected.Length, "Distinct is ordinal; absolute/relative copies of one path collapse while case variants remain separate.");
        await AssertNoPaths(fixture.Repository, selected);
        foreach (string path in new[] { "keep.tmp", "keep.txt", "canary-safe", "CASE.bin" }) { Check(File.Exists(fixture.File(path)), "Literal magic/wildcards retain unrelated sibling: " + path); }
        Check(!File.Exists(fixture.File("canary")) && !Directory.Exists(fixture.File("canary")), "Filename shell syntax never executes a program.");
        Check((await fixture.Repository.GetStatusAsync()).Changes.Count == 0, "Literal rewriting leaves the native worktree clean.");
        Check(!result.Steps[0].Arguments[2].Contains(selected[0], StringComparison.Ordinal)
            && result.Steps[0].Arguments[2].Contains("--pathspec-file-nul", StringComparison.Ordinal), "The index-filter contains fixed code, with NUL path transport instead of interpolated values.");
    }

    private static async Task AnnotatedTagsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        await CommitFile(fixture, "large.bin", "shared retained payload", "Large file");
        fixture.Write("retained-copy.bin", "shared retained payload");
        await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync("Retain a shared blob");
        await Git(fixture.Repository, ["tag", "-a", "annotated", "-m", "Retained annotated target"]);
        string tagObject = await Revision(fixture.Repository, "annotated");
        string tagCommit = await Revision(fixture.Repository, "annotated^{commit}");
        string blob = (await Git(fixture.Repository, ["rev-parse", "HEAD:large.bin"])).StandardOutput.TrimEnd('\n');
        GitHistoryRemovalResult result = await fixture.Repository.RemovePathsFromHistoryAsync(["large.bin"]);
        Check(result.Success && await Head(fixture.Repository) != tagCommit && await Revision(fixture.Repository, "annotated") == tagObject
            && await Revision(fixture.Repository, "annotated^{commit}") == tagCommit, "Annotated tag objects and their old targets retain the native no-tag-name-filter semantics.");
        Check(result.Steps[0].StandardOutput.Contains("--tag-name-filter", StringComparison.Ordinal)
            || result.Steps[0].StandardError.Contains("--tag-name-filter", StringComparison.Ordinal), "The native annotated-tag warning remains in the actual command output.");
        Check((await Git(fixture.Repository, ["cat-file", "-e", blob])).Success
            && (await Git(fixture.Repository, ["show", "annotated:large.bin"])).StandardOutput == "shared retained payload", "Success does not claim a blob disappeared when an annotated tag or another pathname still retains it.");
        Check(!File.Exists(fixture.File("large.bin")) && File.Exists(fixture.File("retained-copy.bin")), "Only the current branch's selected name disappears from its worktree.");
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string before = await State(fixture.Repository);
        foreach (IReadOnlyList<string> paths in new IReadOnlyList<string>[] { [], [""], ["seed.txt", "bad\0name"], ["seed.txt", ".."], ["."], [fixture.Repository.Path], ["/"], [null!], null! })
        {
            GitHistoryRemovalResult result = await fixture.Repository.RemovePathsFromHistoryAsync(paths);
            Check(!result.Success && !result.MutationStarted && !result.Cancelled && result.Error is not null && result.Steps.Count == 0, "Invalid entire selection is rejected before any mutation or native cleanup.");
            Check(await State(fixture.Repository) == before, "Invalid selection preserves refs, reflogs, index and tracked content.");
        }
        using CancellationTokenSource token = new(); token.Cancel();
        GitHistoryRemovalResult cancelled = await fixture.Repository.RemovePathsFromHistoryAsync(["seed.txt"], token.Token);
        Check(cancelled.Cancelled && !cancelled.MutationStarted && cancelled.Steps.Count == 0 && await State(fixture.Repository) == before, "Pre-cancellation neither launches Git nor invents a command result.");
    }

    private static async Task NativeRefusalsAsync()
    {
        foreach (bool staged in new[] { false, true })
        {
            using RepositoryFixture fixture = await SelectedFixture();
            fixture.Write("seed.txt", "dirty retained\n"); if (staged) { await fixture.Repository.StageAllAsync(); }
            string before = await State(fixture.Repository);
            GitHistoryRemovalResult result = await fixture.Repository.RemovePathsFromHistoryAsync(["large.bin"]);
            Check(!result.Success && result.MutationStarted && result.Steps.Count == 1 && result.Steps[0].Result is { Success: false }
                && result.Steps[0].StandardError.Contains("Cannot rewrite branches", StringComparison.Ordinal), "Dirty worktree/index rejection is Git's own diagnostic and exit.");
            Check(await State(fixture.Repository) == before && result.Steps[0].StandardOutputBytes.SequenceEqual(result.Steps[0].Result!.StandardOutputBytes), "The native dirty refusal preserves index, worktree, refs and reflogs with no cleanup.");
        }
        using (RepositoryFixture fixture = await SelectedFixture())
        {
            string oldHead = await Head(fixture.Repository);
            await Git(fixture.Repository, ["update-ref", "refs/original/refs/heads/main", oldHead]);
            string before = await State(fixture.Repository);
            GitHistoryRemovalResult result = await fixture.Repository.RemovePathsFromHistoryAsync(["large.bin"]);
            Check(!result.Success && result.Steps.Count == 1 && result.Steps[0].StandardError.Contains("A previous backup already exists", StringComparison.Ordinal), "Git's preexisting backup refusal remains actionable; force is never supplied.");
            Check(await State(fixture.Repository) == before && await Revision(fixture.Repository, "refs/original/refs/heads/main") == oldHead, "A refused rewrite must never erase the existing backup or expire its reflogs.");
        }
        using (RepositoryFixture fixture = await RepositoryFixture.CreateAsync(seed: false))
        {
            GitHistoryRemovalResult result = await fixture.Repository.RemovePathsFromHistoryAsync(["large.bin"]);
            Check(!result.Success && result.Steps.Count == 1 && result.Steps[0].Result is { Success: false }, "Unborn history retains native filter-branch's refusal without later purge.");
        }
    }

    private static async Task BareAsync()
    {
        using RepositoryFixture fixture = await SelectedFixture();
        string barePath = System.IO.Path.Combine(fixture.Root, "bare.git");
        await Git(fixture.Repository, ["clone", "--bare", "--", fixture.Repository.Path, barePath]);
        GitRepository bare = await GitRepository.OpenAsync(barePath);
        string original = await Head(fixture.Repository);
        GitHistoryRemovalResult result = await bare.RemovePathsFromHistoryAsync(["large.bin"]);
        Check(result.Success && bare.IsBare && await Head(bare) != original && await Head(fixture.Repository) == original, "Native filter-branch rewrites a bare fixture without worktree preflight or modifying its source fixture.");
        await AssertNoPaths(bare, ["large.bin"]);
    }

    private static async Task LinkedAsync()
    {
        using RepositoryFixture fixture = await SelectedFixture();
        string linkedPath = System.IO.Path.Combine(fixture.Root, "linked worktree λ");
        await Git(fixture.Repository, ["worktree", "add", "-b", "linked", "--", linkedPath, "HEAD"]);
        GitRepository linked = await GitRepository.OpenAsync(linkedPath);
        byte[] peerIndex = File.ReadAllBytes(fixture.File(".git/index"));
        GitHistoryRemovalResult result = await linked.RemovePathsFromHistoryAsync(["large.bin"]);
        Check(result.Success && (await linked.GetStatusAsync()).Changes.Count == 0 && !File.Exists(System.IO.Path.Combine(linkedPath, "large.bin")), "Native final read-tree applies only to the invoking linked worktree.");
        Check(File.Exists(fixture.File("large.bin")) && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(peerIndex)
            && await Head(fixture.Repository) == await Head(linked), "Shared branch refs are rewritten while the peer index/worktree retain native unchanged contents.");
        Check((await fixture.Repository.GetStatusAsync()).Changes.Any(change => change.Path == "large.bin"), "The peer's retained index now differs from its rewritten shared ref; no extra reset hides that native outcome.");
    }

    private static async Task ConfiguredGitAsync()
    {
        RequireLinux();
        foreach (string mode in new[] { "absolute", "relative", "simple" })
        {
            using RepositoryFixture fixture = await SelectedFixture();
            string trace = System.IO.Path.Combine(fixture.Root, "commands");
            string wrapper = Wrapper(fixture, "configured-git", Trace(trace) + "exec " + Quote(GitExecutable()) + " \"$@\"\n");
            string parentDirectory = Directory.GetCurrentDirectory();
            try
            {
                string configured = wrapper;
                if (mode != "absolute") { Directory.SetCurrentDirectory(fixture.Root); configured = mode == "relative" ? "./configured-git" : "configured-git"; }
                GitRepository repository = new(fixture.Repository.Path, new GitRunner(configured));
                GitHistoryRemovalResult result = await repository.RemovePathsFromHistoryAsync(["large.bin"]);
                Check(result.Success, "The configured " + mode + " executable is usable after filter-branch changes cwd: " + string.Join("\n", result.Errors));
                string[] commands = File.ReadAllLines(trace);
                Check(commands.Count(command => command == "filter-branch") == 1 && commands.Count(command => command == "rm") >= 2,
                    "Both the outer filter and every index-filter use the configured wrapper.");
                Check(commands[^1] == "gc", "The configured wrapper also runs final cleanup.");
            }
            finally { Directory.SetCurrentDirectory(parentDirectory); }
        }
    }

    private static async Task BackupRaceAsync()
    {
        RequireLinux(); using RepositoryFixture fixture = await SelectedFixture();
        string original = await Head(fixture.Repository), seed = await Revision(fixture.Repository, "HEAD^");
        string trace = System.IO.Path.Combine(fixture.Root, "commands");
        string real = Quote(GitExecutable());
        string wrapper = Wrapper(fixture, "racing-git", Trace(trace) + "if [ \"$history_command\" = for-each-ref ]; then\n"
            + real + " \"$@\" || exit\n" + real + " update-ref refs/original/refs/heads/main " + Quote(seed) + "\nexit $?\nfi\nexec " + real + " \"$@\"\n");
        GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
        GitHistoryRemovalResult result = await repository.RemovePathsFromHistoryAsync(["large.bin"]);
        Check(!result.Success && result.Steps[^1].Step == GitHistoryRemovalStep.DeleteBackupReference && result.Steps[^1].Result is { Success: false }
            && result.Steps[^1].Arguments[^1] == original, "Backup deletion carries the enumerated expected OID and fails through native compare-and-delete after a race.");
        Check(await Revision(repository, "refs/original/refs/heads/main") == seed && !File.ReadAllLines(trace).Contains("reflog") && !File.ReadAllLines(trace).Contains("gc"), "The changed backup remains and reflog/object cleanup never starts.");
        Check((await Git(repository, ["reflog", "show", "--all"])).StandardOutput.Contains(original[..7], StringComparison.Ordinal), "Actual rewrite effects and old reflog entries remain reviewable after cleanup stops.");
    }

    private static async Task CleanupFailuresAsync()
    {
        RequireLinux();
        foreach (string failing in new[] { "for-each-ref", "reflog", "gc" })
        {
            using RepositoryFixture fixture = await SelectedFixture();
            string original = await Head(fixture.Repository), trace = System.IO.Path.Combine(fixture.Root, "commands");
            string real = Quote(GitExecutable());
            string wrapper = Wrapper(fixture, "failing-cleanup", Trace(trace) + "if [ \"$history_command\" = " + Quote(failing)
                + " ]; then exec " + real + " " + Quote(failing) + " --invalid-history-cleanup-option; fi\nexec " + real + " \"$@\"\n");
            GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
            GitHistoryRemovalResult result = await repository.RemovePathsFromHistoryAsync(["large.bin"]);
            GitHistoryRemovalStep expected = failing == "for-each-ref" ? GitHistoryRemovalStep.EnumerateBackupReferences
                : failing == "reflog" ? GitHistoryRemovalStep.ExpireReflogs : GitHistoryRemovalStep.GarbageCollect;
            Check(!result.Success && result.MutationStarted && !result.Cancelled && result.Steps[^1].Step == expected
                && result.Steps[^1].Result is { Success: false } && result.Steps[^1].StandardError.Length != 0, "An actual native cleanup failure remains typed with its real diagnostic/exit.");
            Check(await Head(repository) != original && !File.Exists(fixture.File("large.bin")), "A successful rewrite is not rolled back or misreported after cleanup failure.");
            string[] commands = File.ReadAllLines(trace);
            Check(commands[^1] == failing && (failing == "gc" || !commands.Contains("gc")), "Nothing follows the first failed cleanup command.");
            bool backupsRemain = (await Git(fixture.Repository, ["for-each-ref", "refs/original/"])).StandardOutput.Length != 0;
            Check(backupsRemain == (failing == "for-each-ref"), "Partial backup deletion effects are faithfully retained when later cleanup fails.");
        }
    }

    private static async Task CancellationAsync()
    {
        RequireLinux(); using RepositoryFixture fixture = await SelectedFixture();
        string original = await Head(fixture.Repository), trace = System.IO.Path.Combine(fixture.Root, "commands");
        string wrapper = Wrapper(fixture, "cancelling-git", Trace(trace) + "if [ \"$history_command\" = filter-branch ]; then\n"
            + Quote(GitExecutable()) + " \"$@\" || exit\nprintf 'rewrite finished before cancellation\\n'\nwhile :; do sleep 1; done\nfi\nexec " + Quote(GitExecutable()) + " \"$@\"\n");
        GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
        using CancellationTokenSource token = new(TimeSpan.FromSeconds(15));
        using MemoryStream stdout = new(), stderr = new();
        GitHistoryRemovalResult result = await repository.RemovePathsFromHistoryAsync(["large.bin"], token.Token, item =>
        {
            lock (stdout)
            {
                (item.Stream == GitProcessOutputStream.StandardOutput ? stdout : stderr).Write(item.Bytes.Span);
                if (Encoding.UTF8.GetString(stdout.ToArray()).Contains("rewrite finished before cancellation", StringComparison.Ordinal)) { token.Cancel(); }
            }
        });
        Check(result.Cancelled && !result.Success && result.MutationStarted && result.Steps.Count == 1 && result.Steps[0].Result is null
            && result.Steps[0].StandardOutput.Contains("rewrite finished before cancellation", StringComparison.Ordinal), "In-flight cancellation retains observed native output and unknown exit without a fabricated GitResult.");
        Check(result.Steps[0].StandardOutputBytes.SequenceEqual(stdout.ToArray()) && result.Steps[0].StandardErrorBytes.SequenceEqual(stderr.ToArray()), "Cancelled output remains byte-exact in both streams.");
        Check(await Head(fixture.Repository) != original && (await Git(fixture.Repository, ["for-each-ref", "refs/original/"])).StandardOutput.Length != 0
            && !File.ReadAllLines(trace).Contains("reflog") && !File.ReadAllLines(trace).Contains("gc"), "Completed native rewrite effects and backups survive cancellation without purge.");
    }

    private static async Task ProcessFailuresAsync()
    {
        RequireLinux();
        using (RepositoryFixture fixture = await SelectedFixture())
        {
            string trace = System.IO.Path.Combine(fixture.Root, "commands");
            string wrapper = Wrapper(fixture, "observer-git", Trace(trace) + "if [ \"$history_command\" = filter-branch ]; then printf 'observer failure output\\n'; while :; do sleep 1; done; fi\nexec " + Quote(GitExecutable()) + " \"$@\"\n");
            GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
            GitHistoryRemovalResult result = await repository.RemovePathsFromHistoryAsync(["large.bin"], outputReceived: _ => throw new InvalidOperationException("observer fixture failure"));
            Check(!result.Success && !result.Cancelled && result.MutationStarted && result.Steps.Count == 1 && result.Steps[0].Result is null
                && result.Steps[0].Error == "observer fixture failure" && result.Steps[0].StandardOutput.Contains("observer failure output", StringComparison.Ordinal), "Observer failure preserves its original error and witnessed bytes after runner termination.");
            Check(!File.ReadAllLines(trace).Contains("reflog") && !File.ReadAllLines(trace).Contains("gc"), "Observer failure never proceeds to cleanup.");
        }
        using (RepositoryFixture fixture = await SelectedFixture())
        {
            string wrapper = Wrapper(fixture, "launch-git", CommandSelector + "if [ \"$history_command\" = rev-parse ]; then exec " + Quote(GitExecutable()) + " \"$@\"; fi\nexit 1\n");
            GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
            await repository.GetInfoAsync(); // Cache readonly discovery before removing the executable.
            File.Delete(wrapper);
            GitHistoryRemovalResult result = await repository.RemovePathsFromHistoryAsync(["large.bin"]);
            Check(!result.Success && result.MutationStarted && result.Steps.Count == 1 && result.Steps[0].Result is null && result.Steps[0].Error is not null
                && result.Steps[0].StandardOutputBytes.Length == 0 && result.Steps[0].StandardErrorBytes.Length == 0, "An unlaunchable mutation records its attempted step with no invented output or exit.");
        }
    }

    private static async Task<RepositoryFixture> SelectedFixture()
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        await CommitFile(fixture, "large.bin", "selected large payload", "Selected payload"); return fixture;
    }
    private static async Task CommitFile(RepositoryFixture fixture, string path, string content, string message)
    { fixture.Write(path, content); await fixture.Repository.StageAllAsync(); await fixture.Repository.CommitAsync(message); }
    private static Task<string> Head(GitRepository repository) => Revision(repository, "HEAD");
    private static async Task<string> Revision(GitRepository repository, string revision)
        => (await Git(repository, ["rev-parse", "--verify", revision])).StandardOutput.TrimEnd('\n');
    private static async Task AssertNoPaths(GitRepository repository, IReadOnlyList<string> paths)
    {
        string[] commits = (await Git(repository, ["rev-list", "--all"])).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string commit in commits)
        {
            string[] entries = (await Git(repository, ["ls-tree", "-r", "--name-only", "-z", commit])).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            Check(!entries.Intersect(paths, StringComparer.Ordinal).Any(), "A rewritten reachable commit must not retain the selected path.");
        }
    }
    private static void AssertSuccessfulSequence(GitHistoryRemovalResult result)
    {
        Check(result.Steps[0].Step == GitHistoryRemovalStep.RewriteHistory && result.Steps[1].Step == GitHistoryRemovalStep.EnumerateBackupReferences
            && result.Steps[^2].Step == GitHistoryRemovalStep.ExpireReflogs && result.Steps[^1].Step == GitHistoryRemovalStep.GarbageCollect
            && result.Steps.All(step => step.Result is { Success: true } && step.Success), "The typed sequence retains every actual successful native result.");
        foreach (GitHistoryRemovalStepResult step in result.Steps.Where(step => step.Step == GitHistoryRemovalStep.DeleteBackupReference))
        { Check(step.Arguments.Count == 4 && step.Arguments[1] == "-d" && step.Arguments[2].StartsWith("refs/original/", StringComparison.Ordinal), "Each backup is deleted with an expected object ID."); }
    }
    private static async Task<string> State(GitRepository repository)
    {
        string index = System.IO.Path.Combine(repository.Path, ".git", "index");
        return (await Git(repository, ["show-ref"])).StandardOutput + "\n" + (await Git(repository, ["reflog", "show", "--all"])).StandardOutput
            + "\n" + (File.Exists(index) ? Convert.ToHexString(File.ReadAllBytes(index)) : "") + "\n"
            + Convert.ToHexString(File.ReadAllBytes(System.IO.Path.Combine(repository.Path, "seed.txt")));
    }
    private static async Task<GitResult> Git(GitRepository repository, IReadOnlyList<string> arguments, bool requireSuccess = true)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, ["--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", .. arguments]);
        if (requireSuccess) { Check(result.Success, result.StandardError); } return result;
    }
    private const string CommandSelector = "history_command=\nfor history_arg in \"$@\"; do\ncase \"$history_arg\" in filter-branch|for-each-ref|update-ref|reflog|gc|rm|rev-parse) history_command=$history_arg; break;; esac\ndone\n";
    private static string Trace(string path) => CommandSelector + "printf '%s\\n' \"$history_command\" >> " + Quote(path) + "\n";
    private static string Wrapper(RepositoryFixture fixture, string name, string script)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This wrapper requires native Linux process semantics."); }
        string path = System.IO.Path.Combine(fixture.Root, name); File.WriteAllText(path, "#!/bin/sh\n" + script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
    }
    private static string GitExecutable() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
        .Select(directory => System.IO.Path.Combine(directory, "git")).First(path => File.Exists(path));
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void RequireLinux() { if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This scenario requires native Linux process semantics."); } }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
