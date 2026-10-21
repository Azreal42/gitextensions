using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using GitExtensions.Linux;

internal static class GitLogStreamingScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("stream graph incrementally parses every UTF8/NUL split and retains independent batches", IncrementalParserAsync),
        ("stream graph emits first100 then requested batches and flushes large complete messages by bytes", ParserBatchPolicyAsync),
        ("stream graph rejects truncated records identities and dates rather than returning EOF", InvalidParserAsync),
        ("stream graph projects nonUTF8 message bytes like the captured reader and warns without false literals", InvalidTextBytesAsync),
        ("stream graph maximum is independent of legacy Count and retains native branch overrides", LimitsAsync),
        ("stream graph preserves native filters rewritten parents sorting boundary and literal paths", QueryParityAsync),
        ("stream graph preserves real large multiline UTF8 commit messages", LargeMessageAsync),
        ("stream graph publishes before native EOF and awaits its real retained callback", BackpressureAsync),
        ("stream graph late Git failure remains failure after provisional batches and actual drain", LateFailureAsync),
        ("stream graph incomplete native EOF fails after actual drain without flushing pending rows", TruncatedNativeAsync),
        ("stream graph cancellation waits for the retained callback and retires PID plus start", CancellationAsync),
        ("stream graph propagates the exact sink fault after actual native drain", SinkFailureAsync),
        ("stream graph validates safe query boundaries and keeps unborn current and bare histories read-only", ValidationAndRepositoryKindsAsync)
    ];

    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static string Signature(IEnumerable<GitCommit> commits) => string.Join('\0', commits.Select(commit =>
        string.Join('\0', commit.Hash, string.Join(' ', commit.Parents), commit.AuthorName, commit.AuthorEmail,
            commit.AuthoredAt.ToString("O", CultureInfo.InvariantCulture), commit.Subject, commit.Body, commit.FullMessage)));

    private static string Record(string hash, string parents = "", string author = "Auteur éλ🌍", string subject = "Sujet\nligne", string body = "Corps\nλ🌍\n", string? full = null)
        => string.Join('\0', hash, parents, author, "auteur@example.invalid", "2024-01-02T03:04:05+02:00", subject, body, full ?? subject + "\n\n" + body) + '\0';

    private static async Task IncrementalParserAsync()
    {
        string a = new('a', 40), b = new('b', 64);
        byte[] bytes = Encoding.UTF8.GetBytes(Record(a, b) + Record(b, full: "Dernière\n\n🌍éλ\n"));
        string? expected = null;
        for (int split = 0; split <= bytes.Length; split++)
        {
            List<IReadOnlyList<GitCommit>> batches = [];
            Parser parser = new(1, (batch, _) => { batches.Add(batch); return ValueTask.CompletedTask; });
            await parser.ReadAsync(bytes.AsMemory(0, split)); await parser.ReadAsync(bytes.AsMemory(split)); await parser.CompleteAsync();
            Check(parser.Count == 2 && batches.Count == 2, "A valid split lost a complete NUL record: " + split);
            string actual = Signature(batches.SelectMany(batch => batch)); expected ??= actual;
            Check(actual == expected && batches[0][0].Hash == a && batches[0][0].Parents.Single() == b
                && batches[1][0].FullMessage == "Dernière\n\n🌍éλ\n", "UTF8 split or subsequent parse changed a retained batch: " + split);
        }
        List<GitCommit> everyByte = [];
        Parser bytewise = new(10, (batch, _) => { everyByte.AddRange(batch); return ValueTask.CompletedTask; });
        for (int index = 0; index < bytes.Length; index++) { await bytewise.ReadAsync(bytes.AsMemory(index, 1)); }
        await bytewise.CompleteAsync(); Check(Signature(everyByte) == expected, "One-byte reads changed the canonical fields.");
    }

    private static async Task ParserBatchPolicyAsync()
    {
        List<int> counts = [];
        Parser parser = new(1000, (batch, _) => { counts.Add(batch.Count); return ValueTask.CompletedTask; });
        byte[] small = Encoding.UTF8.GetBytes(Record(new string('a', 40), author: "A", subject: "S", body: "B", full: "S\nB\n"));
        for (int index = 0; index < 205; index++) { await parser.ReadAsync(small); }
        Check(counts.SequenceEqual([100]) && parser.Count == 100, "The first batch must arrive before EOF rather than wait for1000 records.");
        await parser.CompleteAsync(); Check(counts.SequenceEqual([100, 105]) && parser.Count == 205, "The requested following batch/final flush changed.");
        string huge = string.Concat(Enumerable.Repeat("🌍λ é multiline\n", 50000));
        byte[] large = Encoding.UTF8.GetBytes(Record(new string('b', 40), body: huge, full: "Subject\n\n" + huge));
        List<GitCommit> largeRows = [];
        Parser byBytes = new(1000, (batch, _) => { largeRows.AddRange(batch); return ValueTask.CompletedTask; });
        for (int offset = 0; offset < large.Length; offset += 113) { await byBytes.ReadAsync(large.AsMemory(offset, Math.Min(113, large.Length - offset))); }
        Check(largeRows.Count == 1 && largeRows[0].Body == huge, "A complete message exceeding the batch text threshold must be delivered before EOF, without truncation.");
        await byBytes.CompleteAsync(); Check(byBytes.Count == 1, "The final flush duplicated the large record.");
    }

    private static async Task InvalidParserAsync()
    {
        byte[] valid = Encoding.UTF8.GetBytes(Record(new string('a', 40)));
        foreach (byte[] broken in new[]
        {
            valid[..^1], valid[..(Array.IndexOf(valid, (byte)0) + 1)],
            Encoding.UTF8.GetBytes(Record("wrong-id")),
            Encoding.UTF8.GetBytes(Record(new string('a', 40), "wrong-parent")),
            Encoding.UTF8.GetBytes(Record(new string('a', 40)).Replace("2024-01-02T03:04:05+02:00", "not-a-date", StringComparison.Ordinal))
        })
        {
            List<GitCommit> prefix = [];
            Parser parser = new(1, (batch, _) => { prefix.AddRange(batch); return ValueTask.CompletedTask; });
            await parser.ReadAsync(valid);
            await ThrowsAsync<InvalidDataException>(async () => { await parser.ReadAsync(broken); await parser.CompleteAsync(); });
            Check(prefix.Count == 1 && prefix[0].Hash == new string('a', 40), "Invalid data must not invent another completed record or retract the provisional valid prefix.");
        }
        Parser empty = new(1, (_, _) => throw new InvalidOperationException("An empty stream has no batch."));
        await empty.CompleteAsync(); Check(empty.Count == 0, "Empty canonical output is a valid empty traversal.");
    }

    private static async Task InvalidTextBytesAsync()
    {
        // Compare against the existing incremental decoder for every split, including pending
        // incomplete bytes immediately before a NUL. Invalid text is readable; invalid framing is not.
        string[] fields = Record(new string('a', 40)).Split('\0');
        List<byte> raw = [];
        for (int field = 0; field < 8; field++)
        {
            raw.AddRange(Encoding.UTF8.GetBytes(fields[field]));
            if (field is 2 or 5 or 6 or 7) { raw.AddRange(new byte[] { 0xff, 0xc3, 0xa9, 0xc3 }); }
            raw.Add(0);
        }
        byte[] bytes = raw.ToArray();
        GitProcessTextDecoder oracle = new(); string expected = oracle.Append(bytes, flush: true);
        string[] projected = expected.Split('\0');
        for (int split = 0; split <= bytes.Length; split++)
        {
            List<GitCommit> rows = [];
            Parser parser = new(1, (batch, _) => { rows.AddRange(batch); return ValueTask.CompletedTask; });
            await parser.ReadAsync(bytes.AsMemory(0, split)); await parser.ReadAsync(bytes.AsMemory(split)); await parser.CompleteAsync();
            Check(rows.Count == 1 && rows[0].AuthorName == projected[2] && rows[0].Subject == projected[5]
                && rows[0].Body == projected[6] && rows[0].FullMessage == projected[7], "Malformed UTF8 text did not match the existing lossless projection: " + split);
        }
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync();
        // Replaying this byte fixture through both public readers exercises GitOutputText versus
        // the shared incremental decoder without asking Git itself to normalize a corrupt message.
        using NativeGate gate = await NativeGate.CreateAsync(f, recordsBeforePause: 1,
            suppliedOutput: bytes.Concat(Encoding.UTF8.GetBytes(Record(new string('b', 40), subject: "literal \\xFF", body: "literal \\xC3"))).ToArray());
        await gate.ReleaseAsync();
        IReadOnlyList<GitCommit> captured = await gate.Repository.GetRevisionGraphAsync(new());
        var streamed = await Read(gate.Repository, new(), 0, 1);
        Check(Signature(streamed.Rows) == Signature(captured) && streamed.Result.ProcessResult?.Warnings.Any(warning => warning.Contains("UTF-8", StringComparison.Ordinal)) == true,
            "The stream rejected/changed malformed text accepted by the captured reader or lost the explicit decoding warning.");
        byte[] literal = Encoding.UTF8.GetBytes(Record(new string('a', 40), author: "literal \\xFF") + Record(new string('b', 40)));
        using NativeGate clean = await NativeGate.CreateAsync(f, recordsBeforePause: 1, suppliedOutput: literal); await clean.ReleaseAsync();
        var literalRead = await Read(clean.Repository, new(), 0, 1);
        Check(literalRead.Result.ProcessResult?.Warnings.Any(warning => warning.Contains("UTF-8", StringComparison.Ordinal)) == false,
            "A literal backslash-x sequence is valid UTF8 and must not manufacture a decoding warning.");
    }

    private static async Task LimitsAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = Snapshot(f.Repository.Path);
        GitLogQuery query = new(Count: 1);
        IReadOnlyList<GitCommit> captured = await f.Repository.GetLogAsync(query);
        Check(captured.Count == 1, "The legacy reader must still own Count.");
        var limited = await Read(f.Repository, query, maximum: 2, batchSize: 1);
        Check(limited.Result.CommitCount == 2 && limited.Rows.Count == 2 && limited.Batches.SequenceEqual([1, 1]), "Batch size or query.Count became the streaming maximum.");
        var all = await Read(f.Repository, query with { Count = 0 }, maximum: 0, batchSize: 2);
        string[] native = Hashes((await Git(f.Repository, "log", "--format=%H", "--all", "--")).StandardOutput);
        Check(all.Rows.Select(commit => commit.Hash).SequenceEqual(native) && all.Result.CommitCount == native.Length,
            "Zero maximum must omit --max-count and Count must remain irrelevant to this API.");
        GitLogQuery overridden = new(Count: 1) { BranchFilter = "--max-count=2 main" };
        Check((await f.Repository.GetRevisionGraphAsync(overridden)).Count == 2, "The old branch override contract was narrowed.");
        var nativeOverride = await Read(f.Repository, overridden, maximum: 1, batchSize: 1);
        Check(nativeOverride.Rows.Count == 2 && nativeOverride.Result.CommitCount == 2, "The streaming reader silently clipped a valid native override or added a second maximum.");
        Check(Snapshot(f.Repository.Path) == before, "Reading limits/branch overrides changed repository bytes.");
    }

    private static async Task QueryParityAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = Snapshot(f.Repository.Path);
        GitLogQuery[] queries =
        [
            new(), new(All: false), new(Revision: "HEAD", All: false, Skip: 1),
            new(Path: f.LiteralPath, Author: "Linux Test", Committer: "Linux Test", Message: "keep", IgnoreCase: true),
            new(Path: f.LiteralPath, DiffContent: "tip", FullHistory: true, SimplifyMerges: true),
            new(Since: DateTimeOffset.Parse("2002-01-01T00:00:00Z", CultureInfo.InvariantCulture), Until: DateTimeOffset.Parse("2010-01-01T00:00:00Z", CultureInfo.InvariantCulture)),
            new(FirstParent: true), new(HideMergeCommits: true), new(Reflog: true), new(SimplifyByDecoration: true),
            new GitLogQuery { BranchFilter = "side", IncludeStashes = false, IncludeSessionRefs = false, IncludeLatestStashInBranchFilter = false },
            new GitLogQuery(Revision: "HEAD", All: false) { LastRevisionToDisplayHash = f.FirstKept },
            new GitLogQuery { BranchFilter = "--reverse --max-count=3 main" }
        ];
        foreach (GitLogQuery query in queries.Concat(Enum.GetValues<GitLogSort>().Select(sort => new GitLogQuery { Sort = sort })))
        {
            IReadOnlyList<GitCommit> expected = await f.Repository.GetRevisionGraphAsync(query with { Count = 1000 });
            var actual = await Read(f.Repository, query with { Count = 1 }, maximum: 1000, batchSize: 2);
            Check(Signature(actual.Rows) == Signature(expected), "A streaming query diverged from the existing graph contract: " + query);
        }
        GitLogQuery path = new(Path: f.LiteralPath, Message: "Keep", Author: "Linux Test");
        var filtered = await Read(f.Repository, path, maximum: 1000, batchSize: 1);
        string native = (await Git(f.Repository, "log", "--parents", "--format=%H %P", "--fixed-strings", "--author=Linux Test", "--grep=Keep", "--all", "--", f.LiteralPath)).StandardOutput;
        string[] expectedEdges = native.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd(' ')).ToArray();
        Check(filtered.Rows.Select(commit => commit.Hash + (commit.Parents.Count == 0 ? "" : " " + string.Join(' ', commit.Parents))).SequenceEqual(expectedEdges),
            "The independent native literal-path graph disagrees with streamed parent rewriting.");
        Check(filtered.Rows.Single(commit => commit.Hash == f.Tip).Parents.Contains(f.FirstKept)
            && !filtered.Rows.Single(commit => commit.Hash == f.Tip).Parents.Contains(f.Hidden), "The fixture must expose the rewritten parent, not merely compare two readers with identical raw parents.");
        Check(Snapshot(f.Repository.Path) == before, "Streaming filters/native oracle changed config/index/worktree/object bytes.");
    }

    private static async Task LargeMessageAsync()
    {
        using Fixture f = await Fixture.CreateAsync();
        await Git(f.Repository, "config", "user.name", "Auteur éλ🌍");
        string message = "Sujet éλ🌍\n\n" + string.Concat(Enumerable.Repeat("grande ligne 🌍éλ\ttexte\n", 100000));
        f.Owner.Write("large-message.txt", "large\n"); await f.Repository.StageAllAsync();
        GitResult commit = await new GitRunner().RunAsync(f.Repository.Path, ["commit", "-F", "-"], standardInput: message);
        Check(commit.Success, commit.StandardError); string before = Snapshot(f.Repository.Path);
        GitCommit expected = (await f.Repository.GetLogAsync(new GitLogQuery(Count: 1))).Single();
        var actual = await Read(f.Repository, new(Count: 1), maximum: 1, batchSize: 1000);
        Check(actual.Rows.Count == 1 && Signature(actual.Rows) == Signature([expected]) && actual.Rows[0].FullMessage == expected.FullMessage
            && actual.Rows[0].AuthorName == "Auteur éλ🌍", "A real large/multiline/UTF8 commit was normalized, truncated or split into fake records.");
        Check(Snapshot(f.Repository.Path) == before, "The real large-message reader changed repository bytes.");
    }

    private static async Task BackpressureAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); using NativeGate gate = await NativeGate.CreateAsync(f, recordsBeforePause: 1);
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously), acknowledge = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new(); List<IReadOnlyList<GitCommit>> batches = []; (int Pid, string Start)? identity = null;
        Task<GitRevisionGraphStreamResult> work = gate.Repository.StreamRevisionGraphAsync(new(Count: 1), 0, 1, async (batch, _) =>
        { batches.Add(batch); if (batches.Count == 1) { identity = gate.Identity; first.TrySetResult(); await acknowledge.Task; } }, cancellation.Token);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(15)); Check(identity is not null && Alive(identity.Value), "The first real batch was not observed while its native producer was alive.");
            Check(!work.IsCompleted && batches.Count == 1, "A first batch was buffered until EOF or was not actually awaited.");
            string retained = Signature(batches[0]); await gate.ReleaseAsync(); await Until(() => File.Exists(gate.ProducerFinished));
            Check(!work.IsCompleted && batches.Count == 1, "Producer exit must not abandon the callback or execute later callbacks concurrently.");
            acknowledge.TrySetResult(); GitRevisionGraphStreamResult result = await work.WaitAsync(TimeSpan.FromSeconds(15));
            Check(result.CommitCount == f.Total && Signature(batches[0]) == retained && batches.SelectMany(batch => batch).Count() == f.Total,
                "The awaited callback lost rows or the parser later mutated its retained batch.");
            Check(result.ProcessResult?.Success == true && result.ProcessResult.StandardOutputByteCount > 0 && !Alive(identity!.Value), "Success did not include complete native stdout and strict PID/start retirement.");
        }
        finally { cancellation.Cancel(); acknowledge.TrySetResult(); await gate.ReleaseAsync(); await Observe(work); }
    }

    private static async Task LateFailureAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); using NativeGate gate = await NativeGate.CreateAsync(f, recordsBeforePause: 4, exitCode: 17);
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously); List<GitCommit> rows = []; (int Pid, string Start)? identity = null;
        using CancellationTokenSource cancellation = new();
        Task<GitRevisionGraphStreamResult> work = gate.Repository.StreamRevisionGraphAsync(new(), 0, 4, (batch, _) =>
        { rows.AddRange(batch); identity ??= gate.Identity; first.TrySetResult(); return ValueTask.CompletedTask; }, cancellation.Token);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(15)); Check(identity is not null && Alive(identity.Value) && !work.IsCompleted, "The provisional prefix did not precede the real native EOF.");
            await gate.ReleaseAsync(); GitCommandException error = await ThrowsAsync<GitCommandException>(async () => await work);
            Check(error.Result.ExitCode == 17 && error.Result.StandardOutput.Length == 0 && error.Result.StandardError.Contains("late failure", StringComparison.Ordinal),
                "The late Git error became success, retained all stdout or lost bounded diagnostics.");
            Check(rows.Count == 4 && !Alive(identity!.Value), "A late failure flushed the two pending rows or returned before strict native retirement.");
        }
        finally { cancellation.Cancel(); await gate.ReleaseAsync(); await Observe(work); }
    }

    private static async Task TruncatedNativeAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); using NativeGate gate = await NativeGate.CreateAsync(f, recordsBeforePause: 4, truncateFinalNul: true);
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously); int delivered = 0; (int Pid, string Start)? identity = null;
        using CancellationTokenSource cancellation = new();
        Task<GitRevisionGraphStreamResult> work = gate.Repository.StreamRevisionGraphAsync(new(), 0, 4, (batch, _) =>
        { delivered += batch.Count; identity ??= gate.Identity; first.TrySetResult(); return ValueTask.CompletedTask; }, cancellation.Token);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(15)); Check(identity is not null && Alive(identity.Value), "The native protocol fixture never owned a live producer.");
            await gate.ReleaseAsync(); await ThrowsAsync<InvalidDataException>(async () => await work);
            Check(delivered == 4 && !Alive(identity!.Value), "A successful process with a partial protocol tail was mistaken for EOF or flushed its pending rows.");
        }
        finally { cancellation.Cancel(); await gate.ReleaseAsync(); await Observe(work); }
    }

    private static async Task CancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); using NativeGate gate = await NativeGate.CreateAsync(f, recordsBeforePause: 1);
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously), releaseSink = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new(); int callbacks = 0; (int Pid, string Start)? identity = null;
        Task<GitRevisionGraphStreamResult> work = gate.Repository.StreamRevisionGraphAsync(new(), 0, 1, async (_, _) =>
        { callbacks++; identity = gate.Identity; first.TrySetResult(); await releaseSink.Task; }, cancellation.Token);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(15)); Check(identity is not null && Alive(identity.Value), "Cancellation never reached an actual native producer.");
            cancellation.Cancel(); Check(!work.IsCompleted, "Cancel abandoned a callback which has not actually completed.");
            releaseSink.TrySetResult(); await ThrowsAsync<OperationCanceledException>(async () => await work);
            Check(callbacks == 1 && !Alive(identity!.Value), "Cancellation admitted another callback or returned while the PID/start still existed, including a zombie.");
        }
        finally { cancellation.Cancel(); releaseSink.TrySetResult(); await gate.ReleaseAsync(); await Observe(work); }
    }

    private static async Task SinkFailureAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); using NativeGate gate = await NativeGate.CreateAsync(f, recordsBeforePause: 1);
        InvalidOperationException expected = new("fixture sink failure"); (int Pid, string Start)? identity = null;
        Task<GitRevisionGraphStreamResult> work = gate.Repository.StreamRevisionGraphAsync(new(), 0, 1, (_, _) =>
        { identity = gate.Identity; throw expected; });
        try
        {
            InvalidOperationException actual = await ThrowsAsync<InvalidOperationException>(async () => await work);
            Check(ReferenceEquals(expected, actual) && identity is not null && !Alive(identity!.Value), "The exact sink failure was replaced or native work was not drained.");
        }
        finally { await gate.ReleaseAsync(); await Observe(work); }
    }

    private static async Task ValidationAndRepositoryKindsAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); string before = Snapshot(f.Repository.Path); string forbidden = Path.Combine(f.Owner.Root, "forbidden-output");
        foreach (GitLogQuery query in new[] { new GitLogQuery(Skip: -1), new GitLogQuery(Revision: "HEAD") { BranchFilter = "main" },
            new GitLogQuery(Since: DateTimeOffset.Parse("2080-01-01T00:00:00Z", CultureInfo.InvariantCulture), Until: DateTimeOffset.Parse("2000-01-01T00:00:00Z", CultureInfo.InvariantCulture)),
            new GitLogQuery { BranchFilter = "--output=" + forbidden + " main" }, new GitLogQuery { BranchFilter = "--format=unsafe main" },
            new GitLogQuery(Path: "bad\0path") })
        { await ThrowsAsync<ArgumentException>(async () => await Read(f.Repository, query, 0, 1)); }
        await ThrowsAsync<ArgumentOutOfRangeException>(async () => await Read(f.Repository, new(), -1, 1));
        await ThrowsAsync<ArgumentOutOfRangeException>(async () => await Read(f.Repository, new(), 0, 0));
        using (CancellationTokenSource token = new())
        {
            token.Cancel(); await ThrowsAsync<OperationCanceledException>(async () => await f.Repository.StreamRevisionGraphAsync(new(), 0, 1, (_, _) => ValueTask.CompletedTask, token.Token));
        }
        Check(!File.Exists(forbidden) && Snapshot(f.Repository.Path) == before, "Invalid history input changed the read-only repository or redirected output.");
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(seed: false);
        var empty = await Read(unborn.Repository, new(All: false), 0, 1);
        Check(empty.Result.CommitCount == 0 && empty.Rows.Count == 0 && empty.Result.ProcessResult is null, "Current unborn scope invented a log/commit or a process result.");
        string bare = Path.Combine(f.Owner.Root, "bare.git"); await Git(f.Repository, "clone", "--bare", f.Repository.Path, bare);
        var bareRead = await Read(new GitRepository(bare), new(), 0, 2);
        Check(bareRead.Result.CommitCount == f.Total && bareRead.Rows.Count == f.Total, "Bare history was rejected or widened into artificial commits.");
    }

    private static async Task<(GitRevisionGraphStreamResult Result, List<GitCommit> Rows, List<int> Batches)> Read(GitRepository repository, GitLogQuery query, int maximum, int batchSize)
    {
        List<GitCommit> rows = []; List<int> batches = [];
        GitRevisionGraphStreamResult result = await repository.StreamRevisionGraphAsync(query, maximum, batchSize,
            (batch, _) => { rows.AddRange(batch); batches.Add(batch.Count); return ValueTask.CompletedTask; });
        Check(result.CommitCount == rows.Count, "The count must describe actual delivered callbacks."); return (result, rows, batches);
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(TimeSpan.FromSeconds(15)); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Observe(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception error) when (error is not TimeoutException) { } }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(10); } Check(condition(), "The owned stream gate did not settle."); }
    private static string[] Hashes(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    private static async Task<GitResult> Git(GitRepository repository, params string[] arguments)
    { GitResult result = await new GitRunner().RunAsync(repository.Path, ["--no-optional-locks", "--literal-pathspecs", .. arguments]); if (!result.Success) { throw new GitCommandException(arguments, result); } return result; }
    private static string Snapshot(string repository) => string.Join('\n', Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(repository, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string? Start(int pid)
    { try { string stat = File.ReadAllText("/proc/" + pid + "/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19]; } catch (IOException) { return null; } }
    private static bool Alive((int Pid, string Start) identity) => Start(identity.Pid) == identity.Start;

    private sealed class Parser
    {
        private readonly object _instance;
        private readonly MethodInfo _read, _complete;
        private readonly PropertyInfo _count;
        internal Parser(int batchSize, Func<IReadOnlyList<GitCommit>, CancellationToken, ValueTask> sink)
        {
            Type type = typeof(GitRepository).Assembly.GetType("GitExtensions.Linux.GitLogStreamParser", throwOnError: true)!;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            _instance = Activator.CreateInstance(type, flags, null, [batchSize, sink], CultureInfo.InvariantCulture)!;
            _read = type.GetMethod("ReadAsync", flags)!; _complete = type.GetMethod("CompleteAsync", flags)!; _count = type.GetProperty("CommitCount", flags)!;
        }
        internal long Count => (long)_count.GetValue(_instance)!;
        internal ValueTask ReadAsync(ReadOnlyMemory<byte> bytes) => Invoke(_read, [bytes, CancellationToken.None]);
        internal ValueTask CompleteAsync() => Invoke(_complete, [CancellationToken.None]);
        private ValueTask Invoke(MethodInfo method, object[] arguments)
        { try { return (ValueTask)method.Invoke(_instance, arguments)!; } catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; } }
    }

    private sealed record Fixture(RepositoryFixture Owner, string FirstKept, string Hidden, string Tip, string LiteralPath, int Total) : IDisposable
    {
        internal GitRepository Repository => Owner.Repository;
        internal static async Task<Fixture> CreateAsync()
        {
            RepositoryFixture f = await RepositoryFixture.CreateAsync(seed: false);
            try
            {
                const string path = "\nliteral [*]?;$()'éλ.txt";
                f.Write(path, "root\n"); await Commit(f, "Keep root", 2001); await Git(f.Repository, "branch", "side");
                f.Write(path, "main\n"); string first = await Commit(f, "Keep alpha", 2002);
                f.Write("noise.txt", "noise\n"); string hidden = await Commit(f, "Hidden middle", 2003);
                f.Write(path, "tip\n"); string tip = await Commit(f, "Keep tip", 2004);
                await Git(f.Repository, "tag", "-a", "Tag/Été", "-m", "Annotated", first);
                await Git(f.Repository, "checkout", "side"); f.Write("side.txt", "side\n"); await Commit(f, "Keep side", 2005);
                await Git(f.Repository, "checkout", "main"); await Git(f.Repository, "merge", "--no-ff", "side", "-m", "Merge selected");
                return new(f, first, hidden, tip, path, 6);
            }
            catch { f.Dispose(); throw; }
        }
        private static async Task<string> Commit(RepositoryFixture f, string message, int year)
        {
            await f.Repository.StageAllAsync(); GitResult commit = await new GitRunner().RunAsync(f.Repository.Path, ["commit", "-qm", message],
                environment: new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = year + "-01-01T12:00:00+00:00", ["GIT_COMMITTER_DATE"] = year + "-01-01T12:00:00+00:00" });
            Check(commit.Success, commit.StandardError); return (await Git(f.Repository, "rev-parse", "HEAD")).StandardOutput.Trim();
        }
        public void Dispose() => Owner.Dispose();
    }

    private sealed class NativeGate(string directory, GitRepository repository) : IDisposable
    {
        internal GitRepository Repository { get; } = repository;
        internal string ProducerFinished => Path.Combine(directory, "producer-finished");
        internal (int Pid, string Start)? Identity
        {
            get
            {
                string path = Path.Combine(directory, "pid");
                if (!File.Exists(path) || !int.TryParse(File.ReadAllText(path), out int pid) || Start(pid) is not string start) { return null; }
                return (pid, start);
            }
        }
        internal Task ReleaseAsync() => File.WriteAllTextAsync(Path.Combine(directory, "release"), "release\n");
        internal static async Task<NativeGate> CreateAsync(Fixture fixture, int recordsBeforePause, int exitCode = 0, bool truncateFinalNul = false, byte[]? suppliedOutput = null)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private procfs fixtures require Linux."); }
            string directory = Path.Combine(fixture.Owner.Root, "stream-gate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string format = "--format=%H%x00%P%x00%aN%x00%aE%x00%aI%x00%s%x00%b%x00%B";
            GitResult native = await Git(fixture.Repository, "log", "--encoding=UTF-8", "-z", format, "--all", "--");
            byte[] bytes = suppliedOutput ?? native.StandardOutputBytes; int split = 0, separators = 0;
            while (split < bytes.Length && separators < 8 * recordsBeforePause) { if (bytes[split++] == 0) { separators++; } }
            Check(separators == 8 * recordsBeforePause && split < bytes.Length, "The gate must pause before a real remaining native record.");
            string payload = Path.Combine(directory, "native-output"); await File.WriteAllBytesAsync(payload, truncateFinalNul ? bytes[..^1] : bytes);
            string script = Path.Combine(directory, "git");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nstream=no\nfor arg in \"$@\"; do [ \"$arg\" != log ] || stream=yes; done\nif [ \"$stream\" != yes ]; then exec /usr/bin/git \"$@\"; fi\nprintf '%s' \"$$\" > " + Quote(Path.Combine(directory, "pid"))
                + "\nhead -c " + split + " " + Quote(payload) + "\nwhile [ ! -f " + Quote(Path.Combine(directory, "release")) + " ]; do sleep .02; done\ntail -c +" + (split + 1) + " " + Quote(payload)
                + "\nprintf done > " + Quote(Path.Combine(directory, "producer-finished")) + (exitCode == 0 ? "" : "\nprintf '%s' 'fixture late failure' >&2") + "\nexit " + exitCode + "\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new(directory, new GitRepository(fixture.Repository.Path, new GitRunner(script)));
        }
        public void Dispose() { }
    }
}
