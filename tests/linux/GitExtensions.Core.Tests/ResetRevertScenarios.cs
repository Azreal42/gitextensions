using System.Text;
using GitExtensions.Linux;

internal static class ResetRevertScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("reset five modes match native Git on literal files", Guard(FiveModesAsync)),
        ("reset soft preserves raw index and separate worktree bytes", Guard(SoftIndexAsync)),
        ("reset keep and merge reject conflicting local edits without mutation", Guard(ProtectedResetAsync)),
        ("reset other branch moves only the selected ref and suggests its tracking branch", Guard(ResetOtherAsync)),
        ("reset other branch requires force for divergence and rejects current or missing refs", Guard(ResetOtherValidationAsync)),
        ("reset other branch protects a linked worktree even when forced", Guard(ResetOtherWorktreeAsync)),
        ("reset other branch works in a bare repository without checkout", Guard(ResetBareAsync)),
        ("revert default stages reverse changes and keeps unrelated edits", Guard(RevertNoCommitAsync)),
        ("revert automatic option and legacy wrapper create commits", Guard(RevertAutomaticAsync)),
        ("revert merge validates mainline before mutation and supports either parent", Guard(RevertParentsAsync)),
        ("revert preserves encoded existing commit messages and refuses invalid draft bytes", Guard(RevertDraftAsync)),
        ("revert conflict result preserves native state and prevents a subsequent revert", Guard(RevertConflictAsync)),
        ("revert selected revisions are frozen in caller order before mutation", Guard(RevertSelectionAsync)),
        ("reset and revert pre-cancellation leave every Git state unchanged", Guard(CancelledAsync)),
        ("revert native prepare-commit-msg rejection preserves draft and reverse changes", Guard(RevertHookAsync))
    ];
    private static Func<Task> Guard(Func<Task> test) => async () =>
    {
        string? previous = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        try { await test(); } finally { Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", previous); }
    };
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task<GitResult> Git(GitRepository repository, params string[] arguments)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, arguments,
            environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0" });
        if (!result.Success) { throw new GitCommandException(arguments, result); } return result;
    }
    private static async Task<string> Text(GitRepository repository, params string[] arguments)
        => (await Git(repository, arguments)).StandardOutput.TrimEnd('\r', '\n');
    private static async Task<string> Commit(RepositoryFixture fixture, string path, string text, string message)
    { fixture.Write(path, text); await Git(fixture.Repository, "add", "--", path); await Git(fixture.Repository, "commit", "-m", message); return await Text(fixture.Repository, "rev-parse", "HEAD"); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<string> State(RepositoryFixture fixture)
    {
        string root = fixture.Repository.Path;
        string GitFile(string name) => System.IO.Path.Combine(root, ".git", name);
        string Bytes(string name) => File.Exists(GitFile(name)) ? Convert.ToHexString(File.ReadAllBytes(GitFile(name))) : "-";
        string files = string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !System.IO.Path.GetRelativePath(root, path).StartsWith(".git" + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => System.IO.Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(File.ReadAllBytes(path))));
        return await Text(fixture.Repository, "rev-parse", "HEAD") + "\n" + await Text(fixture.Repository, "for-each-ref") + "\n"
            + Bytes("index") + "\n" + Bytes("config") + "\n" + Bytes("MERGE_MSG") + "\n" + Bytes("COMMITMESSAGE") + "\n" + files;
    }
    private static async Task FiveModesAsync()
    {
        foreach (GitResetMode mode in Enum.GetValues<GitResetMode>())
        {
            using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitRepository repository = fixture.Repository;
            string initial = await Text(repository, "rev-parse", "HEAD");
            string path = "literal λ [*] '$()\nfile.txt"; string tip = await Commit(fixture, path, "new content\n", "Second");
            string oraclePath = System.IO.Path.Combine(fixture.Root, "oracle");
            await Git(repository, "clone", "--no-local", repository.Path, oraclePath); GitRepository oracle = new(oraclePath);
            await RepositoryFixture.ConfigureAsync(oracle);
            await Git(oracle, "reset", "--" + mode.ToString().ToLowerInvariant(), initial, "--");
            await repository.ResetAsync(initial, mode);
            Check(await Text(repository, "rev-parse", "HEAD") == initial, "All reset modes must move the real checked-out HEAD.");
            Check(await Text(repository, "write-tree") == await Text(oracle, "write-tree"), "The index must match native git reset " + mode);
            Check(File.Exists(fixture.File(path)) == File.Exists(System.IO.Path.Combine(oraclePath, path)), "Worktree file existence must match native Git.");
            if (File.Exists(fixture.File(path))) { Check(File.ReadAllBytes(fixture.File(path)).SequenceEqual(File.ReadAllBytes(System.IO.Path.Combine(oraclePath, path))), "Worktree bytes must match native Git."); }
            Check(mode is GitResetMode.Soft or GitResetMode.Mixed ? File.Exists(fixture.File(path)) : !File.Exists(fixture.File(path)), "Soft/Mixed preserve the worktree; Hard/Keep/Merge return it to the target.");
            Check(tip != initial, "The test must reset across an actual commit.");
        }
    }
    private static async Task SoftIndexAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string initial = await Text(fixture.Repository, "rev-parse", "HEAD");
        await Commit(fixture, "committed.txt", "committed\n", "Second"); fixture.Write("seed.txt", "staged\n"); await Git(fixture.Repository, "add", "seed.txt"); fixture.Write("seed.txt", "unstaged\n");
        byte[] index = File.ReadAllBytes(fixture.File(".git/index")); await fixture.Repository.ResetAsync(initial, GitResetMode.Soft);
        Check(File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index), "Soft reset must not rewrite the raw index.");
        Check(await Text(fixture.Repository, "show", ":seed.txt") == "staged" && File.ReadAllText(fixture.File("seed.txt")) == "unstaged\n", "Separate index/worktree edits must survive.");
    }
    private static async Task ProtectedResetAsync()
    {
        foreach (GitResetMode mode in new[] { GitResetMode.Keep, GitResetMode.Merge })
        {
            using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string initial = await Text(fixture.Repository, "rev-parse", "HEAD");
            await Commit(fixture, "seed.txt", "committed\n", "Second"); fixture.Write("seed.txt", "local\n"); string state = await State(fixture);
            await Expect<GitCommandException>(() => fixture.Repository.ResetAsync(initial, mode)); Check(await State(fixture) == state, "Protected reset failure must preserve the actual index, refs and local file.");
        }
    }
    private static async Task ResetOtherAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitRepository repository = fixture.Repository;
        string initial = await Text(repository, "rev-parse", "HEAD"); await Git(repository, "branch", "feature/λ", initial);
        string tip = await Commit(fixture, "second.txt", "second\n", "Second");
        await Git(repository, "update-ref", "refs/remotes/origin/feature/λ", tip);
        await Git(repository, "config", "branch.feature/λ.remote", "origin"); await Git(repository, "config", "branch.feature/λ.merge", "refs/heads/feature/λ");
        await Git(repository, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
        fixture.Write("seed.txt", "worktree\n"); byte[] index = File.ReadAllBytes(fixture.File(".git/index")), config = File.ReadAllBytes(fixture.File(".git/config"));
        GitResetOtherBranchPreview preview = await repository.GetResetOtherBranchPreviewAsync(tip);
        Check(preview.SuggestedBranch == "feature/λ" && preview.Branches.Single().IsFastForward, "One matching tracking branch must be suggested.");
        await repository.ResetOtherBranchAsync(new("feature/λ", tip));
        Check(await Text(repository, "rev-parse", "refs/heads/feature/λ") == tip && await Text(repository, "symbolic-ref", "--short", "HEAD") == "main", "Reset other branch must not checkout it.");
        Check(File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index) && File.ReadAllBytes(fixture.File(".git/config")).SequenceEqual(config) && File.ReadAllText(fixture.File("seed.txt")) == "worktree\n", "Only the selected branch ref may change.");
    }
    private static async Task ResetOtherValidationAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); GitRepository repository = fixture.Repository;
        string initial = await Text(repository, "rev-parse", "HEAD"); await Git(repository, "checkout", "-b", "topic"); string tip = await Commit(fixture, "topic.txt", "topic\n", "Topic"); await Git(repository, "checkout", "main");
        string state = await State(fixture);
        await Expect<InvalidOperationException>(() => repository.ResetOtherBranchAsync(new("topic", initial)));
        await Expect<InvalidOperationException>(() => repository.ResetOtherBranchAsync(new("main", tip, true)));
        await Expect<ArgumentException>(() => repository.ResetOtherBranchAsync(new("missing", tip, true)));
        Check(await State(fixture) == state, "All branch validation must precede mutation.");
        await repository.ResetOtherBranchAsync(new("topic", initial, true)); Check(await Text(repository, "rev-parse", "topic") == initial, "Explicit Force reset allows a non-fast-forward ref move.");
    }
    private static async Task ResetOtherWorktreeAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string initial = await Text(fixture.Repository, "rev-parse", "HEAD"); await Git(fixture.Repository, "branch", "other"); string tip = await Commit(fixture, "second.txt", "second\n", "Second");
        string linked = System.IO.Path.Combine(fixture.Root, "linked"); await Git(fixture.Repository, "worktree", "add", linked, "other"); string state = await State(fixture);
        await Expect<InvalidOperationException>(() => fixture.Repository.ResetOtherBranchAsync(new("other", tip, true)));
        Check(await State(fixture) == state && await Text(new GitRepository(linked), "rev-parse", "HEAD") == initial, "A force reset must not invalidate another checked-out worktree.");
    }
    private static async Task ResetBareAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string initial = await Text(fixture.Repository, "rev-parse", "HEAD"); string tip = await Commit(fixture, "second.txt", "second\n", "Second");
        string barePath = System.IO.Path.Combine(fixture.Root, "bare.git"); await Git(fixture.Repository, "clone", "--bare", fixture.Repository.Path, barePath); GitRepository bare = new(barePath); await Git(bare, "branch", "other", initial);
        await bare.ResetOtherBranchAsync(new("other", tip)); Check(await Text(bare, "rev-parse", "other") == tip && (await bare.GetInfoAsync()).IsBare, "A ref-only reset does not require a worktree.");
    }
    private static async Task RevertNoCommitAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string path = "literal λ [*]\nfile.txt";
        string tip = await Commit(fixture, path, "added\n", "Add literal file"); fixture.Write("prior.txt", "staged\n"); await Git(fixture.Repository, "add", "prior.txt"); fixture.Write("prior.txt", "worktree\n"); string refs = await Text(fixture.Repository, "for-each-ref");
        GitRevertResult result = await fixture.Repository.RevertAsync(tip, new GitRevertOptions());
        Check(result.Success && await Text(fixture.Repository, "rev-parse", "HEAD") == tip && await Text(fixture.Repository, "for-each-ref") == refs, "Default no-commit revert must preserve every ref.");
        Check(!File.Exists(fixture.File(path)) && (await fixture.Repository.GetStatusAsync()).Changes.Any(change => change.Path == path && change.IndexStatus == 'D'), "Reverse changes must be staged for the literal file.");
        Check(await Text(fixture.Repository, "show", ":prior.txt") == "staged" && File.ReadAllText(fixture.File("prior.txt")) == "worktree\n", "Unrelated index and worktree edits survive.");
    }
    private static async Task RevertAutomaticAsync()
    {
        foreach (bool legacy in new[] { false, true })
        {
            using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string tip = await Commit(fixture, "added.txt", "added\n", "Added");
            if (legacy) { await fixture.Repository.RevertAsync(tip); }
            else { Check((await fixture.Repository.RevertAsync(tip, new GitRevertOptions(true))).Success, "Automatic revert must succeed."); }
            Check(await Text(fixture.Repository, "rev-parse", "HEAD") != tip && await Text(fixture.Repository, "show", "-s", "--format=%P", "HEAD") == tip, "Automatic and legacy revert create a child of real checkout.");
            Check((await Text(fixture.Repository, "show", "-s", "--format=%B", "HEAD")).Contains(tip, StringComparison.Ordinal) && !File.Exists(fixture.File("added.txt")), "Native revert message identifies the reverted commit.");
        }
    }
    private static async Task<string> Merge(RepositoryFixture fixture)
    {
        string initial = await Text(fixture.Repository, "rev-parse", "HEAD"); await Git(fixture.Repository, "checkout", "-b", "left"); await Commit(fixture, "left.txt", "left\n", "Left");
        await Git(fixture.Repository, "checkout", "-b", "right", initial); await Commit(fixture, "right.txt", "right\n", "Right"); await Git(fixture.Repository, "checkout", "left"); await Git(fixture.Repository, "merge", "--no-ff", "right", "-m", "Merge"); return await Text(fixture.Repository, "rev-parse", "HEAD");
    }
    private static async Task RevertParentsAsync()
    {
        foreach (int parent in new[] { 1, 2 })
        {
            using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string merge = await Merge(fixture); string before = await State(fixture);
            await Expect<ArgumentException>(() => fixture.Repository.RevertAsync(merge, new GitRevertOptions()));
            await Expect<ArgumentException>(() => fixture.Repository.RevertAsync(merge, new GitRevertOptions(MainlineParent: 3)));
            Check(await State(fixture) == before, "Invalid merge parent must not mutate index, refs, worktree or drafts.");
            Check((await fixture.Repository.RevertAsync(merge, new GitRevertOptions(MainlineParent: parent))).Success, "Either selected merge parent can be reverted.");
            Check(File.Exists(fixture.File("left.txt")) == (parent == 1) && File.Exists(fixture.File("right.txt")) == (parent == 2), "Revert must restore the selected parent tree.");
        }
    }
    private static async Task RevertDraftAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); await Git(fixture.Repository, "config", "i18n.commitEncoding", "windows-1252");
        // Linux argv is UTF-8. `git commit -m Café` with a CP1252 config would
        // misdeclare UTF-8 bytes as CP1252; use the real encoded stdin workflow.
        fixture.Write("added.txt", "added\n"); await Git(fixture.Repository, "add", "added.txt");
        await fixture.Repository.CommitAsync("Café"); string tip = await Text(fixture.Repository, "rev-parse", "HEAD");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); Encoding codec = Encoding.GetEncoding(1252);
        GitBinaryResult raw = await new GitRunner().RunBinaryAsync(fixture.Repository.Path, ["cat-file", "commit", tip]);
        Check(raw.Success && raw.StandardOutput.AsSpan().IndexOf(Encoding.ASCII.GetBytes("encoding windows-1252\n")) >= 0
            && raw.StandardOutput.AsSpan().IndexOf(new byte[] { (byte)'C', (byte)'a', (byte)'f', 0xe9 }) >= 0,
            "The source commit must really declare CP1252 and contain its e9 byte, rather than UTF-8 argv bytes.");
        Check((await fixture.Repository.GetLogAsync(1, tip, all: false)).Single().Subject == "Café", "The original commit is readable before revert.");
        string oraclePath = System.IO.Path.Combine(fixture.Root, "oracle"); await Git(fixture.Repository, "clone", "--no-local", fixture.Repository.Path, oraclePath);
        GitRepository oracle = new(oraclePath); await RepositoryFixture.ConfigureAsync(oracle); await Git(oracle, "config", "i18n.commitEncoding", "windows-1252");
        await Git(oracle, "revert", "--no-edit", "--no-commit", "--", tip);
        byte[] nativeMessage = File.ReadAllBytes(System.IO.Path.Combine(oraclePath, ".git", "MERGE_MSG"));
        Check(codec.GetString(nativeMessage).Contains("Café", StringComparison.Ordinal), "Native Git creates its revert message in the configured commit encoding.");
        string draft = "Déjà édité — €"; File.WriteAllBytes(fixture.File(".git/COMMITMESSAGE"), codec.GetBytes(draft));
        await fixture.Repository.RevertAsync(tip, new GitRevertOptions());
        string combined = codec.GetString(File.ReadAllBytes(fixture.File(".git/MERGE_MSG")));
        Check(combined.StartsWith(draft + "\n\n", StringComparison.Ordinal) && combined.Contains("Café", StringComparison.Ordinal), "Existing and generated messages retain their configured encoding.");
        Check(File.ReadAllBytes(fixture.File(".git/MERGE_MSG")).SequenceEqual(codec.GetBytes(draft + "\n\n" + codec.GetString(nativeMessage))),
            "The combined file is byte-exact against the original draft followed by the native Git message.");
        await Git(fixture.Repository, "reset", "--hard", tip); File.Delete(fixture.File(".git/MERGE_MSG")); await Git(fixture.Repository, "config", "i18n.commitEncoding", "UTF-8"); File.WriteAllBytes(fixture.File(".git/COMMITMESSAGE"), [0xff]);
        string before = await State(fixture); await Expect<DecoderFallbackException>(() => fixture.Repository.RevertAsync(tip, new GitRevertOptions())); Check(await State(fixture) == before, "Undecodable existing draft must fail before Git mutation.");
    }
    private static async Task RevertConflictAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string revert = await Commit(fixture, "seed.txt", "reverted\n", "Reverted"); string head = await Commit(fixture, "seed.txt", "later\n", "Later");
        GitRevertResult result = await fixture.Repository.RevertAsync(revert, new GitRevertOptions());
        Check(!result.Success && result.ConflictedPaths.SequenceEqual(["seed.txt"]) && await Text(fixture.Repository, "rev-parse", "HEAD") == head, "Conflict returns native diagnostics and leaves HEAD intact.");
        string before = await State(fixture); await Expect<InvalidOperationException>(() => fixture.Repository.RevertAsync(head, new GitRevertOptions())); Check(await State(fixture) == before, "A second revert cannot mutate unresolved conflicts.");
    }
    private static async Task RevertSelectionAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string first = await Commit(fixture, "one.txt", "one\n", "First"); string second = await Commit(fixture, "two.txt", "two\n", "Second");
        IReadOnlyList<GitRevertRevision> sequence = await fixture.Repository.PrepareRevertSelectionAsync([first, second]); Check(sequence.Select(item => item.Commit.Hash).SequenceEqual([first, second]), "Caller order is retained, not re-sorted or reversed.");
        string before = await State(fixture); await Expect<GitCommandException>(() => fixture.Repository.PrepareRevertSelectionAsync([first, "missing"])); Check(await State(fixture) == before, "Entire selection validation precedes the first mutation.");
    }
    private static async Task CancelledAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string initial = await Text(fixture.Repository, "rev-parse", "HEAD"); await Git(fixture.Repository, "branch", "other"); string tip = await Commit(fixture, "second.txt", "second\n", "Second"); string before = await State(fixture);
        using CancellationTokenSource source = new(); source.Cancel();
        await Expect<OperationCanceledException>(() => fixture.Repository.ResetAsync(initial, GitResetMode.Hard, source.Token));
        await Expect<OperationCanceledException>(() => fixture.Repository.ResetOtherBranchAsync(new("other", tip), source.Token));
        await Expect<OperationCanceledException>(() => fixture.Repository.RevertAsync(tip, new GitRevertOptions(), source.Token));
        Check(await State(fixture) == before, "Cancellation before dispatch preserves exact repository state.");
    }
    private static async Task RevertHookAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string tip = await Commit(fixture, "added.txt", "added\n", "Added");
        string oraclePath = System.IO.Path.Combine(fixture.Root, "oracle"); await Git(fixture.Repository, "clone", "--no-local", fixture.Repository.Path, oraclePath);
        GitRepository oracle = new(oraclePath); await RepositoryFixture.ConfigureAsync(oracle);
        string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        void InstallHooks(string name)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(); }
            string hooks = System.IO.Path.Combine(fixture.Root, name); Directory.CreateDirectory(hooks);
            foreach (string kind in new[] { "pre-commit", "prepare-commit-msg", "commit-msg" })
            {
                string hook = System.IO.Path.Combine(hooks, kind);
                string marker = System.IO.Path.Combine(hooks, kind + ".called");
                File.WriteAllText(hook, "#!/bin/sh\nprintf '%s\\n' " + Quote(kind) + " > " + Quote(marker) + "\n"
                    + (kind == "prepare-commit-msg" ? "printf 'revert-hook-rejected\\n' >&2\nexit 9\n" : "exit 0\n"));
                File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        InstallHooks("hooks"); InstallHooks("oracle-hooks");
        string hooks = System.IO.Path.Combine(fixture.Root, "hooks"), oracleHooks = System.IO.Path.Combine(fixture.Root, "oracle-hooks");
        await Git(fixture.Repository, "config", "core.hooksPath", hooks); await Git(oracle, "config", "core.hooksPath", oracleHooks);
        GitResult native = await new GitRunner().RunAsync(oracle.Path, ["revert", "--no-edit", "--", tip],
            environment: new Dictionary<string, string?> { ["GIT_EDITOR"] = "true", ["GIT_OPTIONAL_LOCKS"] = "0" });
        Check(!native.Success && native.StandardError.Contains("revert-hook-rejected", StringComparison.Ordinal), "The native prepare-commit-msg hook really rejects this revert.");
        string previous = "Previously edited message"; File.WriteAllText(fixture.File(".git/COMMITMESSAGE"), previous);
        GitCommandException? rejection = null; try { await fixture.Repository.RevertAsync(tip, new GitRevertOptions(true)); } catch (GitCommandException error) { rejection = error; }
        Check(rejection?.Result.StandardError.Contains("revert-hook-rejected", StringComparison.Ordinal) == true, "Hook stderr must be available to the dialogue.");
        Check(rejection?.Result.ExitCode == native.ExitCode, "The API reports Git's actual hook failure exit code.");
        foreach (string directory in new[] { hooks, oracleHooks })
        {
            Check(File.Exists(System.IO.Path.Combine(directory, "prepare-commit-msg.called")), "Native sequencer calls prepare-commit-msg.");
            Check(!File.Exists(System.IO.Path.Combine(directory, "pre-commit.called")) && !File.Exists(System.IO.Path.Combine(directory, "commit-msg.called")),
                "Original git revert skips verification hooks; the API must not invent extra hook execution.");
        }
        Check(await Text(fixture.Repository, "rev-parse", "HEAD") == tip && !File.Exists(fixture.File("added.txt")), "A rejected commit leaves reverse changes available for user recovery.");
        Check(await Text(fixture.Repository, "write-tree") == await Text(oracle, "write-tree"), "Rejected automatic revert leaves the same reverse index as native Git.");
        string nativeDraft = File.ReadAllText(System.IO.Path.Combine(oraclePath, ".git", "MERGE_MSG"));
        Check(File.ReadAllText(fixture.File(".git/MERGE_MSG")) == previous + "\n\n" + nativeDraft
            && File.ReadAllText(fixture.File(".git/COMMITMESSAGE")) == previous, "Even an exit-128 hook rejection preserves the prior edited message and the native generated message.");
    }
}
