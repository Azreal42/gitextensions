using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GitExtensions.Linux;

internal static class FileHistoryStreamingScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("file graph stream retains literal former names rewritten parents and one immutable context", RenamesAndLiteralsAsync),
        ("file graph stream keeps divergent scopes native limits skip overrides and snapshot validation", ScopesAndLimitsAsync),
        ("file graph stream keeps directory empty unborn and bare contexts without false rename discovery", DirectoryAndEmptyAsync),
        ("file graph stream runs one real discovery then one graph and rejects invalid inputs before either", CommandsAndValidationAsync),
        ("file graph stream delivers before EOF and genuinely awaits a retained callback", BackpressureAsync),
        ("file graph stream cancels and drains the exact native rename discovery before graph admission", () => CancelPhaseAsync(discovery: true)),
        ("file graph stream cancels and drains the exact native graph without publishing buffered rows", () => CancelPhaseAsync(discovery: false)),
        ("file graph stream retains cancelled callbacks and exact sink faults until actual native drainage", HeldCancellationAndFaultAsync)
    ];

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
    private static string Signature(IEnumerable<GitCommit> rows) => string.Join('\0', rows.Select(row =>
        string.Join('\0', row.Hash, string.Join(' ', row.Parents), row.AuthorName, row.AuthorEmail,
            row.AuthoredAt.ToString("O", CultureInfo.InvariantCulture), row.Subject, row.Body, row.FullMessage)));
    private static string ContextSignature(GitFileHistoryGraphContext context) => JsonSerializer.Serialize(new
    { context.Paths, Mapping = context.PathByCommit.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray() });

    private static async Task RenamesAndLiteralsAsync()
    {
        foreach ((string oldPath, string path) in new[]
        {
            ("\nold [*]?;$()'éλ.txt", "\nnew [*]?;$()'\"éλ.txt"),
            (new string('b', 40), new string('a', 40))
        })
        {
            using Fixture f = await Fixture.CreateAsync(oldPath, path); string before = Snapshot(f.Repository.Path);
            GitFileHistoryGraph captured = await f.Repository.GetFileHistoryGraphAsync(new(Path: path));
            var actual = await Read(f.Repository, new(Count: 0, Path: path), 0, 1);
            Check(Signature(actual.Rows) == Signature(captured.Commits), "The streaming graph diverged from the captured graph.");
            Check(actual.Rows.Select(row => row.Hash).SequenceEqual([f.Tip, f.Rename, f.Root]), "The file graph widened to the omitted noise commit.");
            Check(actual.Result.Context.Paths.SequenceEqual([path, oldPath])
                && actual.Result.Context.PathByCommit[f.Root] == oldPath && actual.Result.Context.PathByCommit[f.Rename] == path,
                "Literal LF/glob/quote/Unicode/hash-like names or the first name per commit changed.");
            string native = (await Git(f.Repository, "log", "--parents", "--format=%H %P", "--all", "--", path, oldPath)).StandardOutput;
            string[] edges = native.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd(' ')).ToArray();
            Check(actual.Rows.Select(row => (row.Hash + " " + string.Join(' ', row.Parents)).TrimEnd(' ')).SequenceEqual(edges)
                && actual.Rows.Single(row => row.Hash == f.Rename).Parents.SequenceEqual([f.Root]), "Real rewritten graph parents were replaced by raw or chronological parents.");
            Check(((IList<string>)actual.Result.Context.Paths).IsReadOnly && ((IDictionary<string, string>)actual.Result.Context.PathByCommit).IsReadOnly,
                "The prepared names must expose readonly collections before any retained batch.");
            Check(Snapshot(f.Repository.Path) == before, "A file graph read changed repository/index/worktree bytes.");
        }
    }

    private static async Task ScopesAndLimitsAsync()
    {
        using Fixture f = await Fixture.CreateAsync(); await Git(f.Repository, "checkout", "-qb", "side", f.Root);
        f.Owner.Write(f.OldPath, "divergent former-name change\n"); string side = await Commit(f.Owner, "Side change", 2010);
        await Git(f.Repository, "checkout", "main"); string before = Snapshot(f.Repository.Path);
        var all = await Read(f.Repository, new(Path: f.Path), 0, 2);
        Check(all.Rows.Any(row => row.Hash == side) && !all.Result.Context.PathByCommit.ContainsKey(side),
            "All scope must retain a divergent former-name commit without inventing a HEAD-discovery mapping.");
        foreach (GitLogQuery query in new[]
        {
            new GitLogQuery(Path: f.Path, All: false),
            new GitLogQuery(Path: f.Path) { BranchFilter = "side" },
            new GitLogQuery(Path: f.Path, Message: "Future", Author: "Linux Test", IgnoreCase: true),
            new GitLogQuery(Path: f.Path, FullHistory: true, SimplifyMerges: true) { Sort = GitLogSort.Topology }
        })
        {
            GitFileHistoryGraph captured = await f.Repository.GetFileHistoryGraphAsync(query);
            Check(Signature((await Read(f.Repository, query with { Count = -1 }, 0, 1)).Rows) == Signature(captured.Commits), "A native scope/filter/sort changed.");
        }
        var limited = await Read(f.Repository, new(Count: 1, Path: f.Path, Skip: 1), 2, 1);
        Check(limited.Rows.Select(row => row.Hash).SequenceEqual(all.Rows.Skip(1).Take(2).Select(row => row.Hash))
            && limited.Result.Traversal.CommitCount == 2 && limited.Result.Context.Paths.Contains(f.OldPath), "Count, skip or maximum truncated discovery instead of only the graph.");
        var overridden = await Read(f.Repository, new GitLogQuery(Count: 0, Path: f.Path) { BranchFilter = "--max-count=2 main" }, 1, 1);
        Check(overridden.Rows.Count == 2, "A C# hard ceiling erased the accepted native branch-option override.");
        await Throws<ArgumentOutOfRangeException>(() => f.Repository.GetFileHistoryGraphAsync(new(Count: 0, Path: f.Path)));
        var noFollow = await Read(f.Repository, new(Path: f.Path), 0, 1, new(FollowRenames: false, ExactRenames: true));
        Check(noFollow.Result.Context.Paths.SequenceEqual([f.Path]) && noFollow.Result.Context.PathByCommit.Count == 0
            && noFollow.Rows.All(row => row.Hash != f.Root), "No-follow must retain a single literal path; Exact remains inert.");
        Check(Snapshot(f.Repository.Path) == before, "Native scopes, limits or validation changed repository bytes.");
    }

    private static async Task DirectoryAndEmptyAsync()
    {
        using Fixture f = await Fixture.CreateAsync("folder/old.txt", "folder/new.txt");
        f.Owner.Write("elsewhere/outside.txt", "outside\n"); await Commit(f.Owner, "Outside folder", 2010);
        var directory = await Read(f.Repository, new(Path: "folder/"), 0, 1, new(IsDirectory: true));
        Check(directory.Rows.Select(row => row.Hash).SequenceEqual([f.Tip, f.Rename, f.Root])
            && directory.Result.Context.Paths.SequenceEqual(["folder/"]) && directory.Result.Context.PathByCommit.Count == 0,
            "Directory history pretended to follow one file or widened its literal subtree.");
        var empty = await Read(f.Repository, new(Path: "absent/"), 0, 1, new(IsDirectory: true));
        Check(empty.Rows.Count == 0 && empty.Contexts.Count == 0 && empty.Result.Traversal.CommitCount == 0
            && empty.Result.Context.Paths.SequenceEqual(["absent/"]) && empty.Result.Context.PathByCommit.Count == 0,
            "An empty native EOF must still return its prepared context without an empty/fabricated callback.");
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(seed: false);
        var current = await Read(unborn.Repository, new(Path: "new.txt", All: false), 0, 1);
        Check(current.Rows.Count == 0 && current.Contexts.Count == 0 && current.Result.Traversal.ProcessResult is null
            && current.Result.Context.Paths.SequenceEqual(["new.txt"]) && current.Result.Context.PathByCommit.Count == 0,
            "Current unborn scope invented a native graph or lost its empty context.");
        await Git(f.Repository, "checkout", "--orphan", "unborn-with-refs");
        var orphan = await Read(f.Repository, new(Path: f.Path), 0, 1);
        Check(orphan.Rows.Any(row => row.Hash == f.Tip) && orphan.Result.Context.Paths.SequenceEqual([f.Path])
            && orphan.Result.Context.PathByCommit.Count == 0, "Unborn HEAD must not erase other refs or invent former names.");
        string bare = Path.Combine(f.Owner.Root, "bare.git"); await Git(f.Repository, "clone", "--bare", f.Repository.Path, bare);
        var bareRead = await Read(new GitRepository(bare), new(Path: f.Path), 0, 1);
        Check(Signature(bareRead.Rows) == Signature(orphan.Rows), "A bare graph with other refs acquired a working-tree requirement.");
    }

    private static async Task CommandsAndValidationAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); using Gate gate = Gate.Create(f, "none");
        GitLogQuery query = new(Path: f.Path);
        await Throws<ArgumentNullException>(() => gate.Repository.StreamRevisionGraphAsync(null!, 0, 1, (_, _) => ValueTask.CompletedTask));
        await Throws<ArgumentNullException>(() => gate.Repository.StreamFileHistoryGraphAsync(null!, 0, 1, (_, _, _) => ValueTask.CompletedTask));
        await Throws<ArgumentNullException>(() => gate.Repository.StreamFileHistoryGraphAsync(query, 0, 1, null!));
        await Throws<ArgumentOutOfRangeException>(() => gate.Repository.StreamFileHistoryGraphAsync(query, -1, 1, (_, _, _) => ValueTask.CompletedTask));
        await Throws<ArgumentOutOfRangeException>(() => gate.Repository.StreamFileHistoryGraphAsync(query, 0, 0, (_, _, _) => ValueTask.CompletedTask));
        await Throws<ArgumentOutOfRangeException>(() => gate.Repository.StreamFileHistoryGraphAsync(query, 0, -1, (_, _, _) => ValueTask.CompletedTask));
        await Throws<ArgumentException>(() => gate.Repository.StreamFileHistoryGraphAsync(new(Path: "bad\0path"), 0, 1, (_, _, _) => ValueTask.CompletedTask));
        using (CancellationTokenSource cancelled = new())
        {
            cancelled.Cancel(); await Throws<OperationCanceledException>(() => gate.Repository.StreamFileHistoryGraphAsync(query, 0, 1,
                (_, _, _) => ValueTask.CompletedTask, cancellationToken: cancelled.Token));
        }
        Check(gate.Commands.Length == 0, "Invalid/pre-cancelled stream input admitted a native preparatory process.");
        f.Owner.Write(f.Path, "dirty unstaged\n"); f.Owner.Write("untracked.txt", "untracked\n"); string before = Snapshot(f.Repository.Path);
        var actual = await Read(gate.Repository, query, 0, 1);
        string[][] logs = gate.Commands.Where(args => args.Contains("log")).ToArray();
        Check(logs.Length == 2 && logs[0].Contains("--follow") && logs[1].Contains("--parents")
            && logs.All(args => args.Contains("--no-optional-locks") && args.Contains("--literal-pathspecs")),
            "Streaming repeated discovery per batch or bypassed the readonly canonical graph command.");
        Check(logs.All(args => !args.Any(arg => arg.StartsWith("--max-count=", StringComparison.Ordinal))),
            "Zero maximum must not apply Count or a hidden cap to discovery or the graph.");
        Check(actual.Rows.Count == 3 && Snapshot(f.Repository.Path) == before, "Streaming commands changed index/worktree/object bytes.");
    }

    private static async Task BackpressureAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); using Gate gate = Gate.Create(f, "graph");
        using CancellationTokenSource cancellation = new(); TaskCompletionSource entered = Signal(), acknowledge = Signal();
        List<IReadOnlyList<GitCommit>> batches = []; GitFileHistoryGraphContext? context = null; string? retained = null;
        Task<GitFileHistoryGraphStreamResult> work = gate.Repository.StreamFileHistoryGraphAsync(new(Path: f.Path), 0, 1, async (names, rows, _) =>
        {
            Check(context is null || ReferenceEquals(context, names), "A later callback replaced the prepared context.");
            context ??= names; batches.Add(rows);
            if (batches.Count == 1) { retained = ContextSignature(names) + Signature(rows); entered.TrySetResult(); await acknowledge.Task; }
        }, cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Limit); await Until(() => gate.Paused);
            Check(gate.Identities.All(Present) && !work.IsCompleted, "The first real batch did not precede the native EOF.");
            Check(context!.PathByCommit[f.Root] == f.OldPath, "The first selectable batch did not already carry its former-name mapping.");
            gate.Release(); await Until(() => gate.Finished);
            Check(!work.IsCompleted && batches.Count == 1, "Native exit abandoned the retained callback or admitted concurrent batches.");
            acknowledge.TrySetResult(); GitFileHistoryGraphStreamResult result = await work.WaitAsync(Limit);
            Check(ReferenceEquals(result.Context, context) && retained == ContextSignature(context) + Signature(batches[0])
                && result.Traversal.CommitCount == 3 && batches.Sum(batch => batch.Count) == 3, "Retained context/batch mutation or row loss occurred.");
            Check(result.Traversal.ProcessResult?.Success == true && gate.Identities.All(Retired), "Success returned before exact native identities were retired.");
        }
        finally { cancellation.Cancel(); acknowledge.TrySetResult(); gate.Release(); await Observe(work); }
    }

    private static async Task CancelPhaseAsync(bool discovery)
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using Fixture f = await Fixture.CreateAsync(); using Gate gate = Gate.Create(f, discovery ? "discovery" : "graph");
        using CancellationTokenSource cancellation = new(); int callbacks = 0; string before = Snapshot(f.Repository.Path);
        Task<GitFileHistoryGraphStreamResult> work = gate.Repository.StreamFileHistoryGraphAsync(new(Path: f.Path), 0, 100,
            (_, _, _) => { callbacks++; return ValueTask.CompletedTask; }, cancellationToken: cancellation.Token);
        try
        {
            await Until(() => gate.Paused); Check(gate.Identities.All(Present) && !work.IsCompleted, "The actual native phase was not held.");
            cancellation.Cancel(); await Throws<OperationCanceledException>(async () => await work);
            Check(callbacks == 0 && gate.Identities.All(Retired), "Cancellation returned before native retirement or flushed a provisional short batch as EOF.");
            Check(gate.Commands.Count(args => args.Contains("log")) == (discovery ? 1 : 2), "Cancellation admitted a later graph or repeated the phase.");
            Check(Snapshot(f.Repository.Path) == before, "Cancelled file graph changed repository bytes.");
        }
        finally { cancellation.Cancel(); gate.Release(); await Observe(work); }
    }

    private static async Task HeldCancellationAndFaultAsync()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        foreach (bool cancel in new[] { true, false })
        {
            using Fixture f = await Fixture.CreateAsync(); using Gate gate = Gate.Create(f, "graph");
            using CancellationTokenSource cancellation = new(); TaskCompletionSource entered = Signal(), acknowledged = Signal(), cancellationObserved = Signal();
            InvalidOperationException fault = new("Exact file graph callback fault"); int callbacks = 0;
            string before = Snapshot(f.Repository.Path);
            Task<GitFileHistoryGraphStreamResult> work = gate.Repository.StreamFileHistoryGraphAsync(new(Path: f.Path), 0, 1, async (names, _, token) =>
            {
                callbacks++; Check(names.PathByCommit[f.Root] == f.OldPath, "Callback fault/cancel lost the prepared names.");
                using CancellationTokenRegistration registration = token.Register(() => cancellationObserved.TrySetResult());
                entered.TrySetResult(); await acknowledged.Task;
                if (!cancel) { throw fault; }
                token.ThrowIfCancellationRequested();
            }, cancellationToken: cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(Limit); await Until(() => gate.Paused);
                if (cancel)
                {
                    cancellation.Cancel(); await cancellationObserved.Task.WaitAsync(Limit);
                    Check(!work.IsCompleted && callbacks == 1, "Cancellation abandoned a callback which has not actually returned.");
                    acknowledged.TrySetResult(); await Throws<OperationCanceledException>(async () => await work);
                }
                else
                {
                    Check(!work.IsCompleted && callbacks == 1, "The callback was not genuinely retained.");
                    acknowledged.TrySetResult(); InvalidOperationException actual = await Throws<InvalidOperationException>(async () => await work);
                    Check(ReferenceEquals(actual, fault), "The exact sink fault was replaced while draining native work.");
                }
                Check(callbacks == 1 && gate.Identities.All(Retired), "Failure/cancellation returned before exact native retirement or admitted a second callback.");
                Check(Snapshot(f.Repository.Path) == before, "A retained cancelled/faulted callback changed repository bytes.");
            }
            finally { cancellation.Cancel(); acknowledged.TrySetResult(); gate.Release(); await Observe(work); }
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<(GitFileHistoryGraphStreamResult Result, List<GitCommit> Rows, List<GitFileHistoryGraphContext> Contexts)> Read(
        GitRepository repository, GitLogQuery query, int maximum, int batchSize, GitFileHistoryGraphOptions? options = null)
    {
        List<GitCommit> rows = []; List<GitFileHistoryGraphContext> contexts = [];
        GitFileHistoryGraphStreamResult result = await repository.StreamFileHistoryGraphAsync(query, maximum, batchSize, (names, batch, _) =>
        { contexts.Add(names); rows.AddRange(batch); return ValueTask.CompletedTask; }, options);
        Check(result.Traversal.CommitCount == rows.Count && contexts.All(names => ReferenceEquals(names, result.Context)), "Context identity or delivered count changed.");
        return (result, rows, contexts);
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Limit); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Observe(Task work)
    { try { await work.WaitAsync(Limit); } catch (Exception error) when (error is not TimeoutException) { } }
    private static async Task Until(Func<bool> condition)
    { Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < Limit) { await Task.Delay(10); } Check(condition(), "The real file graph gate did not settle."); }
    private static async Task<GitResult> Git(GitRepository repository, params string[] args)
    {
        GitResult result = await new GitRunner("/usr/bin/git").RunAsync(repository.Path, ["--no-optional-locks", "--literal-pathspecs", .. args]);
        if (!result.Success) { throw new GitCommandException(args, result); } return result;
    }
    private static async Task<string> Commit(RepositoryFixture fixture, string subject, int year)
    {
        await fixture.Repository.StageAllAsync();
        GitResult result = await new GitRunner("/usr/bin/git").RunAsync(fixture.Repository.Path, ["commit", "-qm", subject], environment: new Dictionary<string, string?>
        { ["GIT_AUTHOR_DATE"] = $"{year}-01-01T12:00:00+00:00", ["GIT_COMMITTER_DATE"] = $"{year}-01-01T12:00:00+00:00" });
        Check(result.Success, result.StandardError); return (await Git(fixture.Repository, "rev-parse", "HEAD")).StandardOutput.TrimEnd('\r', '\n');
    }
    private static string Snapshot(string root) => string.Join('\n', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

    private sealed record Fixture(RepositoryFixture Owner, string Root, string Rename, string Tip, string OldPath, string Path) : IDisposable
    {
        internal GitRepository Repository => Owner.Repository;
        internal static async Task<Fixture> CreateAsync(string oldPath = "old.txt", string path = "new.txt")
        {
            RepositoryFixture owner = await RepositoryFixture.CreateAsync(seed: false);
            try
            {
                owner.Write(oldPath, "root contents\n"); string root = await Commit(owner, "Original tracked file", 2001);
                owner.Write("noise.txt", "noise\n"); await Commit(owner, "Omitted noise", 2002);
                await Git(owner.Repository, "mv", "--", oldPath, path); string rename = await Commit(owner, "Rename tracked file", 2003);
                File.AppendAllText(owner.File(path), "future contents\n"); string tip = await Commit(owner, "Future modification", 2004);
                return new(owner, root, rename, tip, oldPath, path);
            }
            catch { owner.Dispose(); throw; }
        }
        public void Dispose() => Owner.Dispose();
    }

    private readonly record struct Identity(int Pid, string Start);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Probe(int pid, int signal);
    private static string ReadStart(int pid)
    {
        string stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
        return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
    }
    private static bool Present(Identity identity) => !Retired(identity);
    private static bool Retired(Identity identity)
    {
        if (Probe(identity.Pid, 0) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == 3) { return true; } // ESRCH only; EPERM and other failures are not retirement.
            throw new Win32Exception(error, "Cannot establish the exact file-history actor's retirement.");
        }
        try { return ReadStart(identity.Pid) != identity.Start; } // A same-start zombie remains present.
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException || error is IOException { HResult: 3 })
        {
            if (Probe(identity.Pid, 0) == 0) { return false; }
            int native = Marshal.GetLastPInvokeError();
            if (native == 3) { return true; }
            throw new Win32Exception(native, "Missing procfs data did not establish ESRCH.");
        }
    }

    // Same real-runner injection and strict PID/start contract as the existing sink fixtures.
    // A pre-exec pipe pins the short-lived Git child's identity before exec; only real stdout is delayed.
    private sealed class Gate(string directory, GitRepository repository) : IDisposable
    {
        internal GitRepository Repository { get; } = repository;
        internal bool Paused => File.Exists(Path.Combine(directory, "paused"));
        internal bool Finished => File.Exists(Path.Combine(directory, "finished"));
        internal Identity[] Identities => JsonSerializer.Deserialize<Identity[]>(File.ReadAllBytes(Path.Combine(directory, "identities.json")))!;
        internal string[][] Commands => File.Exists(Path.Combine(directory, "commands.jsonl"))
            ? File.ReadAllLines(Path.Combine(directory, "commands.jsonl")).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray() : [];
        internal void Release() => File.WriteAllText(Path.Combine(directory, "release"), "release");
        internal static Gate Create(Fixture fixture, string phase)
        {
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Private native procfs gate."); }
            string directory = Path.Combine(fixture.Owner.Root, "file-stream-gate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "git");
            File.WriteAllText(executable, $$"""
                #!/usr/bin/python3
                import json,os,subprocess,sys,time
                from pathlib import Path
                root=Path({{JsonSerializer.Serialize(directory)}})
                argv=sys.argv[1:]
                with (root/'commands.jsonl').open('a') as trace: trace.write(json.dumps(argv)+'\n')
                wanted={{JsonSerializer.Serialize(phase)}}
                match=(wanted=='discovery' and '--follow' in argv) or (wanted=='graph' and '--parents' in argv and 'log' in argv)
                if not match: os.execv('/usr/bin/git',['git',*argv])
                read_fd,write_fd=os.pipe()
                trampoline="import os,sys; fd=int(sys.argv[1]); ok=os.read(fd,1); os.close(fd); assert ok==b'G'; os.execv('/usr/bin/git',['git',*sys.argv[2:]])"
                child=subprocess.Popen([sys.executable,'-c',trampoline,str(read_fd),*argv],pass_fds=(read_fd,),stdout=subprocess.PIPE)
                os.close(read_fd)
                def identity(pid):
                    fields=Path('/proc/'+str(pid)+'/stat').read_text().rsplit(') ',1)[1].split()
                    return {'Pid':pid,'Start':fields[19]}
                pending=root/'identities.pending'
                pending.write_text(json.dumps([identity(os.getpid()),identity(child.pid)]))
                os.replace(pending,root/'identities.json')
                os.write(write_fd,b'G'); os.close(write_fd)
                prefix=bytearray(); target=1 if wanted=='discovery' else 8
                separators=0
                while separators<target:
                    byte=child.stdout.read(1)
                    if not byte: raise RuntimeError('Real Git ended before the private prefix barrier')
                    prefix.extend(byte)
                    if byte==b'\0': separators+=1
                sys.stdout.buffer.write(prefix); sys.stdout.buffer.flush()
                (root/'paused').write_text('paused')
                for _ in range(6000):
                    if (root/'release').exists(): break
                    time.sleep(.01)
                else: raise RuntimeError('Private release was never acknowledged')
                while True:
                    chunk=child.stdout.read(8192)
                    if not chunk: break
                    sys.stdout.buffer.write(chunk); sys.stdout.buffer.flush()
                code=child.wait()
                (root/'finished').write_text('reaped')
                sys.exit(code)
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new(directory, new GitRepository(fixture.Repository.Path, new GitRunner(executable)));
        }
        public void Dispose() => Release();
    }
}
