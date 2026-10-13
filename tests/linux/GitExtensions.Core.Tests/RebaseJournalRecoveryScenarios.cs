using System.Text.Json;
using System.Text.Json.Nodes;
using GitExtensions.Linux;

internal static partial class RebaseAdvancedScenarios
{
    private static Task InvalidNativeMergeJournalsAsync() => InvalidRebaseJournalsAsync("merge", false);
    private static Task InvalidNativeApplyJournalsAsync() => InvalidRebaseJournalsAsync("apply", false);
    private static Task InvalidLegacyJournalsAsync() => InvalidRebaseJournalsAsync("merge", true);

    private static readonly string[] InvalidJournalCases = ["missing-state", "truncated-state", "unknown-codec", "utf16-codec",
        "foreign-worktree", "missing-repository", "null-list", "unknown-file", "file-link", "directory-link", "dangling-link",
        "state-link", "journal-file", "journal-directory-link", "journal-dangling-link"];

    private static async Task InvalidRebaseJournalsAsync(string backend, bool legacy)
    {
        foreach (string damage in InvalidJournalCases)
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string[] original = await ConflictTopic(f);
            f.Write("untracked [journal].dat", "kept\0bytes\n");
            string originalHead = await f.Repository.ResolveRevisionAsync("HEAD"), originalIndex = await Text(f.Repository, "ls-files", "--stage"),
                originalTree = await Text(f.Repository, "write-tree"), originalRefs = await Text(f.Repository, "for-each-ref");
            if (legacy)
            {
                GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
                await Expect<GitCommandException>(() => f.Repository.InteractiveRebaseAsync(plan, Environment.ProcessPath!));
            }
            else
            {
                await f.Repository.SetConfigAsync("rebase.backend", backend);
                await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            }
            string journal = f.File(legacy ? ".git/gitextensions-rebase" : ".git/gitextensions-rebase-native");
            await DamageRebaseJournalAsync(f, journal, legacy, damage);
            // A damaged saved codec/session must not be replaced with this current setting.
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "unknown-current-journal-codec");
            byte[] config = File.ReadAllBytes(f.File(".git/config"));
            string before = await JournalRecoveryWitnessAsync(f), journalBefore = JournalEntryWitness(journal),
                outsideBefore = JournalEntryWitness(System.IO.Path.Combine(f.Root, "outside-journal"));
            GitRepository reopened = new(f.Repository.Path); GitRebaseState state = await reopened.GetRebaseStateAsync();
            Check(state.IsActive && state.Backend == (backend == "merge" ? GitRebaseBackend.Merge : GitRebaseBackend.Apply)
                && state.OriginalHead == originalHead && state.HeadName == "refs/heads/topic" && state.CurrentCommit == original[0]
                && state.HasConflicts, "Invalid " + damage + " must retain the real Git rebase identity/conflicts.");
            Check(state.JournalError is { Length: > 0 } && state.EncodingError is null && state.JournalPaths.SequenceEqual(new[] { journal })
                && !state.HasRecoverableJournal && !state.CanContinue && !state.CanSkip && !state.CanCommit && !state.CanEditTodo
                && state.Options is null && state.Steps.Count == 0 && state.TodoText is null && state.TodoPath is null,
                "An invalid private journal must be explicit, without inferred options/codec/todo or continuation actions.");
            await Expect<InvalidOperationException>(() => reopened.ContinueRebaseAsync());
            await Expect<InvalidOperationException>(() => reopened.ContinueRebaseAsync(Environment.ProcessPath!));
            await Expect<InvalidOperationException>(() => reopened.SkipRebaseAsync());
            await Expect<InvalidOperationException>(() => reopened.EditRebaseTodoAsync(Environment.ProcessPath!));
            await Expect<InvalidOperationException>(async () => await reopened.PreserveOrphanedRebaseJournalsAsync());
            Check(await JournalRecoveryWitnessAsync(f) == before, "Reading/refusing a damaged active journal must not mutate any preimage or external target.");
            GitResult aborted = await reopened.AbortRebaseAsync();
            Check(aborted.Success && aborted.Warnings.Count > 0 && aborted.Warnings.Any(warning => warning.Contains(journal, StringComparison.Ordinal)),
                "Successful Git Abort must remain successful with a distinct journal-preservation warning.");
            Check(await f.Repository.ResolveRevisionAsync("HEAD") == originalHead && await Text(f.Repository, "ls-files", "--stage") == originalIndex
                && await Text(f.Repository, "write-tree") == originalTree && await Text(f.Repository, "for-each-ref") == originalRefs
                && await Text(f.Repository, "branch", "--show-current") == "topic" && File.ReadAllText(f.File("conflict1.txt")) == "topic\n"
                && File.ReadAllText(f.File("untracked [journal].dat")) == "kept\0bytes\n" && File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config),
                "Abort must restore the original Git checkout/index/refs and preserve untracked/config bytes.");
            Check(JournalEntryWitness(journal) == journalBefore && JournalEntryWitness(System.IO.Path.Combine(f.Root, "outside-journal")) == outsideBefore,
                "Abort must preserve the damaged journal entry and all link targets exactly.");
            state = await reopened.GetRebaseStateAsync();
            Check(!state.IsActive && state.HasRecoverableJournal && state.JournalError is { Length: > 0 }, "The orphan must remain explicitly recoverable after Abort.");
            before = await JournalRecoveryWitnessAsync(f);
            await Expect<IOException>(() => reopened.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            Check(await JournalRecoveryWitnessAsync(f) == before, "Starting another operation must never silently remove invalid orphaned metadata.");
            IReadOnlyList<GitRebasePreservedJournal> preserved = await reopened.PreserveOrphanedRebaseJournalsAsync();
            Check(preserved.Count == 1 && preserved[0].OriginalPath == journal && System.IO.Path.GetDirectoryName(preserved[0].PreservedPath) == f.File(".git")
                && !EntryExists(journal) && JournalEntryWitness(preserved[0].PreservedPath) == journalBefore
                && JournalEntryWitness(System.IO.Path.Combine(f.Root, "outside-journal")) == outsideBefore,
                "Explicit preservation must rename the canonical entry in its own GitDirectory, including dangling/directory links, without altering bytes or targets.");
            state = await reopened.GetRebaseStateAsync();
            Check(!state.IsActive && !state.HasRecoverableJournal && state.JournalError is null && state.JournalPaths.Count == 0,
                "Preserved copies must no longer masquerade as active canonical journals.");
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "utf-8");
            await Expect<GitCommandException>(() => reopened.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            Check((await reopened.GetRebaseStateAsync()).JournalError is null, "A new real Git attempt must work after explicit journal preservation.");
            Check((await reopened.AbortRebaseAsync()).Warnings.Count == 0 && JournalEntryWitness(preserved[0].PreservedPath) == journalBefore,
                "The fresh valid attempt must clean only its own journal and keep the preserved copy unchanged.");
        }
    }

    private static async Task DamageRebaseJournalAsync(RepositoryFixture f, string journal, bool legacy, string damage)
    {
        string statePath = System.IO.Path.Combine(journal, "state.json"), outside = System.IO.Path.Combine(f.Root, "outside-journal");
        if (damage == "missing-state") { File.Delete(statePath); }
        else if (damage == "truncated-state") { File.WriteAllText(statePath, "{\"EncodingName\":"); }
        else if (damage is "unknown-codec" or "utf16-codec" or "foreign-worktree" or "missing-repository" or "null-list")
        {
            JsonObject state = JsonNode.Parse(File.ReadAllBytes(statePath))!.AsObject();
            if (damage is "unknown-codec" or "utf16-codec") { state["EncodingName"] = damage == "unknown-codec" ? "unknown-saved-journal-codec" : "utf-16"; }
            else if (damage == "null-list") { state[legacy ? "Messages" : "SkippedCommits"] = null; }
            else
            {
                string other = System.IO.Path.Combine(f.Root, "other-worktree");
                if (damage == "foreign-worktree") { await Git(f.Repository, "worktree", "add", "--detach", other, "main"); }
                state[legacy ? "DonePath" : "RepositoryPath"] = legacy ? System.IO.Path.Combine(other, "rebase-merge", "done") : other;
            }
            File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(state));
        }
        else if (damage == "unknown-file") { File.WriteAllBytes(System.IO.Path.Combine(journal, "unknown.bin"), [0, 255, 13, 10]); }
        else if (damage == "journal-file")
        { Directory.Move(journal, outside); File.WriteAllBytes(journal, [0, 255, 13, 10]); }
        else if (damage is "journal-directory-link" or "journal-dangling-link")
        {
            Directory.Move(journal, outside);
            Directory.CreateSymbolicLink(journal, damage == "journal-directory-link" ? outside : System.IO.Path.Combine(f.Root, "missing-link-target"));
        }
        else if (damage == "state-link")
        { File.Move(statePath, outside); File.CreateSymbolicLink(statePath, outside); }
        else if (damage == "directory-link")
        { Directory.CreateDirectory(outside); File.WriteAllBytes(System.IO.Path.Combine(outside, "external.bin"), [42, 0, 255]); Directory.CreateSymbolicLink(System.IO.Path.Combine(journal, "unknown-link"), outside); }
        else
        {
            File.WriteAllBytes(outside, [42, 0, 255]);
            File.CreateSymbolicLink(System.IO.Path.Combine(journal, "unknown-link"), damage == "dangling-link" ? System.IO.Path.Combine(f.Root, "missing-link-target") : outside);
        }
    }

    private static bool EntryExists(string path) => new FileInfo(path).LinkTarget is not null || File.Exists(path) || Directory.Exists(path);
    private static string JournalEntryWitness(string path, bool worktree = false)
    {
        List<string> entries = [];
        void Read(string item, string relative)
        {
            string? link = new FileInfo(item).LinkTarget;
            if (link is not null) { entries.Add(relative + ":link:" + link); return; }
            if (Directory.Exists(item))
            {
                entries.Add(relative + ":directory:" + (OperatingSystem.IsWindows() ? "" : File.GetUnixFileMode(item).ToString()));
                foreach (string child in Directory.GetFileSystemEntries(item).Order(StringComparer.Ordinal))
                { if (!(worktree && relative == "." && System.IO.Path.GetFileName(child) == ".git")) { Read(child, System.IO.Path.GetRelativePath(path, child)); } }
            }
            else if (File.Exists(item)) { entries.Add(relative + ":file:" + (OperatingSystem.IsWindows() ? "" : File.GetUnixFileMode(item).ToString()) + ":" + Convert.ToHexString(File.ReadAllBytes(item))); }
            else { entries.Add(relative + ":absent"); }
        }
        Read(path, "."); return string.Join("\n", entries);
    }

    private static async Task<string> JournalRecoveryWitnessAsync(RepositoryFixture f)
        => await Text(f.Repository, "rev-parse", "HEAD") + "\n" + await Text(f.Repository, "for-each-ref") + "\n"
            + Convert.ToHexString(File.ReadAllBytes(f.File(".git/index"))) + "\n" + Convert.ToHexString(File.ReadAllBytes(f.File(".git/config")))
            + "\n" + JournalEntryWitness(f.Repository.Path, worktree: true) + "\n" + JournalEntryWitness(f.File(".git/rebase-merge"))
            + "\n" + JournalEntryWitness(f.File(".git/rebase-apply")) + "\n" + JournalEntryWitness(f.File(".git/gitextensions-rebase-native"))
            + "\n" + JournalEntryWitness(f.File(".git/gitextensions-rebase"));

    private static async Task AmbiguousRebaseJournalsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await ConflictTopic(f);
        await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
        string legacy = f.File(".git/gitextensions-rebase"); Directory.CreateDirectory(legacy);
        File.WriteAllBytes(System.IO.Path.Combine(legacy, "state.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { EditorExecutable = Environment.ProcessPath!, DonePath = f.File(".git/rebase-merge/done"), Messages = new Dictionary<string, string>(), EncodingName = "utf-8" }));
        string before = await JournalRecoveryWitnessAsync(f); GitRebaseState state = await f.Repository.GetRebaseStateAsync();
        Check(state.JournalError is { Length: > 0 } && state.JournalPaths.Count == 2 && state.Options is null && !state.CanContinue,
            "Two valid but competing native/legacy identities must not select a session implicitly.");
        await Expect<InvalidOperationException>(() => f.Repository.ContinueRebaseAsync());
        Check(await JournalRecoveryWitnessAsync(f) == before, "Ambiguous sessions must remain read-only.");
        string nativeBefore = JournalEntryWitness(f.File(".git/gitextensions-rebase-native")), legacyBefore = JournalEntryWitness(legacy);
        Check((await f.Repository.AbortRebaseAsync()).Warnings.Count > 0, "Git Abort must succeed while retaining both ambiguous journals.");
        IReadOnlyList<GitRebasePreservedJournal> preserved = await f.Repository.PreserveOrphanedRebaseJournalsAsync();
        Check(preserved.Count == 2 && JournalEntryWitness(preserved.Single(entry => entry.OriginalPath == legacy).PreservedPath) == legacyBefore
            && JournalEntryWitness(preserved.Single(entry => entry.OriginalPath.EndsWith("gitextensions-rebase-native", StringComparison.Ordinal)).PreservedPath) == nativeBefore,
            "Explicit recovery must retain both identities and their exact independent contents.");
    }

    private static async Task DamagedTerminalAbortJournalsAsync()
    {
        foreach (string backend in new[] { "merge", "apply", "legacy" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await ConflictTopic(f);
            string journal = f.File(backend == "legacy" ? ".git/gitextensions-rebase" : ".git/gitextensions-rebase-native");
            if (backend == "legacy")
            {
                GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
                await Expect<GitCommandException>(() => f.Repository.InteractiveRebaseAsync(plan, Environment.ProcessPath!));
            }
            else
            {
                await f.Repository.SetConfigAsync("rebase.backend", backend);
                await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            }
            await Git(f.Repository, "rebase", "--abort");
            File.WriteAllText(System.IO.Path.Combine(journal, "state.json"), "{");
            string before = await JournalRecoveryWitnessAsync(f), journalBefore = JournalEntryWitness(journal);
            GitRepository reopened = new(f.Repository.Path); GitRebaseState state = await reopened.GetRebaseStateAsync();
            Check(!state.IsActive && state.HasRecoverableJournal && state.JournalPaths.SequenceEqual(new[] { journal }) && state.Options is null,
                "A terminal Abort followed by damaged private metadata must expose a genuine recoverable orphan.");
            await Expect<InvalidOperationException>(() => reopened.AbortRebaseAsync());
            using (CancellationTokenSource cancelled = new())
            {
                cancelled.Cancel();
                await Expect<OperationCanceledException>(async () => await reopened.PreserveOrphanedRebaseJournalsAsync(cancelled.Token));
            }
            Check(await JournalRecoveryWitnessAsync(f) == before, "Absent-backend Abort and cancelled preservation must leave every preimage unchanged.");
            IReadOnlyList<GitRebasePreservedJournal> preserved = await reopened.PreserveOrphanedRebaseJournalsAsync();
            Check(preserved.Count == 1 && JournalEntryWitness(preserved[0].PreservedPath) == journalBefore
                && (await reopened.PreserveOrphanedRebaseJournalsAsync()).Count == 0,
                "Explicit preservation must retain the terminal journal exactly and return no invented paths on a repeated request.");
            await Expect<GitCommandException>(() => reopened.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            await reopened.AbortRebaseAsync();
            Check(JournalEntryWitness(preserved[0].PreservedPath) == journalBefore, "A fresh rebase after terminal-orphan recovery must preserve the retained diagnostic copy.");
        }
    }

    private static async Task LinkedWorktreeJournalPreservationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await ConflictTopic(f);
        string worktree = System.IO.Path.Combine(f.Root, "linked-journal-worktree");
        await Git(f.Repository, "worktree", "add", "-b", "independent-journal", worktree, "topic");
        GitRepository linked = new(worktree); GitRepositoryInfo info = await linked.GetInfoAsync();
        string rootBefore = await JournalRecoveryWitnessAsync(f);
        await Expect<GitCommandException>(() => linked.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
        string journal = System.IO.Path.Combine(info.GitDirectory, "gitextensions-rebase-native");
        await Git(linked, "rebase", "--abort"); File.WriteAllText(System.IO.Path.Combine(journal, "state.json"), "{");
        string before = JournalEntryWitness(journal);
        Check((await linked.GetRebaseStateAsync()).HasRecoverableJournal && !(await f.Repository.GetRebaseStateAsync()).HasRecoverableJournal,
            "Only the worktree owning the canonical journal may report its damaged orphan.");
        IReadOnlyList<GitRebasePreservedJournal> preserved = await linked.PreserveOrphanedRebaseJournalsAsync();
        Check(preserved.Count == 1 && System.IO.Path.GetDirectoryName(preserved[0].PreservedPath) == info.GitDirectory
            && JournalEntryWitness(preserved[0].PreservedPath) == before && await JournalRecoveryWitnessAsync(f) == rootBefore,
            "Preservation must use the actual linked GitDirectory and leave the primary worktree's refs/index/files unchanged.");
        await Expect<GitCommandException>(() => linked.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
        Check((await linked.GetRebaseStateAsync()).JournalError is null && (await linked.AbortRebaseAsync()).Warnings.Count == 0,
            "The linked worktree must be able to start and abort a fresh valid rebase independently.");
    }
}
