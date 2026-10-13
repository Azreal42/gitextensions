using System.Text;
using GitExtensions.Linux;

internal static partial class RebaseAdvancedScenarios
{
    private static Task UnreadableNativeTodoAsync() => UnreadableNativeInstructionsAsync("git-rebase-todo", "unknown-native-instruction-current-codec", resolved: false);
    private static Task UnreadableNativeDoneAsync() => UnreadableNativeInstructionsAsync("done", "utf-16", resolved: true);

    private static async Task UnreadableNativeInstructionsAsync(string filename, string currentCodec, bool resolved)
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        string[] commits = await EncodedConflictTopicAsync(f);
        f.Write("untracked [instructions].dat", "kept\0bytes\n");
        await Git(f.Repository, "branch", "independent", "main"); await Git(f.Repository, "tag", "instruction-base", "main");
        string head = await f.Repository.ResolveRevisionAsync("HEAD"), index = await Text(f.Repository, "ls-files", "--stage"), refs = await Text(f.Repository, "for-each-ref");
        string worktree = JournalEntryWitness(f.Repository.Path, worktree: true);
        await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
        if (resolved) { await f.Repository.ResolveConflictAsync(EncodedConflictPaths[0], GitConflictResolution.Theirs); }
        GitRepository reopened = new(f.Repository.Path);
        GitRebaseState readable = await reopened.GetRebaseStateAsync();
        Check(readable.Steps.Count >= 3 && readable.InstructionError is null && readable.EncodingName == "windows-1252"
            && readable.TodoEncodingName == "utf-8" && readable.CanCommit && readable.CanContinue == resolved,
            "The real CP1252 session must initially expose its independently declared UTF8 todo and valid recovery.");
        string path = f.File(".git/rebase-merge/" + filename);
        byte[] original = File.ReadAllBytes(path);
        _ = new UTF8Encoding(false, true).GetString(original);
        // A CP1252 comment is deliberately not valid UTF8. Trying the saved message
        // codec would appear to succeed, but would change Git's declared todo meaning.
        await File.WriteAllBytesAsync(path, [.. original, (byte)'\n', (byte)'#', (byte)' ', (byte)'C', (byte)'a', (byte)'f', 0xe9, (byte)'\n']);
        await f.Repository.SetConfigAsync("i18n.commitEncoding", currentCodec);
        byte[] config = File.ReadAllBytes(f.File(".git/config"));
        string pending = await JournalRecoveryWitnessAsync(f);
        GitRebaseState state = await reopened.GetRebaseStateAsync();
        CheckMinimalInstructionState(state, filename, commits[0], head, hasConflicts: !resolved);
        await RefuseInstructionOperationsAsync(reopened);
        using (CancellationTokenSource cancelled = new())
        {
            cancelled.Cancel();
            await Expect<OperationCanceledException>(() => reopened.GetRebaseStateAsync(cancelled.Token));
        }
        Check(await JournalRecoveryWitnessAsync(f) == pending, "Read/refused recovery/cancellation must preserve every journal, instruction, raw index, config, ref and worktree byte.");
        GitResult aborted = await reopened.AbortRebaseAsync();
        Check(aborted.Success && aborted.Warnings.Count == 0, "Native Abort must not decode unreadable instructions or consult the changed codec; its valid private journal cleans normally.");
        Check(await reopened.ResolveRevisionAsync("HEAD") == head && await Text(reopened, "ls-files", "--stage") == index
            && await Text(reopened, "for-each-ref") == refs && JournalEntryWitness(f.Repository.Path, worktree: true) == worktree
            && File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config), "Abort must restore original HEAD/refs/indexed blobs/files and preserve the deliberately changed config.");
        Check(!Directory.Exists(f.File(".git/rebase-merge")) && !Directory.Exists(f.File(".git/gitextensions-rebase-native")),
            "Git's normal successful Abort may remove its backend; a valid private journal must not be mislabeled as damaged or preserved.");
        GitRebaseState terminal = await reopened.GetRebaseStateAsync();
        Check(!terminal.IsActive && terminal.InstructionError is null && terminal.JournalError is null && !terminal.HasRecoverableJournal,
            "Unreadable native files do not create an orphaned private-journal error after terminal Abort.");
    }

    private static void CheckMinimalInstructionState(GitRebaseState state, string filename, string current, string original, bool hasConflicts)
    {
        Check(state.IsActive && state.Backend == GitRebaseBackend.Merge && state.CurrentCommit == current
            && state.OriginalHead == original && state.HeadName == "refs/heads/topic" && state.HasConflicts == hasConflicts,
            "An unreadable native instruction file must retain real administrative identities and index conflicts.");
        Check(state.InstructionError is not null && state.InstructionError.Contains(filename, StringComparison.Ordinal)
            && state.EncodingError is null && state.JournalError is null && state.EncodingName == "windows-1252"
            && state.TodoEncodingName == "utf-8" && state.Options?.BranchName == "main" && state.Steps.Count == 0
            && state.TodoPath is null && state.TodoText is null && !state.CanContinue && !state.CanSkip && !state.CanCommit && !state.CanEditTodo,
            "Keep validated saved options/codecs, distinguish unreadable instructions from a broken journal, and expose no guessed/partial plan or unsafe actions.");
    }

    private static async Task RefuseInstructionOperationsAsync(GitRepository repository)
    {
        foreach (Func<Task> command in new Func<Task>[]
        {
            () => repository.ContinueRebaseAsync(), () => repository.ContinueRebaseAsync("/usr/bin/true"),
            () => repository.SkipRebaseAsync(), () => repository.SkipRebaseAsync("/usr/bin/true"),
            () => repository.EditRebaseTodoAsync(), () => repository.EditRebaseTodoAsync("/usr/bin/true")
        })
        {
            InvalidOperationException? refusal = null;
            try { await command(); } catch (InvalidOperationException error) { refusal = error; }
            Check(refusal?.Message.Contains("instruction text", StringComparison.Ordinal) == true,
                "Each native recovery overload must explicitly reject the unreadable instructions before editor/journal/Git mutation.");
        }
    }

    private static async Task NativeInstructionDirectoriesAsync()
    {
        foreach (string filename in new[] { "git-rebase-todo", "done" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string[] commits = await EncodedConflictTopicAsync(f);
            string head = await f.Repository.ResolveRevisionAsync("HEAD"), index = await Text(f.Repository, "ls-files", "--stage");
            await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            string path = f.File(".git/rebase-merge/" + filename); byte[] original = File.ReadAllBytes(path);
            File.Delete(path); Directory.CreateDirectory(path); await File.WriteAllBytesAsync(System.IO.Path.Combine(path, "kept.bin"), [0, 255, 13, 10]);
            string before = await JournalRecoveryWitnessAsync(f);
            CheckMinimalInstructionState(await f.Repository.GetRebaseStateAsync(), filename, commits[0], head, hasConflicts: true);
            await RefuseInstructionOperationsAsync(f.Repository);
            Check(await JournalRecoveryWitnessAsync(f) == before, "A directory is an actual present-but-unreadable file, not an empty/missing Git plan; reads must leave it exact.");
            // Remove this explicit filesystem fixture fault only after proving no app
            // repair occurred. The UTF8 cases separately prove Abort with damage intact.
            Directory.Delete(path, recursive: true); await File.WriteAllBytesAsync(path, original);
            Check((await f.Repository.GetRebaseStateAsync()).InstructionError is null, "Restoring exact declared instruction bytes must make the real state readable again.");
            await f.Repository.AbortRebaseAsync();
            Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && await Text(f.Repository, "ls-files", "--stage") == index,
                "A later real Abort must retain its normal restoration after the explicit filesystem fixture fault is removed.");
        }
    }

    private static async Task NativeInstructionPermissionsAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The instruction access-denial scenario requires Linux file permissions."); }
        foreach (string filename in new[] { "git-rebase-todo", "done" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string[] commits = await EncodedConflictTopicAsync(f);
            string head = await f.Repository.ResolveRevisionAsync("HEAD"), index = await Text(f.Repository, "ls-files", "--stage");
            await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            string path = f.File(".git/rebase-merge/" + filename); byte[] original = File.ReadAllBytes(path);
            using FileStream evidence = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            UnixFileMode mode = File.GetUnixFileMode(path);
            try
            {
                File.SetUnixFileMode(path, mode & ~(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
                ExpectSync<UnauthorizedAccessException>(() => File.ReadAllBytes(path));
                byte[] rawIndex = File.ReadAllBytes(f.File(".git/index")), config = File.ReadAllBytes(f.File(".git/config"));
                string backend = InstructionEntryWitness(f.File(".git/rebase-merge"), path, evidence), journal = JournalEntryWitness(f.File(".git/gitextensions-rebase-native")), refs = await Text(f.Repository, "for-each-ref");
                CheckMinimalInstructionState(await f.Repository.GetRebaseStateAsync(), filename, commits[0], head, hasConflicts: true);
                await RefuseInstructionOperationsAsync(f.Repository);
                Check(InstructionEntryWitness(f.File(".git/rebase-merge"), path, evidence) == backend
                    && JournalEntryWitness(f.File(".git/gitextensions-rebase-native")) == journal
                    && File.ReadAllBytes(f.File(".git/index")).SequenceEqual(rawIndex) && File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config)
                    && await Text(f.Repository, "for-each-ref") == refs, "Access-denial recovery must preserve original denied mode, bytes, all backend entries and Git/journal state.");
                GitResult aborted = await f.Repository.AbortRebaseAsync();
                Check(aborted.Success && aborted.Warnings.Count == 0 && !File.Exists(path)
                    && !Directory.Exists(f.File(".git/rebase-merge")) && !Directory.Exists(f.File(".git/gitextensions-rebase-native")),
                    "Git Abort must execute while the instruction file is still unreadable; its writable parent permits native cleanup without a decoder or chmod repair.");
                evidence.Position = 0; using MemoryStream retained = new(); evidence.CopyTo(retained);
                Check(retained.ToArray().SequenceEqual(original), "The pre-opened readonly evidence descriptor must retain the exact original inode bytes after Git removes its path.");
                Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && await Text(f.Repository, "ls-files", "--stage") == index
                    && await Text(f.Repository, "for-each-ref") == refs && File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config),
                    "Actual Abort under access denial must restore original HEAD/index/refs and preserve configuration.");
            }
            finally { if (File.Exists(path)) { File.SetUnixFileMode(path, mode); } }
        }
    }

    private static async Task LegacyUnreadableInstructionsAsync()
    {
        foreach (string filename in new[] { "git-rebase-todo", "done" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string[] commits = await ConflictTopic(f);
            string head = await f.Repository.ResolveRevisionAsync("HEAD"), index = await Text(f.Repository, "ls-files", "--stage"), refs = await Text(f.Repository, "for-each-ref");
            GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
            await Expect<GitCommandException>(() => f.Repository.InteractiveRebaseAsync(plan, Environment.ProcessPath!));
            GitRebaseState valid = await f.Repository.GetRebaseStateAsync();
            Check(valid.UsesLegacyPlan && valid.EncodingName == "utf-8" && valid.InstructionError is null && valid.Steps.Count >= 3,
                "The real legacy plan must start readable with a saved UTF8 codec and an actual conflict.");
            string path = f.File(".git/rebase-merge/" + filename); byte[] before = File.ReadAllBytes(path);
            await File.WriteAllBytesAsync(path, [.. before, (byte)'\n', (byte)'#', (byte)' ', 0xff, (byte)'\n']);
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "unknown-legacy-instruction-current-codec");
            string pending = await JournalRecoveryWitnessAsync(f); byte[] config = File.ReadAllBytes(f.File(".git/config"));
            GitRebaseState state = await f.Repository.GetRebaseStateAsync();
            Check(state.InstructionError?.Contains(filename, StringComparison.Ordinal) == true && state.UsesLegacyPlan
                && state.EncodingName == "utf-8" && state.CurrentCommit == commits[0] && state.HasConflicts
                && state.JournalError is null && state.Steps.Count == 0 && !state.CanContinue && !state.CanSkip && !state.CanCommit && !state.CanEditTodo,
                "Legacy native instruction corruption must project the same safe minimal state without losing its genuine saved-plan identity.");
            await RefuseInstructionOperationsAsync(f.Repository);
            Check(await JournalRecoveryWitnessAsync(f) == pending, "Legacy Continue/Skip/editor overloads must refuse before saved-codec validation, editor rewrite or Git mutation.");
            Check((await f.Repository.AbortRebaseAsync()).Warnings.Count == 0
                && await f.Repository.ResolveRevisionAsync("HEAD") == head && await Text(f.Repository, "ls-files", "--stage") == index
                && await Text(f.Repository, "for-each-ref") == refs && File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config)
                && !Directory.Exists(f.File(".git/rebase-merge")) && !Directory.Exists(f.File(".git/gitextensions-rebase")),
                "Legacy Abort must remain native and codec-independent with the real unreadable file still present until Git's normal removal.");
        }
    }

    private static string InstructionEntryWitness(string root, string deniedPath, FileStream evidence)
    {
        List<string> entries = [];
        void Read(string path)
        {
            string relative = System.IO.Path.GetRelativePath(root, path), mode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(path).ToString() : "";
            if (Directory.Exists(path))
            { entries.Add(relative + ":directory:" + mode); foreach (string child in Directory.GetFileSystemEntries(path).Order(StringComparer.Ordinal)) { Read(child); } }
            else
            {
                byte[] bytes;
                if (path == deniedPath) { evidence.Position = 0; using MemoryStream copy = new(); evidence.CopyTo(copy); bytes = copy.ToArray(); }
                else { bytes = File.ReadAllBytes(path); }
                entries.Add(relative + ":file:" + mode + ":" + Convert.ToHexString(bytes));
            }
        }
        Read(root); return string.Join("\n", entries);
    }
}
