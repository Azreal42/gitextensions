using System.Diagnostics;
using System.Reflection;
using System.Text;
using GitExtensions.Linux;
using GitExtensions.Linux.Scripts;

internal static class GitCommandCacheScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("command cache real tree reader hits without a second ls-tree and Clear misses", Guard(TreeAsync)),
        ("command cache real blob reader preserves Unicode NUL CRLF TAB ESC and isolates returned bytes", Guard(BlobAsync)),
        ("command cache real commit details reuse object and no-notes message reads while mailmap stays fresh", Guard(DetailsAsync)),
        ("command cache real commit notes remain fresh after note replacement", Guard(NotesAsync)),
        ("command cache HEAD stays live while immutable tree arguments remain distinct", Guard(LiveHeadAndArgumentsAsync)),
        ("command cache true repository namespaces partition identical object arguments", Guard(RepositoryPartitionAsync)),
        ("command cache captured environments partition true immutable blob reads", Guard(EnvironmentPartitionAsync)),
        ("command cache real readonly stdin hashes retain distinct exact input byte identities", Guard(StdinPartitionAsync)),
        ("command cache retention copies stdout stderr and warnings independently of miss and hit callers", Guard(CopiesAsync)),
        ("command cache unsuccessful declined throwing and oversized factories never admit results", Guard(AdmissionAsync)),
        ("command cache precancel and cancellation after factory completion do not admit or invoke wrongly", Guard(CancellationAsync)),
        ("command cache LRU50 hit and inspection touch eviction without changing insertion order", Guard(LruAsync)),
        ("command cache retained byte budget evicts independently of entry capacity and rejects excessive text", Guard(ByteBudgetAsync)),
        ("command cache observers run on an unlocked cache and isolate throwing reentrant subscribers", Guard(ObserversAsync)),
        ("command cache Clear during a real read permits later admission independently of the journal", Guard(ClearInFlightAsync)),
        ("command cache real reader cancellation drains its actor and leaves no retained result", Guard(RealCancellationAsync)),
        ("command cache competing misses return their own bytes and retain one coherent immutable entry", Guard(CompetingMissesAsync)),
        ("command cache public runner and live status stay outside immutable read admission", Guard(UncachedPathsAsync))
    ];

    private static Func<Task> Guard(Func<Task> scenario) => async () =>
    {
        GitEnvironmentOptions options = GitEnvironment.Options;
        GitCommandLogCapturePolicy policy = GitCommandLog.CapturePolicy;
        string? fallback = GitRunner.AskpassFallbackPath;
        string[] names = ["GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_OPTIONAL_LOCKS", "GIT_ASKPASS", "SSH_ASKPASS", "DISPLAY", "WAYLAND_DISPLAY"];
        Dictionary<string, string?> previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        GitEnvironment.Configure(new() { Executable = "/usr/bin/git" });
        GitCommandLog.ConfigureCapture(new(false)); GitRunner.AskpassFallbackPath = null;
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null"); Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_OPTIONAL_LOCKS", "0");
        foreach (string name in names.Skip(3)) { Environment.SetEnvironmentVariable(name, null); }
        GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        try { await scenario(); }
        finally
        {
            GitCommandCache.Shared.Clear(); GitCommandLog.Clear(); GitEnvironment.Configure(options);
            GitCommandLog.ConfigureCapture(policy); GitRunner.AskpassFallbackPath = fallback;
            foreach ((string name, string? value) in previous) { Environment.SetEnvironmentVariable(name, value); }
        }
    };
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static int Invocations(string verb) => GitCommandLog.Snapshot().Count(entry => entry.Command == "git " + verb);
    private static async Task<string> Head(GitRepository repository) => await repository.ResolveRevisionAsync("HEAD");
    private static GitBinaryResult Bytes(string value, int exit = 0) => new(exit, Encoding.UTF8.GetBytes(value), "");
    private static Task<GitBinaryResult> Read(GitCommandCache cache, string identity,
        Func<CancellationToken, Task<(GitBinaryResult Result, bool Admit)>> factory, CancellationToken token = default)
        => (Task<GitBinaryResult>)typeof(GitCommandCache).GetMethod("ReadThroughAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cache, [identity, identity, "git test " + identity, factory, token])!;
    private static Task<GitBinaryResult> Put(GitCommandCache cache, string identity, string text)
        => Read(cache, identity, _ => Task.FromResult((Bytes(text), true)));
    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    // Reflection reaches only the exact internal opt-in helper agreed with its author.
    // Every helper call below still launches Git on a miss. Public readers supply the main proof.
    private static Task<GitBinaryResult> RepositoryRead(GitRepository repository, string kind, IReadOnlyList<string> arguments,
        byte[]? input = null, IReadOnlyDictionary<string, string?>? environment = null, CancellationToken token = default)
    {
        Type family = typeof(GitRepository).Assembly.GetType("GitExtensions.Linux.GitCachedReadKind", throwOnError: true)!;
        MethodInfo method = typeof(GitRepository).GetMethod("RunCachedBinaryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<GitBinaryResult>)method.Invoke(repository, [Enum.Parse(family, kind), arguments, token, input, environment, null])!;
    }

    private static async Task TreeAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write("literal [tree] λ.txt", "content\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("tree literal");
        string sha = await Head(f.Repository); GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        IReadOnlyList<GitTreeEntry> first = await f.Repository.GetTreeAsync(sha);
        int count = Invocations("ls-tree"); Check(count == 1 && first.Any(entry => entry.Path == "literal [tree] λ.txt"), "First tree command or literal path missing.");
        IReadOnlyList<GitTreeEntry> second = await f.Repository.GetTreeAsync(sha);
        Check(first.SequenceEqual(second) && Invocations("ls-tree") == count, "A true repeated tree reader launched ls-tree again or changed content.");
        Check(GitCommandCache.Shared.Snapshot().Count > 0, "Successful immutable reads were not retained.");
        GitCommandCache.Shared.Clear();
        Check((await f.Repository.GetTreeAsync(sha)).SequenceEqual(first) && Invocations("ls-tree") == count + 1, "Clear did not make the actual target command miss.");
    }

    private static async Task BlobAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        const string path = "literal [blob] λ.txt", text = "λ\0NUL\r\nCRLF\tTAB\u001b[31mESC\n終🙂\n";
        byte[] expected = Encoding.UTF8.GetBytes(text); await f.Repository.SetConfigAsync("core.autocrlf", "false");
        File.WriteAllBytes(f.File(path), expected); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("exact blob");
        string sha = await Head(f.Repository); GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        byte[] first = await f.Repository.GetFileBytesAsync(path, sha); Check(first.SequenceEqual(expected) && Invocations("cat-file") == 1, "Real blob bytes were changed.");
        first[0] ^= 0x7f; byte[] second = await f.Repository.GetFileBytesAsync(path, sha);
        Check(second.SequenceEqual(expected) && Invocations("cat-file") == 1 && !ReferenceEquals(first, second), "A caller mutated retained blob bytes or the hit spawned Git.");
        GitCommandCacheEntry retained = GitCommandCache.Shared.Snapshot().Single(entry => entry.StandardOutput == text);
        Check(GitCommandCache.Shared.TryInspect(retained.Id, out GitCommandCacheEntry? inspect) && inspect.StandardOutput == text,
            "The actual retained text projection lost NUL/control/Unicode bytes.");
        second[^1] ^= 0x1; Check((await f.Repository.GetFileBytesAsync(path, sha)).SequenceEqual(expected) && Invocations("cat-file") == 1, "A hit shared its byte array.");
    }

    private static async Task DetailsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string sha = await Head(f.Repository);
        GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        GitCommitDetails first = await f.Repository.GetCommitDetailsAsync(sha, includeNotes: false);
        int objects = Invocations("cat-file"), messages = Invocations("show"), mailmap = Invocations("check-mailmap");
        Check(objects > 0 && messages > 0 && mailmap > 0, "Initial metadata subreads did not actually execute.");
        GitCommitDetails second = await f.Repository.GetCommitDetailsAsync(sha, includeNotes: false);
        Check(first.Hash == second.Hash && first.Message == second.Message && first.Author == second.Author && second.Notes is null,
            "Cached metadata changed semantic fields or invented notes.");
        Check(Invocations("cat-file") == objects && Invocations("show") == messages && Invocations("check-mailmap") > mailmap,
            "Object/no-notes reads missed or live mailmap was incorrectly cached.");
    }

    private static async Task NotesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string sha = await Head(f.Repository);
        Check((await f.Repository.AddNoteAsync(sha, "first note λ\n")).Success, "Seed note failed.");
        GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        GitCommitDetails before = await f.Repository.GetCommitDetailsAsync(sha);
        Check(before.Notes?.Contains("first note λ", StringComparison.Ordinal) == true, "Real notes positive witness absent.");
        Check((await f.Repository.AddNoteAsync(sha, "second note 終\n", replace: true)).Success, "Replace real note failed.");
        int shows = Invocations("show"); GitCommitDetails after = await f.Repository.GetCommitDetailsAsync(sha);
        Check(after.Notes is string notes && notes.Contains("second note 終", StringComparison.Ordinal) && !notes.Contains("first note", StringComparison.Ordinal)
            && Invocations("show") > shows, "A mutable note was retained as immutable commit data.");
    }

    private static async Task LiveHeadAndArgumentsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write("a.txt", "a\n"); f.Write("b.txt", "b\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("two paths");
        string sha = await Head(f.Repository); GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        IReadOnlyList<GitTreeEntry> a = await f.Repository.GetTreeAsync(sha, "a.txt");
        IReadOnlyList<GitTreeEntry> b = await f.Repository.GetTreeAsync(sha, "b.txt");
        Check(a.Single().Path == "a.txt" && b.Single().Path == "b.txt" && Invocations("ls-tree") == 2, "Distinct argv/path inputs collided.");
        await f.Repository.GetTreeAsync(sha, "a.txt"); Check(Invocations("ls-tree") == 2, "Exact argument replay did not hit.");
        IReadOnlyList<GitTreeEntry> oldHead = await f.Repository.GetTreeAsync();
        f.Write("new.txt", "new head\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("advance HEAD");
        IReadOnlyList<GitTreeEntry> newHead = await f.Repository.GetTreeAsync();
        Check(oldHead.All(entry => entry.Path != "new.txt") && newHead.Any(entry => entry.Path == "new.txt"), "Mutable HEAD resolved to an old cached tree.");
    }

    private static async Task RepositoryPartitionAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string sha = await Head(f.Repository);
        GitRepository other = await GitRepository.InitAsync(Path.Combine(f.Root, "other")); await RepositoryFixture.ConfigureAsync(other);
        Check((await other.RunAsync(["fetch", "--no-tags", f.Repository.Path, sha])).Success, "Seed same object in second namespace failed.");
        GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        byte[] a = await f.Repository.GetFileBytesAsync("seed.txt", sha), b = await other.GetFileBytesAsync("seed.txt", sha);
        Check(a.SequenceEqual(b) && Invocations("cat-file") == 2, "Identical immutable arguments crossed repository namespace.");
        await other.GetFileBytesAsync("seed.txt", sha); Check(Invocations("cat-file") == 2, "Second namespace did not retain its own hit.");
    }

    private static async Task EnvironmentPartitionAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string sha = await Head(f.Repository);
        Dictionary<string, string?> baseEnvironment = new(GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git" }).EnvironmentVariables);
        GitRepository In(string value) => new(f.Repository.Path, GitExecutionEnvironment.Capture(new() { Executable = "/usr/bin/git" },
            new Dictionary<string, string?>(baseEnvironment) { ["GITEXTENSIONS_CACHE_CONTEXT_WITNESS"] = value }).CreateRunner());
        GitRepository a = In("A"), b = In("B"); GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        byte[] first = await a.GetFileBytesAsync("seed.txt", sha); await a.GetFileBytesAsync("seed.txt", sha);
        Check(Invocations("cat-file") == 1, "Frozen environment did not hit.");
        Check((await b.GetFileBytesAsync("seed.txt", sha)).SequenceEqual(first) && Invocations("cat-file") == 2, "Different captured environment shared an identity.");
        await b.GetFileBytesAsync("seed.txt", sha); Check(Invocations("cat-file") == 2, "Second environment did not hit its own result.");
    }

    private static async Task StdinPartitionAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        byte[] a = Encoding.UTF8.GetBytes("A\0λ\r\n\t\u001b[0m終🙂"), b = [.. a, 0x42];
        GitBinaryResult first = await RepositoryRead(f.Repository, "Metadata", ["hash-object", "--stdin"], a);
        GitBinaryResult hit = await RepositoryRead(f.Repository, "Metadata", ["hash-object", "--stdin"], a.ToArray());
        Check(first.Success && hit.StandardOutput.SequenceEqual(first.StandardOutput) && Invocations("hash-object") == 1, "Equal exact stdin bytes did not hit the true readonly Git result.");
        GitBinaryResult different = await RepositoryRead(f.Repository, "Metadata", ["hash-object", "--stdin"], b);
        Check(different.Success && !different.StandardOutput.SequenceEqual(first.StandardOutput) && Invocations("hash-object") == 2, "Distinct stdin bytes collided.");
        await RepositoryRead(f.Repository, "Metadata", ["hash-object", "--stdin"], a); Check(Invocations("hash-object") == 2, "Original stdin identity was displaced by different input.");
    }

    private static async Task CopiesAsync()
    {
        GitCommandCache cache = new(); const string text = "α\0NUL\r\nCRLF\tTAB\u001b[31mESC終🙂", error = "stderr\0\r\n\t\u001b終";
        byte[] original = Encoding.UTF8.GetBytes(text), originalError = Encoding.UTF8.GetBytes(error);
        byte[] output = original.ToArray(), stderr = originalError.ToArray(); List<string> warnings = ["warning λ", "warning 終"];
        int calls = 0;
        Task<(GitBinaryResult, bool)> Factory(CancellationToken _) { calls++; return Task.FromResult((new GitBinaryResult(0, output, error) { StandardErrorBytes = stderr, Warnings = warnings }, true)); }
        GitBinaryResult miss = await Read(cache, "copies", Factory);
        miss.StandardOutput[0] = 0; miss.StandardErrorBytes[0] = 0; warnings[0] = "caller changed warning";
        GitBinaryResult hit = await Read(cache, "copies", Factory);
        Check(calls == 1 && hit.StandardOutput.SequenceEqual(original) && hit.StandardErrorBytes.SequenceEqual(originalError)
            && hit.StandardError == error && hit.Warnings.SequenceEqual(new[] { "warning λ", "warning 終" }), "Miss caller or warning owner mutated retained data.");
        hit.StandardOutput[^1] ^= 0x1; hit.StandardErrorBytes[^1] ^= 0x1;
        if (hit.Warnings is IList<string> mutable)
        {
            bool rejected = false; try { mutable[0] = "mutated"; } catch (NotSupportedException) { rejected = true; }
            Check(rejected, "Hit warnings are mutable.");
        }
        GitBinaryResult second = await Read(cache, "copies", Factory);
        Check(calls == 1 && second.StandardOutput.SequenceEqual(original) && second.StandardErrorBytes.SequenceEqual(originalError), "Hit caller mutated shared arrays.");
        GitCommandCacheEntry entry = cache.Snapshot().Single();
        Check(entry.StandardOutput == text && entry.StandardError == error, "Text inspection changed exact UTF8 controls or stderr.");
        IReadOnlyList<GitCommandCacheEntry> snapshot = cache.Snapshot(); cache.Clear();
        Check(snapshot.Single() == entry && cache.Snapshot().Count == 0 && cache.RetainedBytes == 0 && !cache.TryInspect(entry.Id, out _), "Clear mutated an old snapshot or retained bytes/id.");
    }

    private static async Task AdmissionAsync()
    {
        GitCommandCache cache = new(maximumEntryBytes: 4); int calls = 0;
        async Task Twice(string name, Func<CancellationToken, Task<(GitBinaryResult, bool)>> factory)
        { await Read(cache, name, factory); await Read(cache, name, factory); Check(cache.Snapshot().Count == 0 && cache.RetainedBytes == 0, name + " was incorrectly admitted."); }
        await Twice("failure", _ => { calls++; return Task.FromResult((Bytes("bad", 7), true)); });
        await Twice("declined", _ => { calls++; return Task.FromResult((Bytes("ok"), false)); });
        await Twice("oversize-output", _ => { calls++; return Task.FromResult((Bytes("12345"), true)); });
        await Twice("oversize-error", _ => { calls++; return Task.FromResult((new GitBinaryResult(0, [1, 2], "err") { StandardErrorBytes = [3, 4, 5] }, true)); });
        Check(calls == 8, "A non-admitted request skipped a later factory.");
        for (int index = 0; index < 2; index++)
        { await Expect<InvalidOperationException>(() => Read(cache, "throw", _ => throw new InvalidOperationException("factory-fault"))); }
        Check(cache.Snapshot().Count == 0, "A throwing factory left cache content.");
        GitBinaryResult edge = await Put(cache, "edge", "1234");
        Check(edge.Success && cache.Snapshot().Count == 1, "Exact raw entry-byte boundary was rejected.");
    }

    private static async Task CancellationAsync()
    {
        GitCommandCache cache = new(); int calls = 0; using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Expect<OperationCanceledException>(() => Read(cache, "pre", _ => { calls++; return Task.FromResult((Bytes("unused"), true)); }, cancel.Token));
        Check(calls == 0 && cache.Snapshot().Count == 0, "Precancel invoked or admitted the factory.");
        using CancellationTokenSource after = new(); TaskCompletionSource<(GitBinaryResult, bool)> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<GitBinaryResult> operation = Read(cache, "after", _ => { calls++; return pending.Task; }, after.Token);
        after.Cancel(); pending.SetResult((Bytes("completed"), true));
        await Expect<OperationCanceledException>(async () => await operation);
        Check(calls == 1 && cache.Snapshot().Count == 0 && cache.RetainedBytes == 0, "A result returned after cancellation was admitted.");
        await Put(cache, "hit", "stored");
        await Expect<OperationCanceledException>(() => Read(cache, "hit", _ => throw new InvalidOperationException("hit factory"), cancel.Token));
        Check(cache.Snapshot().Single().StandardOutput == "stored", "A cancelled hit removed prior content.");
    }

    private static async Task LruAsync()
    {
        GitCommandCache cache = new(); Check(GitCommandCache.DefaultCapacity == 50, "Windows default LRU capacity differs.");
        for (int index = 0; index < 50; index++) { await Put(cache, "id" + index, "data" + index); }
        IReadOnlyList<GitCommandCacheEntry> initial = cache.Snapshot();
        Check(initial.Count == 50 && initial.Select(entry => entry.Key).SequenceEqual(Enumerable.Range(0, 50).Select(index => "id" + index)), "Insertion order changed.");
        Check(cache.TryInspect(initial[0].Id, out _), "Inspection failed to touch first entry.");
        await Read(cache, "id1", _ => throw new InvalidOperationException("Hit unexpectedly invoked factory."));
        Check(cache.Snapshot().Select(entry => entry.Id).SequenceEqual(initial.Select(entry => entry.Id)), "Inspection/hit reordered the visible list.");
        await Put(cache, "id50", "fifty"); IReadOnlyList<GitCommandCacheEntry> final = cache.Snapshot();
        Check(final.Count == 50 && final.All(entry => entry.Key != "id2") && final.Any(entry => entry.Key == "id0") && final.Any(entry => entry.Key == "id1"), "LRU did not respect inspection and true hit touches.");
        Check(final.Select(entry => entry.Key).SequenceEqual(new[] { "id0", "id1" }.Concat(Enumerable.Range(3, 48).Select(index => "id" + index))), "Eviction changed insertion order.");
        Check(initial.Count == 50 && initial[2].Key == "id2" && !cache.TryInspect(initial[2].Id, out _), "Old snapshot changed or evicted identity remained inspectable.");
        if (initial is IList<GitCommandCacheEntry> mutable)
        {
            bool rejected = false; try { mutable[0] = initial[1]; } catch (NotSupportedException) { rejected = true; }
            Check(rejected, "Public snapshot collection is mutable.");
        }
        long last = final[^1].Id; cache.Clear(); await Put(cache, "new", "new");
        Check(cache.Snapshot().Single().Id > last, "Clear reused an identity.");
    }

    private static async Task ByteBudgetAsync()
    {
        GitCommandCache cache = new(capacity: 50, maximumBytes: 256, maximumEntryBytes: 128);
        for (int index = 0; index < 20; index++) { await Put(cache, index.ToString(), new string('x', 16)); Check(cache.RetainedBytes <= 256, "Retained bytes exceeded its independent budget."); }
        Check(cache.Snapshot().Count > 0 && cache.Snapshot().Count < 20 && cache.Snapshot().Last().Key == "19", "Byte budget did not evict independently of capacity50.");
        GitCommandCache small = new(maximumBytes: 64, maximumEntryBytes: 1024);
        await Read(small, "a", _ => Task.FromResult((new GitBinaryResult(0, [1], "") { Warnings = [new string('w', 128)] }, true)));
        Check(small.Snapshot().Count == 0 && small.RetainedBytes == 0, "Warning text escaped the total retained-byte budget.");
        await Put(small, "b", new string('x', 64)); Check(small.Snapshot().Count == 0, "Decoded text escaped the total retained-byte budget.");
    }

    private static async Task ObserversAsync()
    {
        GitCommandCache cache = new(); List<Task<IReadOnlyList<GitCommandCacheEntry>>> workers = []; bool unlocked = true; int observed = 0;
        void Probe()
        {
            Task<IReadOnlyList<GitCommandCacheEntry>> worker = Task.Run(cache.Snapshot); workers.Add(worker);
            unlocked &= worker.Wait(TimeSpan.FromSeconds(5));
        }
        void Throw() => throw new InvalidOperationException("observer-fault");
        void Observe() { observed++; }
        cache.Changed += Probe; cache.Changed += Throw; cache.Changed += Observe;
        try
        {
            Check((await Put(cache, "unlocked", "real-value")).Success, "Observer changed factory success.");
            cache.Clear(); foreach (Task worker in workers) { await worker.WaitAsync(TimeSpan.FromSeconds(5)); }
            Check(unlocked && observed == 2, "Observer ran under the cache lock or a throwing observer blocked later observers.");
        }
        finally { cache.Changed -= Probe; cache.Changed -= Throw; cache.Changed -= Observe; }
        int entries = 0;
        void Reenter() { if (++entries == 1) { cache.Clear(); } }
        cache.Changed += Reenter;
        try { await Put(cache, "reenter", "value"); Check(entries == 2 && cache.Snapshot().Count == 0, "Reentrant Clear deadlocked or lost its own notification."); }
        finally { cache.Changed -= Reenter; }
    }

    private static async Task ClearInFlightAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string sha = await Head(f.Repository); using ProcessGate gate = new(f, "clear");
        GitCommandCache cache = new(); GitCommandLog.Clear(); Task<GitBinaryResult>? operation = null; int calls = 0;
        async Task<(GitBinaryResult, bool)> Factory(CancellationToken token) { calls++; return (await gate.Run(sha, token), true); }
        try
        {
            operation = Read(cache, "live", Factory); await gate.Ready();
            Check(!operation.IsCompleted && cache.Snapshot().Count == 0, "Factory was not genuinely in flight.");
            GitCommandLogEntry running = GitCommandLog.Snapshot().Single(entry => entry.Command == "git cat-file");
            Check(running.IsRunning && running.ProcessId == gate.ProcessId, "The target read lacked its real running process.");
            cache.Clear(); Check(GitCommandLog.Snapshot().Any(entry => entry.Id == running.Id), "Cache Clear removed the separate journal.");
            GitCommandLog.Clear(); gate.Release(); GitBinaryResult done = await operation.WaitAsync(TimeSpan.FromSeconds(15));
            Check(done.Success && cache.Snapshot().Count == 1 && calls == 1, "Clear prevented Windows-style admission of a successful in-flight read.");
            Check(GitCommandLog.Snapshot().Count == 0, "Cache admission restored a journal entry removed during execution.");
            GitBinaryResult hit = await Read(cache, "live", Factory); Check(hit.StandardOutput.SequenceEqual(done.StandardOutput) && calls == 1, "Readmission did not become a true hit.");
            Check(!LinuxProcessState.IsRunning(gate.ProcessId) && !LinuxProcessState.IsRunning(gate.ChildId), "Read finished before its actual actors retired.");
        }
        finally { gate.Release(); if (operation is not null) { await operation.WaitAsync(TimeSpan.FromSeconds(15)); } }
    }

    private static async Task RealCancellationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); string sha = await Head(f.Repository); using ProcessGate gate = new(f, "cancel");
        GitCommandCache cache = new(); using CancellationTokenSource cancel = new(); Task<GitBinaryResult>? operation = null;
        try
        {
            operation = Read(cache, "cancelled", async token => (await gate.Run(sha, token), true), cancel.Token);
            await gate.Ready(); cancel.Cancel(); await Expect<OperationCanceledException>(async () => await operation);
            Check(!LinuxProcessState.IsRunning(gate.ProcessId) && !LinuxProcessState.IsRunning(gate.ChildId) && cache.Snapshot().Count == 0 && cache.RetainedBytes == 0, "Cancelled actor/result survived the owned read.");
        }
        finally
        {
            cancel.Cancel(); gate.Release();
            if (operation is not null) { try { await operation.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { } }
        }
    }

    private static async Task CompetingMissesAsync()
    {
        GitCommandCache cache = new();
        TaskCompletionSource<(GitBinaryResult, bool)> a = new(TaskCreationOptions.RunContinuationsAsynchronously), b = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<GitBinaryResult> left = Read(cache, "same", _ => a.Task), right = Read(cache, "same", _ => b.Task);
        b.SetResult((Bytes("right"), true)); GitBinaryResult r = await right; IReadOnlyList<GitCommandCacheEntry> before = cache.Snapshot();
        a.SetResult((Bytes("left"), true)); GitBinaryResult l = await left;
        Check(Encoding.UTF8.GetString(l.StandardOutput) == "left" && Encoding.UTF8.GetString(r.StandardOutput) == "right", "Concurrent caller results were swapped.");
        Check(cache.Snapshot().Count == 1 && cache.Snapshot().Single().Id == before.Single().Id
            && cache.Snapshot().Single().StandardOutput == "left" && before.Single().StandardOutput == "right",
            "The last completed admission changed its stable identity, lost its output or mutated the prior snapshot.");
        GitBinaryResult hit = await Read(cache, "same", _ => throw new InvalidOperationException("Concurrent admission absent."));
        Check(Encoding.UTF8.GetString(hit.StandardOutput) == cache.Snapshot().Single().StandardOutput, "Retained entry text and bytes came from different concurrent results.");
    }

    private static async Task UncachedPathsAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); GitCommandCache.Shared.Clear(); GitCommandLog.Clear();
        await f.Repository.GetStatusAsync(); f.Write("new.txt", "working tree\n");
        GitStatus status = await f.Repository.GetStatusAsync();
        Check(Invocations("status") == 2 && status.Changes.Any(change => change.Path == "new.txt"), "A live worktree status was cached.");
        int recorded = GitCommandLog.Snapshot().Count;
        await new GitRunner().RunAsync(f.Repository.Path, ["--version"]); await new GitRunner().RunAsync(f.Repository.Path, ["--version"]);
        Check(GitCommandLog.Snapshot().Count == recorded + 2, "Direct runner calls did not both launch their real processes.");
        Check(GitCommandCache.Shared.Snapshot().Count == 0, "The general runner or live status entered the immutable cache.");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private sealed class ProcessGate : IDisposable
    {
        private readonly RepositoryFixture _fixture;
        private readonly string _wrapper, _marker, _release;
        internal int ProcessId { get; private set; }
        internal int ChildId { get; private set; }
        internal ProcessGate(RepositoryFixture fixture, string name)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This native cache fixture requires Linux."); }
            _fixture = fixture; _wrapper = Path.Combine(fixture.Root, "cache-gate-" + name); _marker = _wrapper + ".pid"; _release = _wrapper + ".release";
            File.WriteAllText(_wrapper, "#!/bin/sh\nsleep 60 &\nchild=$!\nprintf '%s %s\\n' \"$$\" \"$child\" > " + Quote(_marker + ".tmp") + "\nmv " + Quote(_marker + ".tmp") + " " + Quote(_marker)
                + "\nwhile test ! -f " + Quote(_release) + "; do sleep 0.02; done\nkill \"$child\" 2>/dev/null || :\nwait \"$child\" 2>/dev/null || :\nexec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(_wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        internal Task<GitBinaryResult> Run(string sha, CancellationToken token) => new GitRunner(_wrapper).RunBinaryAsync(_fixture.Repository.Path, ["cat-file", "blob", sha + ":seed.txt"], token);
        internal async Task Ready()
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (File.Exists(_marker))
                {
                    string[] values = File.ReadAllText(_marker).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (values.Length == 2 && int.TryParse(values[0], out int pid) && int.TryParse(values[1], out int child)
                        && GitCommandLog.Snapshot().Any(entry => entry.IsRunning && entry.ProcessId == pid))
                    { ProcessId = pid; ChildId = child; Check(LinuxProcessState.IsRunning(pid) && LinuxProcessState.IsRunning(child), "Target actors not live at the actual launch gate."); return; }
                }
                await Task.Delay(10);
            }
            throw new InvalidOperationException("Actual cache Git process did not reach its private gate.");
        }
        internal void Release() => File.WriteAllText(_release, "release\n");
        public void Dispose() => Release();
    }
}
