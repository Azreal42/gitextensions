using System.Text;
using System.Text.Json;
using GitExtensions.Linux;

internal static partial class RebaseAdvancedScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("rebase options preserve upstream and real onto branch selection", Guard(OntoAsync)),
        ("rebase date options take precedence over interactive and merge options", Guard(DatesAsync)),
        ("rebase autosquash explicitly overrides config and retains native fixups", Guard(AutoSquashAsync)),
        ("rebase merge graph retains native label reset merge and update-ref todo", Guard(MergeGraphAsync)),
        ("rebase nullable update-refs follows config and explicit overrides", Guard(UpdateRefsAsync)),
        ("rebase auto-stash false leaves native configuration in force", Guard(AutoStashAsync)),
        ("rebase merge state reconnects metadata and tracks skipped conflicts", Guard(ConflictsAsync)),
        ("rebase merge and apply grids preserve distinct author and committer dates", Guard(CommitDateAsync)),
        ("rebase abort restores original branch index tree and file bytes", Guard(AbortAsync)),
        ("rebase apply backend exposes current pending patches and rejects edit-todo", Guard(ApplyBackendAsync)),
        ("rebase external session gets an editor for edit-todo and continue", Guard(ExternalTodoAsync)),
        ("rebase native editor strictly preserves CP1252 message bytes", Guard(NativeEncodingAsync)),
        ("rebase legacy reword and squash use the strict commit codec", Guard(LegacyEncodingAsync)),
        ("rebase editor preimage and pathname guards preserve concurrent text", Guard(EditorGuardsAsync)),
        ("rebase incompatible native message is rejected without file corruption", Guard(EditorEncodingFailureAsync)),
        ("rebase invalid options codecs and pre-cancellation leave state untouched", Guard(ValidationAsync)),
        ("rebase cancellation terminates a blocked native editor and stays recoverable", Guard(EditorCancellationAsync)),
        ("rebase saved native codec keeps merge and apply Abort independent of current configuration", Guard(SavedNativeAbortCodecAsync)),
        ("rebase saved legacy codec keeps recovery readable and Abort available", Guard(SavedLegacyAbortCodecAsync)),
        ("external rebase invalid codecs expose minimal merge and apply recovery without mutation", Guard(ExternalAbortCodecAsync)),
        ("terminal Abort and completion permit new native and legacy rebase attempts", Guard(TerminalRebaseLifecycleAsync)),
        ("rebase worktree lease protects a live native editor across processes and cancellation", Guard(NativeRebaseLeaseAsync)),
        ("rebase worktree lease protects startup before backend creation and releases after cancellation", Guard(RebaseStartupLeaseAsync)),
        ("rebase worktree leases isolate linked worktrees and preserve unsafe stale journals", Guard(RebaseLeaseIsolationAsync)),
        ("pull-rebase refuses paused merge and apply sessions before auto-stash or fetch", Guard(PullRebaseActiveBackendAsync)),
        ("damaged native merge journals retain Git recovery and exact explicit preservation", Guard(InvalidNativeMergeJournalsAsync)),
        ("damaged native apply journals retain Git recovery and exact explicit preservation", Guard(InvalidNativeApplyJournalsAsync)),
        ("damaged legacy journals retain Git recovery and exact explicit preservation", Guard(InvalidLegacyJournalsAsync)),
        ("ambiguous native and legacy journals remain preserved through Git Abort and explicit recovery", Guard(AmbiguousRebaseJournalsAsync)),
        ("damaged journals after terminal Abort require exact explicit preservation before a fresh attempt", Guard(DamagedTerminalAbortJournalsAsync)),
        ("orphan preservation uses the actual linked worktree GitDirectory without touching the primary checkout", Guard(LinkedWorktreeJournalPreservationAsync))
    ];
    private const string ModeVariable = "GITEXTENSIONS_REBASE_TEST_MODE";
    private const string CaptureVariable = "GITEXTENSIONS_REBASE_TEST_CAPTURE";

    // Registered before the ordinary console suite by Root. This is a real child executable invoked by Git.
    internal static bool TryHandleEditor(string[] args, out int exitCode)
    {
        if (TryHandleLifecycleWorker(args, out exitCode)) { return true; }
        exitCode = 0;
        if (!GitRebaseEditor.IsNativeEditorInvocation(args) || Environment.GetEnvironmentVariable(ModeVariable) is not string mode) { return false; }
        try
        {
            GitRebaseEditorRequest request = GitRebaseEditor.ReadNativeEditorRequest(args);
            string directory = Environment.GetEnvironmentVariable(CaptureVariable) ?? throw new InvalidOperationException("Missing private editor capture directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, request.Kind + "-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(new { request.Kind, request.Text, request.EncodingName, request.PreimageSha256, request.FilePath }));
            Encoding encoding = Encoding.GetEncoding(request.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (mode == "block")
            {
                File.WriteAllText(System.IO.Path.Combine(directory, "editor.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Thread.Sleep(Timeout.Infinite); return true;
            }
            if (mode == "cas" && request.Kind == GitRebaseEditorKind.Todo)
            {
                byte[] concurrent = encoding.GetBytes(request.Text + "\n# Concurrent edit preserved\n");
                File.WriteAllBytes(request.FilePath, concurrent);
                ExpectSync<InvalidOperationException>(() => GitRebaseEditor.WriteNativeEditorRequest(request, request.Text));
                Check(File.ReadAllBytes(request.FilePath).SequenceEqual(concurrent), "CAS must preserve the concurrent todo bytes.");
                return true;
            }
            if (mode == "unsafe" && request.Kind == GitRebaseEditorKind.Todo)
            {
                string outside = System.IO.Path.Combine(directory, "outside.txt"); File.WriteAllText(outside, "outside unchanged");
                ExpectSync<InvalidOperationException>(() => GitRebaseEditor.ReadNativeEditorRequest([args[0], outside]));
                Check(File.ReadAllText(outside) == "outside unchanged", "A path outside Git editor files must remain untouched.");
            }
            string text = request.Text;
            if (request.Kind == GitRebaseEditorKind.Todo)
            {
                if (mode is "reword" or "bad-message") { text = ReplaceFirstPick(text, "reword"); }
                if (mode == "break") { text = "break\n" + text; }
                if (mode == "structural") { text = "exec true\n" + text; }
            }
            else if (mode == "reword") { text = "Café £ réécrit\n\nMessage exact à conserver.\n"; }
            else if (mode == "bad-message")
            {
                byte[] before = File.ReadAllBytes(request.FilePath);
                ExpectSync<EncoderFallbackException>(() => GitRebaseEditor.WriteNativeEditorRequest(request, "Incompatible 🧪"));
                Check(File.ReadAllBytes(request.FilePath).SequenceEqual(before), "An encoding failure must not touch Git's message file.");
                exitCode = 1; return true;
            }
            GitRebaseEditor.WriteNativeEditorRequest(request, text);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
        return true;
    }

    private static string ReplaceFirstPick(string text, string action)
    {
        int start = text.StartsWith("pick ", StringComparison.Ordinal) ? 0 : text.IndexOf("\npick ", StringComparison.Ordinal) + 1;
        if (start < 0 || !text.AsSpan(start).StartsWith("pick ")) { throw new InvalidOperationException("Expected a real pick in Git's native todo."); }
        return text[..start] + action + text[(start + 4)..];
    }
    private static Func<Task> Guard(Func<Task> test) => async () =>
    {
        string? previous = Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS");
        Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        try { await test(); } finally { Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", previous); }
    };
    private sealed class EditorScope : IDisposable
    {
        private readonly string? _mode = Environment.GetEnvironmentVariable(ModeVariable);
        private readonly string? _capture = Environment.GetEnvironmentVariable(CaptureVariable);
        internal EditorScope(RepositoryFixture fixture, string mode)
        { DirectoryPath = System.IO.Path.Combine(fixture.Root, "editor-captures"); Directory.CreateDirectory(DirectoryPath); Environment.SetEnvironmentVariable(ModeVariable, mode); Environment.SetEnvironmentVariable(CaptureVariable, DirectoryPath); }
        internal string DirectoryPath { get; }
        internal string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("Missing test apphost.");
        internal string[] Texts(GitRebaseEditorKind kind) => Directory.GetFiles(DirectoryPath, kind + "-*.json")
            .Select(path => JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty("Text").GetString()!).ToArray();
        public void Dispose() { Environment.SetEnvironmentVariable(ModeVariable, _mode); Environment.SetEnvironmentVariable(CaptureVariable, _capture); }
    }
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static void ExpectSync<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<GitResult> Git(GitRepository repository, params string[] arguments)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, arguments);
        if (!result.Success) { throw new GitCommandException(arguments, result); } return result;
    }
    private static async Task<string> Text(GitRepository repository, params string[] arguments)
        => (await Git(repository, arguments)).StandardOutput.TrimEnd('\r', '\n');
    private static async Task<string> Commit(RepositoryFixture fixture, string path, string text, string message)
    { fixture.Write(path, text); await fixture.Repository.StageAsync([path]); await fixture.Repository.CommitAsync(message); return await fixture.Repository.ResolveRevisionAsync("HEAD"); }
    private static async Task<string> State(RepositoryFixture fixture)
    {
        string files = string.Join("\n", Directory.GetFiles(fixture.Repository.Path, "*", SearchOption.AllDirectories)
            .Where(path => !System.IO.Path.GetRelativePath(fixture.Repository.Path, path).StartsWith(".git/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => System.IO.Path.GetRelativePath(fixture.Repository.Path, path) + ":" + Convert.ToHexString(File.ReadAllBytes(path))));
        return await Text(fixture.Repository, "rev-parse", "HEAD") + "\n" + await Text(fixture.Repository, "for-each-ref") + "\n"
            + Convert.ToHexString(File.ReadAllBytes(fixture.File(".git/index"))) + "\n" + Convert.ToHexString(File.ReadAllBytes(fixture.File(".git/config"))) + "\n" + files;
    }
    private static async Task<string> Topic(RepositoryFixture fixture)
    {
        string baseline = await fixture.Repository.ResolveRevisionAsync("HEAD");
        await Git(fixture.Repository, "switch", "-c", "topic"); await Commit(fixture, "topic.txt", "topic\n", "Topic");
        await Git(fixture.Repository, "switch", "main"); await Commit(fixture, "upstream.txt", "upstream\n", "Upstream");
        await Git(fixture.Repository, "switch", "topic"); return baseline;
    }

    private static async Task OntoAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string baseline = await f.Repository.ResolveRevisionAsync("HEAD");
        await Git(f.Repository, "switch", "-c", "old-base"); string excluded = await Commit(f, "excluded.txt", "excluded\n", "Excluded upstream");
        await Git(f.Repository, "switch", "-c", "topic"); await Commit(f, "literal [*] λ\nfile.txt", "retained\n", "Retained");
        await Git(f.Repository, "switch", "main"); string onto = await Commit(f, "onto.txt", "onto\n", "Onto");
        GitRebaseOptions options = new() { BranchName = "topic", From = excluded, OnTo = "main" };
        await f.Repository.RebaseAsync(options);
        Check(await Text(f.Repository, "branch", "--show-current") == "topic", "The named target branch must be checked out and rewritten by native --onto.");
        Check((await f.Repository.GetLogAsync(1, "HEAD", all: false)).Single().Parents.Single() == onto, "The selected range must start on OnTo.");
        Check(!File.Exists(f.File("excluded.txt")) && File.Exists(f.File("literal [*] λ\nfile.txt")), "From must exclude its upstream changes and retain literal file bytes.");
        Check(await Text(f.Repository, "rev-parse", "old-base") == excluded && await Text(f.Repository, "rev-parse", "main~1") == baseline, "Other original references remain intact.");
    }
    private static async Task DatesAsync()
    {
        foreach (bool ignore in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f);
            await Git(f.Repository, "commit", "--amend", "--no-edit", "--date=2001-02-03T04:05:06Z");
            GitCommit before = (await f.Repository.GetLogAsync(1, "HEAD", all: false)).Single();
            using EditorScope editor = new(f, "capture");
            await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true, PreserveMerges = true, AutoSquash = true,
                IgnoreDate = ignore, CommitterDateIsAuthorDate = true }, editor.Executable);
            string[] dates = (await Text(f.Repository, "show", "-s", "--format=%aI%n%cI", "HEAD")).Split('\n');
            Check(ignore ? dates[0] != before.AuthoredAt.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture) : dates[0] == dates[1], "Date options must use Git's original precedence and timestamp semantics.");
            Check(editor.Texts(GitRebaseEditorKind.Todo).Length == 0, "Date flags supersede interactive, preserve-merges and autosquash; no sequence editor is invoked.");
        }
    }
    private static async Task AutoSquashAsync()
    {
        foreach (bool autosquash in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string baseline = await f.Repository.ResolveRevisionAsync("HEAD");
            await Git(f.Repository, "switch", "-c", "topic"); await Commit(f, "topic.txt", "one\n", "Feature base"); await Commit(f, "topic.txt", "two\n", "fixup! Feature base");
            await Git(f.Repository, "switch", "main"); await Commit(f, "upstream.txt", "upstream\n", "Upstream"); await Git(f.Repository, "switch", "topic");
            await f.Repository.SetConfigAsync("rebase.autosquash", "true"); using EditorScope editor = new(f, "capture");
            await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true, AutoSquash = autosquash }, editor.Executable);
            Check(await Text(f.Repository, "rev-list", "--count", "main..topic") == (autosquash ? "1" : "2"), "Explicit --no-autosquash must override configured true; true must squash the real fixup.");
            Check(editor.Texts(GitRebaseEditorKind.Todo).Single().Contains(autosquash ? "fixup " : "pick ", StringComparison.Ordinal), "Git-generated todo action must remain intact.");
            Check(await Text(f.Repository, "config", "rebase.autosquash") == "true" && File.ReadAllText(f.File("topic.txt")) == "two\n", "Rebase options must not write configuration or lose content.");
            Check((await f.Repository.GetLogAsync(20, "topic", all: false)).Any(commit => commit.Hash == baseline), "The shared base remains reachable.");
        }
    }
    private static async Task MergeGraphAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Git(f.Repository, "switch", "-c", "topic");
        string first = await Commit(f, "one.txt", "one\n", "First"); await Git(f.Repository, "branch", "side", first);
        string second = await Commit(f, "two.txt", "two\n", "Second"); await Git(f.Repository, "branch", "dependent", second);
        await Git(f.Repository, "switch", "side"); await Commit(f, "side.txt", "side\n", "Side");
        await Git(f.Repository, "switch", "topic"); await Git(f.Repository, "merge", "--no-ff", "--no-edit", "side");
        await Git(f.Repository, "switch", "main"); await Commit(f, "upstream.txt", "upstream\n", "Upstream"); await Git(f.Repository, "switch", "topic");
        GitRebaseCapabilities cap = await f.Repository.GetRebaseCapabilitiesAsync(); using EditorScope editor = new(f, "structural");
        await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true, PreserveMerges = true, UpdateRefs = cap.SupportUpdateRefs ? true : null }, editor.Executable);
        string todo = editor.Texts(GitRebaseEditorKind.Todo).Single();
        Check(todo.Contains("label ", StringComparison.Ordinal) && todo.Contains("reset ", StringComparison.Ordinal) && todo.Contains("merge -C ", StringComparison.Ordinal), "Git's merge graph instructions must survive the textual bridge.");
        Check(!cap.SupportUpdateRefs || todo.Contains("update-ref refs/heads/dependent", StringComparison.Ordinal), "Git-generated update-ref must remain in the native todo.");
        Check(await Text(f.Repository, "rev-list", "--count", "--merges", "main..topic") == "1", "PreserveMerges must retain a genuine two-parent merge.");
        Check(File.Exists(f.File("one.txt")) && File.Exists(f.File("two.txt")) && File.Exists(f.File("side.txt")), "All branches' committed file contents must survive.");
    }
    private static async Task UpdateRefsAsync()
    {
        foreach (bool? choice in new bool?[] { null, false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Git(f.Repository, "switch", "-c", "topic");
            string first = await Commit(f, "one.txt", "one\n", "First"); await Git(f.Repository, "branch", "dependent", first); await Commit(f, "two.txt", "two\n", "Second");
            await Git(f.Repository, "switch", "main"); await Commit(f, "upstream.txt", "upstream\n", "Upstream"); await Git(f.Repository, "switch", "topic");
            await f.Repository.SetConfigAsync("rebase.updaterefs", "true"); GitRebaseCapabilities cap = await f.Repository.GetRebaseCapabilitiesAsync();
            if (!cap.SupportUpdateRefs) { if (choice is not null) { await Expect<NotSupportedException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", UpdateRefs = choice })); } continue; }
            await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", UpdateRefs = choice });
            Check((await Text(f.Repository, "rev-parse", "dependent") != first) == (choice != false), "Nullable UpdateRefs follows config; explicit false/true controls actual dependent references.");
            Check(await Text(f.Repository, "config", "rebase.updaterefs") == "true", "The override must not rewrite repository settings.");
        }
    }
    private static async Task AutoStashAsync()
    {
        foreach ((bool configured, bool selected) in new[] { (true, false), (false, true), (false, false) })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f); await f.Repository.SetConfigAsync("rebase.autostash", configured ? "true" : "false");
            f.Write("seed.txt", "tracked dirty bytes\n"); string before = await State(f);
            if (!configured && !selected) { await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" })); Check(await State(f) == before, "Dirty rejection must preserve the repository."); }
            else
            {
                await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", AutoStash = selected });
                Check(File.ReadAllText(f.File("seed.txt")) == "tracked dirty bytes\n", "Native autostash must restore the original dirty bytes.");
                Check(await Text(f.Repository, "stash", "list") == "", "Successfully reapplied autostash must not leave a stray stash.");
            }
            Check(await Text(f.Repository, "config", "rebase.autostash") == (configured ? "true" : "false"), "AutoStash false must omit --no-autostash and preserve native config.");
        }
    }
    private static async Task<string[]> ConflictTopic(RepositoryFixture f)
    {
        foreach (int i in new[] { 1, 2, 3 }) { f.Write("conflict" + i + ".txt", "base\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Conflict base");
        await Git(f.Repository, "switch", "-c", "topic"); List<string> hashes = [];
        foreach (int i in new[] { 1, 2, 3 }) { hashes.Add(await Commit(f, "conflict" + i + ".txt", "topic\n", "Conflict " + i)); }
        await Git(f.Repository, "switch", "main"); foreach (int i in new[] { 1, 2, 3 }) { f.Write("conflict" + i + ".txt", "upstream\n"); }
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Upstream conflicts"); await Git(f.Repository, "switch", "topic"); return hashes.ToArray();
    }
    private static async Task ConflictsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string[] original = await ConflictTopic(f);
        await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
        GitRepository reopened = new(f.Repository.Path); GitRebaseState state = await reopened.GetRebaseStateAsync();
        Check(state.Backend == GitRebaseBackend.Merge && state.HasConflicts && !state.CanContinue && state.CurrentCommit == original[0], "Native state must expose the stopped commit and its index conflicts after reopening.");
        Check(state.Options?.BranchName == "main" && state.Steps.Any(p => p.Status == GitRebaseStepStatus.Applying && p.Commit?.Subject == "Conflict 1")
            && state.Steps.Count(p => p.Status == GitRebaseStepStatus.Pending) >= 2, "Metadata, native options and remaining todo must be reconnectable.");
        await reopened.ResolveConflictAsync("conflict1.txt", GitConflictResolution.Theirs);
        Check((await reopened.GetRebaseStateAsync()).CanContinue, "Staging the genuine resolution must enable continue.");
        await Expect<GitCommandException>(() => reopened.ContinueRebaseAsync());
        Check((await new GitRepository(f.Repository.Path).GetRebaseStateAsync()).CurrentCommit == original[1], "Continue must stop at the next actual conflict.");
        await Expect<GitCommandException>(() => reopened.SkipRebaseAsync()); state = await reopened.GetRebaseStateAsync();
        Check(state.CurrentCommit == original[2] && state.Steps.Any(p => p.CommitHash == original[1] && p.Status == GitRebaseStepStatus.Skipped), "Skip must advance Git and retain the skipped patch across reopening.");
        await reopened.ResolveConflictAsync("conflict3.txt", GitConflictResolution.Theirs); await reopened.ContinueRebaseAsync();
        Check(!(await reopened.GetRebaseStateAsync()).IsActive && File.ReadAllText(f.File("conflict2.txt")) == "upstream\n", "Completion must remove pending state and omit the skipped patch's changes.");
    }
    private static async Task CommitDateAsync()
    {
        DateTimeOffset authorDate = DateTimeOffset.Parse("2001-02-03T04:05:06+02:00", System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset commitDate = DateTimeOffset.Parse("2010-11-12T13:14:15-03:00", System.Globalization.CultureInfo.InvariantCulture);
        foreach (string backend in new[] { "merge", "apply" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await ConflictTopic(f);
            GitResult amended = await new GitRunner().RunAsync(f.Repository.Path, ["commit", "--amend", "--no-edit", "--reset-author"],
                environment: new Dictionary<string, string?>
                {
                    ["GIT_AUTHOR_DATE"] = "2001-02-03T04:05:06+02:00",
                    ["GIT_COMMITTER_DATE"] = "2010-11-12T13:14:15-03:00"
                });
            Check(amended.Success, "The fixture must contain a real commit with distinct author and committer timestamps.");
            string target = await f.Repository.ResolveRevisionAsync("HEAD"); await f.Repository.SetConfigAsync("rebase.backend", backend);
            await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            GitRebaseState state = await new GitRepository(f.Repository.Path).GetRebaseStateAsync();
            GitRebasePatch patch = state.Steps.Single(step => step.CommitHash == target);
            Check(state.Backend == (backend == "merge" ? GitRebaseBackend.Merge : GitRebaseBackend.Apply), "The date proof must read the requested real backend.");
            Check(patch.AuthorDate == authorDate && patch.CommitDate == commitDate && patch.AuthorDate != patch.CommitDate,
                "PatchGrid date must expose the commit's committer date separately from its original author date.");
            Check(patch.CommitDate?.Offset == commitDate.Offset && patch.Commit?.AuthoredAt == authorDate,
                "The committer timezone and unchanged global GitCommit author metadata must both be preserved.");
            await f.Repository.AbortRebaseAsync();
        }
    }
    private static async Task AbortAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await ConflictTopic(f); string head = await f.Repository.ResolveRevisionAsync("HEAD");
        string tree = await Text(f.Repository, "write-tree"); string config = File.ReadAllText(f.File(".git/config"));
        await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
        await new GitRepository(f.Repository.Path).AbortRebaseAsync();
        Check(await f.Repository.ResolveRevisionAsync("HEAD") == head && await Text(f.Repository, "write-tree") == tree && await Text(f.Repository, "branch", "--show-current") == "topic", "Native abort restores the original checkout, ref and index tree.");
        Check(File.ReadAllText(f.File("conflict1.txt")) == "topic\n" && File.ReadAllText(f.File(".git/config")) == config && !(await f.Repository.GetRebaseStateAsync()).IsActive, "Abort must preserve file/config bytes and clear native operation state.");
    }
    private static async Task ApplyBackendAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string[] hashes = await ConflictTopic(f); await f.Repository.SetConfigAsync("rebase.backend", "apply");
        await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
        GitRebaseState state = await new GitRepository(f.Repository.Path).GetRebaseStateAsync();
        Check(state.Backend == GitRebaseBackend.Apply && !state.CanEditTodo && state.CurrentCommit == hashes[0] && state.Steps.Count == 3,
            "Apply backend must report real numbered patch files and stopped commit rather than invent a merge todo.");
        Check(state.Steps[0].Commit?.Subject == "Conflict 1" && state.Steps[0].PatchPath is string patch && File.Exists(patch), "Patch metadata and on-disk mailbox path must be useful to the grid.");
        await Expect<InvalidOperationException>(() => f.Repository.EditRebaseTodoAsync()); await f.Repository.AbortRebaseAsync();
    }
    private static async Task ExternalTodoAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f); await Commit(f, "second.txt", "second\n", "Second");
        string upstream = await f.Repository.ResolveRevisionAsync("main");
        GitResult started = await new GitRunner().RunAsync(f.Repository.Path, ["rebase", "--interactive", "main"],
            environment: new Dictionary<string, string?> { ["GIT_SEQUENCE_EDITOR"] = "sed -i '1i break'" });
        Check(started.Success, "The external native editor must pause at break.");
        GitRebaseState state = await f.Repository.GetRebaseStateAsync(); Check(state.Options is null && state.IsActive, "External state must not pretend to know the user's original options.");
        using EditorScope editor = new(f, "capture"); await new GitRepository(f.Repository.Path).EditRebaseTodoAsync(editor.Executable);
        Check(editor.Texts(GitRebaseEditorKind.Todo).Single().Contains("pick ", StringComparison.Ordinal), "Edit-todo must invoke the native editor with the remaining Git todo.");
        await new GitRepository(f.Repository.Path).ContinueRebaseAsync();
        Check((await f.Repository.GetLogAsync(2, "HEAD", all: false)).Last().Parents.Contains(upstream), "Continue from a new repository object must use the persistent native editor state.");
    }
    private static async Task NativeEncodingAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f); await f.Repository.SetConfigAsync("i18n.commitEncoding", "windows-1252");
        await f.Repository.CommitAsync(new GitCommitOptions("Café £ original", Amend: true)); using EditorScope editor = new(f, "reword");
        await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true }, editor.Executable);
        Check(editor.Texts(GitRebaseEditorKind.Message).Single().Contains("Café £ original", StringComparison.Ordinal), "The native editor must decode Git's actual legacy bytes correctly.");
        GitBinaryResult raw = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["cat-file", "commit", "HEAD"]);
        Check(raw.Success && Encoding.ASCII.GetString(raw.StandardOutput).Contains("encoding windows-1252", StringComparison.Ordinal)
            && raw.StandardOutput.AsSpan().IndexOf(new byte[] { (byte)'C', (byte)'a', (byte)'f', 0xe9, (byte)' ', 0xa3 }) >= 0, "The rewritten object must retain its declared CP1252 encoding and exact message bytes.");
        Check((await f.Repository.GetLogAsync(1, "HEAD", all: false)).Single().Subject == "Café £ réécrit", "Canonical UTF8 log must display the exact rewritten message.");
    }
    private static async Task LegacyEncodingAsync()
    {
        foreach (bool squash in new[] { false, true })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string baseline = await f.Repository.ResolveRevisionAsync("HEAD");
            await f.Repository.SetConfigAsync("i18n.commitEncoding", "iso-8859-1"); await Commit(f, "one.txt", "one\n", "Café one"); await Commit(f, "two.txt", "two\n", "Café two");
            GitRebasePlan plan = await f.Repository.GetRebasePlanAsync(baseline); GitRebaseStep[] steps = plan.Steps.ToArray();
            int target = squash ? 1 : 0; steps[target] = steps[target] with { Action = squash ? GitRebaseAction.Squash : GitRebaseAction.Reword, Message = "Café édition exacte" };
            await f.Repository.InteractiveRebaseAsync(plan with { Steps = steps }, Environment.ProcessPath!);
            IReadOnlyList<GitCommit> log = await f.Repository.GetLogAsync(5, "HEAD", all: false);
            Check(log.Any(c => c.Subject == "Café édition exacte"), "Legacy reword/squash must encode the supplied message using i18n.commitEncoding.");
            GitBinaryResult raw = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["cat-file", "commit", log.First(c => c.Subject == "Café édition exacte").Hash]);
            Check(raw.StandardOutput.AsSpan().IndexOf(new byte[] { (byte)'C', (byte)'a', (byte)'f', 0xe9 }) >= 0, "Legacy editor must write Latin1 bytes, not UTF8 under a Latin1 header.");
        }
    }
    private static async Task EditorGuardsAsync()
    {
        foreach (string mode in new[] { "cas", "unsafe" })
        {
            using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f); using EditorScope editor = new(f, mode);
            await f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true }, editor.Executable);
            Check(!(await f.Repository.GetRebaseStateAsync()).IsActive && editor.Texts(GitRebaseEditorKind.Todo).Length == 1, "Real native editor guard assertions must execute while retaining a valid Git todo.");
        }
    }
    private static async Task EditorEncodingFailureAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f); await f.Repository.SetConfigAsync("i18n.commitEncoding", "windows-1252");
        using EditorScope editor = new(f, "bad-message");
        await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true }, editor.Executable));
        Check((await f.Repository.GetRebaseStateAsync()).IsActive && editor.Texts(GitRebaseEditorKind.Message).Length == 1, "A failed native message edit leaves Git recoverable, rather than silently replacing incompatible glyphs.");
        Check(!(await f.Repository.GetLogAsync(5, "HEAD", all: false)).Any(c => c.Subject.Contains('�') || c.Subject.Contains("Incompatible", StringComparison.Ordinal)), "No corrupted replacement message may be committed.");
        await f.Repository.AbortRebaseAsync();
    }
    private static async Task ValidationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f); string before = await State(f);
        await Expect<ArgumentException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", From = "HEAD~1" }));
        await Expect<GitCommandException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "missing-ref" }));
        await Expect<ArgumentException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "--exec=touch-marker" }));
        using CancellationTokenSource cancel = new(); cancel.Cancel(); await Expect<OperationCanceledException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }, cancellationToken: cancel.Token));
        Check(await State(f) == before, "Validation and pre-cancellation must not alter the raw index, refs, config or files.");
        foreach (string codec in new[] { "utf-16", "shift_jis", "not-an-encoding" })
        {
            await f.Repository.SetConfigAsync("i18n.commitEncoding", codec); before = await State(f);
            await Expect<NotSupportedException>(() => f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main" }));
            Check(await State(f) == before && !Directory.Exists(f.File(".git/gitextensions-rebase-native")), "Incompatible codec rejection must occur before any state/mutation.");
        }
        await f.Repository.SetConfigAsync("i18n.commitEncoding", "windows-1252"); GitRebasePlan plan = await f.Repository.GetRebasePlanAsync("main");
        GitRebaseStep[] steps = plan.Steps.Select(s => s with { Action = GitRebaseAction.Reword, Message = "Cannot encode 🧪" }).ToArray(); before = await State(f);
        await Expect<EncoderFallbackException>(() => f.Repository.InteractiveRebaseAsync(plan with { Steps = steps }, Environment.ProcessPath!));
        Check(await State(f) == before && !Directory.Exists(f.File(".git/gitextensions-rebase")), "Legacy reword must validate glyphs before rewriting HEAD or writing its state.");
    }
    private static async Task EditorCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This process-tree assertion uses Linux /proc."); }
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); await Topic(f); using EditorScope editor = new(f, "block"); using CancellationTokenSource cancel = new();
        Task<GitResult> running = f.Repository.RebaseAsync(new GitRebaseOptions { BranchName = "main", Interactive = true }, editor.Executable, cancel.Token);
        string pidFile = System.IO.Path.Combine(editor.DirectoryPath, "editor.pid"); DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(pidFile) && DateTime.UtcNow < deadline && !running.IsCompleted) { await Task.Delay(25); }
        Check(File.Exists(pidFile), "The real Git child editor must enter its blocked phase."); int pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        cancel.Cancel(); await Expect<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(10)));
        string stat = "/proc/" + pid + "/stat"; bool alive;
        try { alive = File.Exists(stat) && LinuxProcessState.IsRunningStat(File.ReadAllText(stat)); } catch (IOException) { alive = false; }
        Check(!alive, "Cancellation must terminate the native editor child.");
        if ((await f.Repository.GetRebaseStateAsync()).IsActive) { await new GitRepository(f.Repository.Path).AbortRebaseAsync(); }
        Check(!(await f.Repository.GetRebaseStateAsync()).IsActive, "Any pending native operation must remain reconnectable and abortable after cancellation.");
    }
}
