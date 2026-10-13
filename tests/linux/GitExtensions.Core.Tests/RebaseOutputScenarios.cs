using System.Text;
using System.Text.Json;
using GitExtensions.Linux;

internal static partial class RebaseAdvancedScenarios
{
    private static readonly string[] EncodedConflictPaths = ["conflict café λ1.txt", "conflict café λ2.txt", "conflict café λ3.txt"];
    private static readonly string[] EncodedConflictSubjects = ["Café € à rejouer 1", "Après € à rejouer 2", "Troisième € à rejouer 3"];

    private static async Task<string[]> EncodedConflictTopicAsync(RepositoryFixture f, string? firstSubject = null)
    {
        await f.Repository.SetConfigAsync("i18n.commitEncoding", "windows-1252");
        foreach (string path in EncodedConflictPaths) { f.Write(path, "base\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Conflict base");
        await Git(f.Repository, "switch", "-c", "topic"); List<string> hashes = [];
        for (int index = 0; index < EncodedConflictPaths.Length; index++)
        { hashes.Add(await Commit(f, EncodedConflictPaths[index], "topic\n", index == 0 && firstSubject is not null ? firstSubject : EncodedConflictSubjects[index])); }
        await Git(f.Repository, "switch", "main");
        foreach (string path in EncodedConflictPaths) { f.Write(path, "upstream\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Upstream conflicts"); await Git(f.Repository, "switch", "topic");
        return hashes.ToArray();
    }

    private static Encoding Cp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static async Task<GitCommandException> RebaseOutputFailureAsync(Func<Task<GitResult>> action)
    {
        try { await action(); }
        catch (GitCommandException error) { return error; }
        throw new InvalidOperationException("The real Git rebase must stop at its expected conflict.");
    }

    private static string DiagnosticTail(string line) => line[(line.IndexOf("... ", StringComparison.Ordinal) + 4)..];
    private static byte[] RawDiagnosticTail(byte[] bytes, string prefix)
        => Encoding.Latin1.GetBytes(DiagnosticTail(Encoding.Latin1.GetString(bytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith(prefix, StringComparison.Ordinal))));

    private static void CheckEncodedConflictOutput(GitCommandException error, string subject, string path, bool merge, bool nativeTodo = true)
    {
        GitResult result = error.Result;
        string text = result.StandardOutput + result.StandardError;
        Check(!result.Success && text.Contains(subject, StringComparison.Ordinal) && (!merge || error.Message.Contains(subject, StringComparison.Ordinal))
            && !text.Contains('\uFFFD') && result.Warnings.Count == 0,
            "Known subject fields must display the exact declared CP1252 text without replacement, UTF8 guessing or unrelated warnings.");
        Check(text.Contains(path, StringComparison.Ordinal), "Git's UTF8 file paths must remain exact in the same mixed output.");
        byte[] raw = [.. result.StandardOutputBytes, .. result.StandardErrorBytes];
        Check(raw.AsSpan().IndexOf(Cp1252().GetBytes(subject)) >= 0 && raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes(path)) >= 0,
            "Both raw pipes must retain the genuine CP1252 subject and UTF8 path bytes.");
        if (merge)
        {
            string[] lines = result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string objectSubject = DiagnosticTail(lines.Single(line => line.StartsWith("error: could not apply ", StringComparison.Ordinal)));
            string todoSubject = DiagnosticTail(lines.Single(line => line.StartsWith("Could not apply ", StringComparison.Ordinal)));
            Check(objectSubject == subject && (todoSubject == subject || todoSubject == "# " + subject),
                "Each independently emitted Merge field must retain its actual subject; counting substrings elsewhere cannot prove this.");
            Check(RawDiagnosticTail(result.StandardErrorBytes, "error: could not apply ").SequenceEqual(Cp1252().GetBytes(objectSubject))
                && RawDiagnosticTail(result.StandardErrorBytes, "Could not apply ").SequenceEqual((nativeTodo ? Encoding.UTF8 : Cp1252()).GetBytes(todoSubject)),
                "Raw object diagnostics must be CP1252, while actual todo diagnostics must use their separately declared native UTF8 or legacy CP1252 codec.");
        }
        else
        {
            Check(result.StandardOutput.Contains("Applying: " + subject, StringComparison.Ordinal)
                && result.StandardOutput.Contains("Patch failed at ", StringComparison.Ordinal),
                "The apply backend's stdout subject fields must use the same explicit commit codec.");
        }
    }

    private static void CheckNativeInstructionSubjects(RepositoryFixture f, params string[] subjects)
    {
        byte[] todo = File.ReadAllBytes(f.File(".git/rebase-merge/git-rebase-todo")), done = File.ReadAllBytes(f.File(".git/rebase-merge/done"));
        byte[] instructions = [.. done, .. todo];
        string text = new UTF8Encoding(false, true).GetString(instructions);
        foreach (string subject in subjects)
        { Check(text.Contains(subject, StringComparison.Ordinal), "Native todo/done must preserve the actual subject in strictly declared UTF8, including ambiguous CP1252 source bytes."); }
    }

    private static async Task NativeMixedRebaseOutputAsync()
    {
        foreach (string backend in new[] { "merge", "apply" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string[] hashes = await EncodedConflictTopicAsync(f);
            await f.Repository.SetConfigAsync("rebase.backend", backend); byte[] config = File.ReadAllBytes(f.File(".git/config"));
            GitCommandException first = await RebaseOutputFailureAsync(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            CheckEncodedConflictOutput(first, EncodedConflictSubjects[0], EncodedConflictPaths[0], backend == "merge");
            if (backend == "merge")
            {
                CheckNativeInstructionSubjects(f, EncodedConflictSubjects);
                Check((await f.Repository.GetRebaseStateAsync()).TodoEncodingName == "utf-8",
                    "The native journal and state must declare the todo codec independently of CP1252 commit messages.");
                string todo = f.File(".git/rebase-merge/git-rebase-todo"); byte[] before = File.ReadAllBytes(todo);
                Check((await f.Repository.EditRebaseTodoAsync()).Success && File.ReadAllBytes(todo).SequenceEqual(before),
                    "The output decoder must not change native todo/editor bytes when Edit todo uses the existing true editor.");
            }
            await f.Repository.ResolveConflictAsync(EncodedConflictPaths[0], GitConflictResolution.Theirs);
            GitCommandException continued = await RebaseOutputFailureAsync(() => f.Repository.ContinueRebaseAsync());
            CheckEncodedConflictOutput(continued, EncodedConflictSubjects[1], EncodedConflictPaths[1], backend == "merge");
            if (backend == "merge")
            {
                CheckNativeInstructionSubjects(f, EncodedConflictSubjects);
                Check(continued.Result.StandardOutput.Contains(EncodedConflictSubjects[0], StringComparison.Ordinal)
                    && continued.Result.StandardOutputBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(EncodedConflictSubjects[0])) >= 0,
                    "Native commit summaries use pinned UTF8 log output, independently of their CP1252 commit objects.");
            }
            GitCommandException skipped = await RebaseOutputFailureAsync(() => f.Repository.SkipRebaseAsync());
            CheckEncodedConflictOutput(skipped, EncodedConflictSubjects[2], EncodedConflictPaths[2], backend == "merge");
            GitRebaseState state = await f.Repository.GetRebaseStateAsync();
            Check(state.CurrentCommit == hashes[2] && state.Steps.Any(patch => patch.CommitHash == hashes[1] && patch.Status == GitRebaseStepStatus.Skipped),
                "Decoding must preserve real Continue/Skip semantics and the saved skipped patch.");
            await f.Repository.AbortRebaseAsync();
            Check(File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config), "Per-command codec pins must not rewrite Git configuration.");
        }
    }

    private static async Task AmbiguousCp1252RebaseOutputAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await EncodedConflictTopicAsync(f, "Ã©");
        GitCommandException error = await RebaseOutputFailureAsync(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
        CheckEncodedConflictOutput(error, "Ã©", EncodedConflictPaths[0], merge: true);
        CheckNativeInstructionSubjects(f, "Ã©", EncodedConflictSubjects[1], EncodedConflictSubjects[2]);
        Check(error.Result.StandardErrorBytes.AsSpan().IndexOf(new byte[] { 0xc3, 0xa9 }) >= 0,
            "The real CP1252 subject must deliberately have bytes that are also a valid UTF8 sequence.");
        await f.Repository.AbortRebaseAsync();
    }

    private static string WriteOutputHook(RepositoryFixture f, bool refuse)
    {
        string directory = System.IO.Path.Combine(f.Root, "hooks-café-λ"); Directory.CreateDirectory(directory);
        string hook = System.IO.Path.Combine(directory, "pre-rebase");
        File.WriteAllText(hook, "#!/bin/sh\nprintf '%s\\n' 'Échec externe café λ / chemin UTF8' '[hook not-a-hash] café λ' 'error: could not apply zzzz... café λ' >&2\nprintf 'outside-byte: \\377\\n' >&2\nexit " + (refuse ? "1" : "0") + "\n", new UTF8Encoding(false, true));
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        return directory;
    }

    private static async Task ExternalUtf8RebaseOutputAsync()
    {
        foreach (bool refuse in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f);
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "windows-1252");
            await Git(f.Repository, "branch", "-m", "branche-café-λ");
            await f.Repository.SetConfigAsync("core.hooksPath", WriteOutputHook(f, refuse));
            GitResult result;
            if (refuse) { result = (await RebaseOutputFailureAsync(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }))).Result; }
            else { result = await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }); }
            Check(result.Success == !refuse && result.StandardError.Contains("Échec externe café λ / chemin UTF8", StringComparison.Ordinal)
                && result.StandardError.Contains("[hook not-a-hash] café λ", StringComparison.Ordinal)
                && result.StandardError.Contains("error: could not apply zzzz... café λ", StringComparison.Ordinal)
                && result.StandardError.Contains("outside-byte: \\xFF", StringComparison.Ordinal) && result.Warnings.Count > 0,
                "Unidentified external/invalid-format text must stay UTF8; invalid bytes must be explicit warnings without changing Git's actual success or failure.");
            Check(result.StandardErrorBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Échec externe café λ / chemin UTF8")) >= 0
                && result.StandardErrorBytes.Contains((byte)255), "Raw stderr must retain both the hook's UTF8 text and its undecodable byte.");
            Check((await f.Repository.GetReferencesAsync()).Any(reference => reference.Name == "refs/heads/branche-café-λ"),
                "Administrative UTF8 ref reads must not inherit the Rebase subject decoder.");
            GitResult defaultOutput = await new GitRunner().RunAsync(f.Repository.Path, ["for-each-ref", "--format=%(refname)"]);
            Check(defaultOutput.StandardOutput.Contains("refs/heads/branche-café-λ", StringComparison.Ordinal) && defaultOutput.Warnings.Count == 0
                && defaultOutput.StandardOutputBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("refs/heads/branche-café-λ")) >= 0,
                "The default runner must preserve normal administrative UTF8 text and raw stdout exactly.");
        }
    }

    private static async Task LegacyMixedRebaseOutputAsync()
    {
        foreach (bool explicitEditor in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await EncodedConflictTopicAsync(f);
            GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
            GitCommandException first = await RebaseOutputFailureAsync(() => f.Repository.InteractiveRebaseAsync(plan, Environment.ProcessPath!));
            CheckEncodedConflictOutput(first, EncodedConflictSubjects[0], EncodedConflictPaths[0], merge: true, nativeTodo: false);
            await f.Repository.ResolveConflictAsync(EncodedConflictPaths[0], GitConflictResolution.Theirs);
            GitCommandException continued = await RebaseOutputFailureAsync(() => explicitEditor
                ? f.Repository.ContinueRebaseAsync(Environment.ProcessPath!) : f.Repository.ContinueRebaseAsync());
            CheckEncodedConflictOutput(continued, EncodedConflictSubjects[1], EncodedConflictPaths[1], merge: true, nativeTodo: false);
            GitCommandException skipped = await RebaseOutputFailureAsync(() => explicitEditor
                ? f.Repository.SkipRebaseAsync(Environment.ProcessPath!) : f.Repository.SkipRebaseAsync());
            CheckEncodedConflictOutput(skipped, EncodedConflictSubjects[2], EncodedConflictPaths[2], merge: true, nativeTodo: false);
            await f.Repository.AbortRebaseAsync();
        }
    }

    private static async Task AbortOutputWithoutCodecAsync()
    {
        foreach (string backend in new[] { "merge", "apply", "legacy" })
        foreach (string invalidCodec in new[] { "unknown-abort-output-codec", "utf-16" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await EncodedConflictTopicAsync(f);
            string head = await f.Repository.ResolveRevisionAsync("HEAD"), index = await Text(f.Repository, "ls-files", "--stage"), refs = await Text(f.Repository, "for-each-ref");
            if (backend == "legacy")
            {
                GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
                await RebaseOutputFailureAsync(() => f.Repository.InteractiveRebaseAsync(plan, Environment.ProcessPath!));
            }
            else
            {
                await f.Repository.SetConfigAsync("rebase.backend", backend);
                await RebaseOutputFailureAsync(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            }
            await f.Repository.SetConfigAsync("i18n.commitEncoding", invalidCodec);
            string journal = f.File(backend == "legacy" ? ".git/gitextensions-rebase" : ".git/gitextensions-rebase-native");
            File.WriteAllText(System.IO.Path.Combine(journal, "state.json"), "{");
            string journalBefore = JournalEntryWitness(journal); byte[] config = File.ReadAllBytes(f.File(".git/config"));
            string wrapper = System.IO.Path.Combine(f.Root, "git-with-abort-output");
            // The owned wrapper emits one deliberately invalid UTF8 byte, then executes real Git.
            // This checks warning merging; it does not claim Git itself generated that byte.
            File.WriteAllText(wrapper, "#!/bin/sh\nfor argument in \"$@\"; do\n if [ \"$argument\" = --abort ]; then printf '\\377\\n' >&2; fi\ndone\nexec git \"$@\"\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            GitRepository reopened = new(f.Repository.Path, new GitRunner(wrapper));
            GitResult aborted = await reopened.AbortRebaseAsync();
            Check(aborted.Success && aborted.StandardError == "\\xFF\n" && aborted.StandardErrorBytes.SequenceEqual(new byte[] { 255, 10 })
                && aborted.Warnings.Any(warning => warning.Contains("stderr", StringComparison.Ordinal))
                && aborted.Warnings.Any(warning => warning.Contains(journal, StringComparison.Ordinal)),
                "Codec-independent real Git Abort must merge raw-output and journal-preservation warnings without replacing success.");
            Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && await Text(f.Repository, "ls-files", "--stage") == index
                && await Text(f.Repository, "for-each-ref") == refs && File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config)
                && JournalEntryWitness(journal) == journalBefore && (await reopened.GetRebaseStateAsync()).HasRecoverableJournal,
                "Abort must restore the real checkout/index/refs while preserving invalid config and damaged journal bytes.");
        }
    }

    private static async Task ValidEncodingChangeRebaseOutputAsync()
    {
        foreach (bool legacy in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await EncodedConflictTopicAsync(f);
            if (legacy)
            {
                GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
                await RebaseOutputFailureAsync(() => f.Repository.InteractiveRebaseAsync(plan, Environment.ProcessPath!));
            }
            else { await RebaseOutputFailureAsync(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" })); }
            await f.Repository.ResolveConflictAsync(EncodedConflictPaths[0], GitConflictResolution.Theirs);
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "utf-8");
            string before = await JournalRecoveryWitnessAsync(f);
            await Expect<InvalidOperationException>(() => f.Repository.ContinueRebaseAsync());
            Check(await JournalRecoveryWitnessAsync(f) == before, "Even a valid changed encoding must be rejected before journal/editor/Git mutation.");
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "windows-1252");
            byte[] config = File.ReadAllBytes(f.File(".git/config"));
            GitCommandException continued = await RebaseOutputFailureAsync(() => f.Repository.ContinueRebaseAsync());
            CheckEncodedConflictOutput(continued, EncodedConflictSubjects[1], EncodedConflictPaths[1], merge: true, nativeTodo: !legacy);
            GitBinaryResult commit = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["cat-file", "commit", "HEAD"]);
            Check(commit.Success && commit.StandardOutput.AsSpan().IndexOf("encoding windows-1252\n"u8) >= 0
                && commit.StandardOutput.AsSpan().IndexOf(Cp1252().GetBytes(EncodedConflictSubjects[0])) >= 0
                && File.ReadAllBytes(f.File(".git/config")).SequenceEqual(config),
                "After restoring the validated codec, Continue must create a CP1252 object and keep configuration bytes unchanged.");
            await f.Repository.AbortRebaseAsync();
        }
    }

    private static async Task NativeTodoCodecCompatibilityAsync()
    {
        foreach (string codec in new[] { "windows-1252", "utf-8" })
        foreach (string backend in new[] { "merge", "apply" })
        foreach (bool oldJournal in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync();
            string[] hashes = await EncodedConflictTopicAsync(f, "Ã©");
            await f.Repository.SetConfigAsync("i18n.commitEncoding", codec);
            string head = await f.Repository.ResolveRevisionAsync("HEAD"), index = await Text(f.Repository, "ls-files", "--stage"), refs = await Text(f.Repository, "for-each-ref");
            GitResult start = await new GitRunner().RunAsync(f.Repository.Path,
                ["-c", "i18n.commitEncoding=" + codec, "-c", "i18n.logOutputEncoding=" + codec, "rebase", "--" + backend, "main"]);
            Check(!start.Success, "The real previous/native or external Git session must stop at its genuine conflict.");
            string journal = f.File(".git/gitextensions-rebase-native");
            if (oldJournal)
            {
                GitRepositoryInfo info = await f.Repository.GetInfoAsync(); Directory.CreateDirectory(journal);
                if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(journal, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                // Exact previous schema: commit codec present, todo codec deliberately absent.
                await File.WriteAllBytesAsync(System.IO.Path.Combine(journal, "state.json"), JsonSerializer.SerializeToUtf8Bytes(new
                { RepositoryPath = info.RootPath, info.GitDirectory, EditorExecutable = "", EncodingName = codec, SkippedCommits = Array.Empty<string>() }));
            }
            string before = await JournalRecoveryWitnessAsync(f), journalBefore = JournalEntryWitness(journal);
            GitRepository reopened = new(f.Repository.Path); GitRebaseState state = await reopened.GetRebaseStateAsync();
            bool minimal = codec != "utf-8" && backend == "merge";
            Check(state.IsActive && state.CurrentCommit == hashes[0] && state.OriginalHead == head && state.HasConflicts,
                "Compatibility recovery must retain actual backend/HEAD/conflicts, regardless of unknown todo provenance.");
            if (minimal)
            {
                Check(state.Steps.Count == 0 && state.TodoText is null && state.TodoEncodingName is null && !state.CanSkip && !state.CanEditTodo
                    && (oldJournal ? state.JournalError is not null && state.EncodingError is null : state.EncodingError is not null && state.JournalError is null),
                    "Undeclared nonUTF8 Merge must remain minimal; the old journal or external-codec diagnostic must identify the actual missing contract.");
                if (oldJournal)
                {
                    await Expect<InvalidOperationException>(() => reopened.ContinueRebaseAsync());
                    await Expect<InvalidOperationException>(() => reopened.SkipRebaseAsync());
                    await Expect<InvalidOperationException>(() => reopened.EditRebaseTodoAsync());
                }
                else
                {
                    await Expect<NotSupportedException>(() => reopened.ContinueRebaseAsync());
                    await Expect<NotSupportedException>(() => reopened.SkipRebaseAsync());
                    await Expect<NotSupportedException>(() => reopened.EditRebaseTodoAsync());
                }
            }
            else
            {
                Check(state.Steps.Count >= 3 && state.EncodingError is null && state.JournalError is null && state.CanSkip
                    && (backend == "apply" || state.TodoEncodingName == "utf-8" && state.CanEditTodo),
                    "Old UTF8 journals, strict external UTF8 convention and Apply without a todo must retain normal readable recovery.");
            }
            Check(await JournalRecoveryWitnessAsync(f) == before, "All compatibility reads/refusals must preserve journal, Git backend, config, worktree, index and refs exactly.");
            GitResult aborted = await reopened.AbortRebaseAsync();
            Check(aborted.Success && await f.Repository.ResolveRevisionAsync("HEAD") == head && await Text(f.Repository, "ls-files", "--stage") == index
                && await Text(f.Repository, "for-each-ref") == refs, "Compatibility Abort must restore Git's real checkout/index/refs independently of codec provenance.");
            if (oldJournal && codec != "utf-8")
            {
                Check(aborted.Warnings.Count > 0 && JournalEntryWitness(journal) == journalBefore && (await reopened.GetRebaseStateAsync()).HasRecoverableJournal,
                    "Without a remaining backend, keep the undeclared old nonUTF8 journal with its explicit diagnostic; do not infer that it formerly used Apply.");
                IReadOnlyList<GitRebasePreservedJournal> preserved = await reopened.PreserveOrphanedRebaseJournalsAsync();
                Check(preserved.Count == 1 && JournalEntryWitness(preserved[0].PreservedPath) == journalBefore,
                    "Explicit orphan preservation must retain every old-schema journal byte before another operation is attempted.");
            }
            else { Check(aborted.Warnings.Count == 0 && !Directory.Exists(journal), "Known or absent journal metadata must retain normal Abort cleanup."); }
            await RebaseOutputFailureAsync(() => reopened.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            Check((await reopened.GetRebaseStateAsync()).JournalError is null, "A fresh native session must declare its own todo codec after explicit old-journal recovery.");
            await reopened.AbortRebaseAsync();
        }
    }
}
