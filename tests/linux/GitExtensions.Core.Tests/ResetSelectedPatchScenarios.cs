using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;

internal static class ResetSelectedPatchScenarios
{
    private const string Literal = ":[reset]* été\t\n[1].txt";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("selected reset working tree keeps adjacent unselected changes and exact index bytes", WorkingTreeAsync),
        ("selected reset index and worktree uses one native apply with exact CP1252 CRLF bytes", IndexAndWorkingTreeAsync),
        ("selected reset divergent index fails atomically without a cached fallback or second application", DivergentIndexAsync),
        ("selected reset empty bare malformed and precancelled requests preserve native state", GuardsAsync),
        ("selected reset owns input bytes and does not transcode through a later file codec setting", SnapshotAsync),
        ("selected reset cancellation drains its owned actors and leaves the unstarted mutation exact", CancellationAsync)
    ];

    private static async Task WorkingTreeAsync()
    {
        const string original = "first old €\nsecond old €\ncontext unchanged\n";
        using RepositoryFixture f = await CreateAsync(original);
        Write(f, "first new €\nsecond new €\ncontext unchanged\n");
        GitDiffDocument document = await f.Repository.GetDiffDocumentAsync(Literal);
        int start = document.Patch.IndexOf("+first new €", StringComparison.Ordinal);
        Check(start >= 0, "The real diff must expose the selected added line.");
        // A reverse patch is based on the changed side, so isIndex/staged must be true even for WT-only reset.
        byte[] selected = GitEncodedPatch.SelectLines(document, start, "+first new €\n".Length, staged: true, reverse: true)!;
        byte[] originalReset = GitEncodedPatch.SelectResetLines(document, start, "+first new €\n".Length)!;
        Check(selected.Length > 0 && originalReset.Length > 0, "Both original selector paths must produce real byte patches.");

        string oracle = Path.Combine(f.Root, "original-worktree-oracle");
        CopyTree(f.Repository.Path, oracle);
        GitBinaryResult nativeOracle = await new GitRunner().RunBinaryAsync(oracle, ["apply", "--whitespace=nowarn", "-"], standardInput: originalReset);
        Check(nativeOracle.Success, nativeOracle.StandardError);
        byte[] expected = File.ReadAllBytes(Path.Combine(oracle, Literal));
        Check(GitFileEncodingCodec.GetEncoding("windows-1252").GetString(expected) == "second new €\ncontext unchanged\n",
            "Resetting one added line removes that line, preserving the neighbouring unselected addition.");

        Material before = await CaptureAsync(f); string otherFiles = WorkingFiles(f, Literal);
        GitResult result = await f.Repository.ResetSelectedPatchAsync(selected, indexAndWorkingTree: false);
        Material after = await CaptureAsync(f);
        Check(result.Success && File.ReadAllBytes(f.File(Literal)).SequenceEqual(expected), "The WT facade must match the original native reset patch.");
        SameMetadata(before, after);
        Check(before.Index.SequenceEqual(after.Index) && before.Entries.SequenceEqual(after.Entries), "WT-only reset must preserve every raw index byte and entry.");
        Check(WorkingFiles(f, Literal) == otherFiles, "No adjacent literal path, staged file or untracked bytes may change.");
        Check((await f.Repository.GetIndexFileBytesAsync(Literal)).SequenceEqual(GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes(original)),
            "The committed/index baseline is not silently staged by reset.");
    }

    private static async Task IndexAndWorkingTreeAsync()
    {
        string original = Original("\r\n"), changed = Changed(original), expectedText = original.Replace("last old €", "last new €", StringComparison.Ordinal);
        using RepositoryFixture f = await CreateAsync(original);
        Write(f, changed);
        await f.Repository.StageAsync([Literal]);
        GitDiffDocument document = await f.Repository.GetDiffDocumentAsync(Literal, staged: true, options: new(ContextLines: 1));
        Check(GitPatch.Parse(document.Patch).Single().Hunks.Count == 2, "The real staged changes must remain two distinct hunks.");
        byte[] selected = GitEncodedPatch.SelectHunks(document, [0]);
        byte[] expected = GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes(expectedText);
        Wrapper wrapper = CreateWrapper(f, "index reset", gated: false);
        Material before = await CaptureAsync(f); string otherFiles = WorkingFiles(f, Literal);
        GitRepository observed = new(f.Repository.Path, new GitRunner(wrapper.Executable));
        GitResult result = await observed.ResetSelectedPatchAsync(selected, indexAndWorkingTree: true);
        Material after = await CaptureAsync(f);
        Check(result.Success && File.ReadAllBytes(f.File(Literal)).SequenceEqual(expected), "Selected hunk reset restores the first change in the WT, preserving CRLF and the other hunk.");
        byte[] actualIndex = await f.Repository.GetIndexFileBytesAsync(Literal);
        Check(actualIndex.SequenceEqual(expected), "One --index apply must update both the real index blob and WT.");
        Check(!before.Index.SequenceEqual(after.Index), "The selected index entry must actually change.");
        SameMetadata(before, after);
        Check(WorkingFiles(f, Literal) == otherFiles, "Unrelated staged/unstaged and untracked files remain exact.");
        AssertOneApply(wrapper, selected, index: true);
    }

    private static async Task DivergentIndexAsync()
    {
        string original = Original("\n");
        using RepositoryFixture f = await CreateAsync(original);
        Write(f, Changed(original)); await f.Repository.StageAsync([Literal]);
        GitDiffDocument document = await f.Repository.GetDiffDocumentAsync(Literal, staged: true, options: new(ContextLines: 1));
        byte[] selected = GitEncodedPatch.SelectHunks(document, [0]);
        Write(f, Changed(original) + "additional unstaged line €\n");
        Wrapper wrapper = CreateWrapper(f, "divergent reset", gated: false);
        GitRepository observed = new(f.Repository.Path, new GitRunner(wrapper.Executable));
        Material before = await CaptureAsync(f);
        GitCommandException error = await ExpectAsync<GitCommandException>(() => observed.ResetSelectedPatchAsync(selected, true));
        Material after = await CaptureAsync(f);
        Check(!error.Result.Success && error.Result.StandardError.Length > 0 && error.Result.StandardErrorBytes.Length > 0,
            "The actual --index rejection and raw stderr must remain available.");
        Check(error.Arguments.SequenceEqual(new[] { "apply", "--whitespace=nowarn", "--reverse", "--index", "-" }), "No --cached or secondary worktree application is permitted.");
        SameAll(before, after);
        AssertOneApply(wrapper, selected, index: true);
    }

    private static async Task GuardsAsync()
    {
        using RepositoryFixture f = await CreateAsync(Original("\n"));
        Write(f, Changed(Original("\n")));
        GitDiffDocument document = await f.Repository.GetDiffDocumentAsync(Literal, options: new(ContextLines: 1));
        byte[] patch = GitEncodedPatch.SelectHunks(document, [0]);
        Wrapper wrapper = CreateWrapper(f, "guarded reset", gated: false);
        GitRepository observed = new(f.Repository.Path, new GitRunner(wrapper.Executable));
        Material before = await CaptureAsync(f);
        await ExpectAsync<ArgumentException>(() => observed.ResetSelectedPatchAsync(ReadOnlyMemory<byte>.Empty, false));
        using CancellationTokenSource stopped = new(); stopped.Cancel();
        await ExpectAsync<OperationCanceledException>(() => observed.ResetSelectedPatchAsync(patch, false, stopped.Token));
        Check(Commands(wrapper).Count == 0, "Empty or precancelled requests cannot start apply.");
        SameAll(before, await CaptureAsync(f));

        byte[] invalid = Encoding.UTF8.GetBytes("not a patch é\n");
        GitCommandException error = await ExpectAsync<GitCommandException>(() => observed.ResetSelectedPatchAsync(invalid, false));
        Check(!error.Result.Success && error.Result.StandardErrorBytes.Length > 0, "Malformed bytes are rejected by real Git, not reconstructed by the facade.");
        AssertOneApply(wrapper, invalid, index: false);
        SameAll(before, await CaptureAsync(f));

        string barePath = Path.Combine(f.Root, "bare reset.git");
        GitRepository bare = await GitRepository.InitAsync(barePath, bare: true);
        string bareBefore = Tree(barePath);
        await ExpectAsync<InvalidOperationException>(() => bare.ResetSelectedPatchAsync(patch, true));
        Check(Tree(barePath) == bareBefore, "Bare refusal cannot modify refs, configuration, objects or create an index.");
    }

    private static async Task SnapshotAsync()
    {
        string original = Original("\n");
        using RepositoryFixture f = await CreateAsync(original);
        Write(f, Changed(original));
        GitDiffDocument document = await f.Repository.GetDiffDocumentAsync(Literal, options: new(ContextLines: 1));
        byte[] selected = GitEncodedPatch.SelectHunks(document, [0]), expectedInput = selected.ToArray();
        Check(expectedInput.Contains((byte)0x80) && expectedInput.AsSpan().IndexOf(new byte[] { 0xc3, 0xa9 }) >= 0,
            "The real selected bytes combine CP1252 body and UTF8 literal pathname headers.");
        await f.Repository.SetConfigAsync("i18n.filesencoding", "utf-16");
        Wrapper wrapper = CreateWrapper(f, "owned bytes reset", gated: true);
        GitRepository observed = new(f.Repository.Path, new GitRunner(wrapper.Executable));
        Material before = await CaptureAsync(f);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        Task<GitResult> pending = observed.ResetSelectedPatchAsync(selected, false, cancellation.Token);
        Array.Fill(selected, (byte)0xff);
        try
        {
            await UntilAsync(() => File.Exists(wrapper.Marker), pending);
            Actor[] actors = ReadActors(wrapper);
            Check(!pending.IsCompleted && actors.All(actor => actor.Alive()), "The real invocation and sleeper remain owned at the barrier.");
            File.WriteAllText(wrapper.Release, "release\n");
            GitResult result = await pending.WaitAsync(Deadline);
            Check(result.Success && File.ReadAllBytes(wrapper.Input).SequenceEqual(expectedInput), "Input is copied before await and never transcoded through the changed repository codec.");
            Check(File.ReadAllBytes(f.File(Literal)).SequenceEqual(GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes(original.Replace("last old €", "last new €", StringComparison.Ordinal))),
                "The actual WT result retains legacy bytes and the unselected hunk.");
            Material after = await CaptureAsync(f);
            SameMetadata(before, after);
            Check(before.Index.SequenceEqual(after.Index) && before.Entries.SequenceEqual(after.Entries), "Owned input processing must not stage any data.");
            Check(actors.All(actor => !actor.Alive()), "The actual wrapper and sleeper are gone before success returns.");
            AssertOneApply(wrapper, expectedInput, index: false);
        }
        finally { cancellation.Cancel(); File.WriteAllText(wrapper.Release, "release\n"); await DrainAsync(pending); }
    }

    private static async Task CancellationAsync()
    {
        using RepositoryFixture f = await CreateAsync(Original("\n"));
        Write(f, Changed(Original("\n"))); await f.Repository.StageAsync([Literal]);
        GitDiffDocument document = await f.Repository.GetDiffDocumentAsync(Literal, staged: true, options: new(ContextLines: 1));
        byte[] selected = GitEncodedPatch.SelectHunks(document, [0]);
        Wrapper wrapper = CreateWrapper(f, "cancel selected reset", gated: true);
        GitRepository observed = new(f.Repository.Path, new GitRunner(wrapper.Executable));
        Material before = await CaptureAsync(f);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        Task<GitResult> pending = observed.ResetSelectedPatchAsync(selected, true, cancellation.Token);
        try
        {
            await UntilAsync(() => File.Exists(wrapper.Marker), pending);
            Actor[] actors = ReadActors(wrapper);
            Check(actors.Length == 2 && actors.All(actor => actor.Alive()), "Both recorded actors must exist before cancellation.");
            Check(actors.All(actor => actor.Group == actors[0].Group) && actors[0].Group != Actor.Read(Environment.ProcessId).Group,
                "Only this invocation's private process group may be stopped.");
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
            Check(actors.All(actor => !actor.Alive()), "Cancellation returns after the wrapper and inherited-pipe sleeper terminate.");
            SameAll(before, await CaptureAsync(f));
            AssertOneApply(wrapper, selected, index: true);
        }
        finally { cancellation.Cancel(); await DrainAsync(pending); }
    }

    private static string Original(string newline) => "first old € ‘quote’" + newline + string.Concat(Enumerable.Range(0, 18).Select(i => "stable " + i + newline)) + "last old €" + newline;
    private static string Changed(string text) => text.Replace("first old € ‘quote’", "first new € “quote”", StringComparison.Ordinal).Replace("last old €", "last new €", StringComparison.Ordinal);
    private static void Write(RepositoryFixture f, string text) => File.WriteAllBytes(f.File(Literal), GitFileEncodingCodec.GetEncoding("windows-1252").GetBytes(text));
    private static async Task<RepositoryFixture> CreateAsync(string original)
    {
        RepositoryFixture f = await RepositoryFixture.CreateAsync();
        try
        {
            await f.Repository.SetConfigAsync("core.autocrlf", "false");
            await f.Repository.SetConfigAsync("i18n.filesencoding", "windows-1252");
            Write(f, original); f.Write("other.txt", "other committed\n"); f.Write(":[reset]* été\t\n1.txt", "neighbour unchanged\n");
            await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Selected reset source");
            f.Write("other.txt", "other indexed\n"); await f.Repository.StageAsync(["other.txt"]); f.Write("other.txt", "other unstaged\n");
            File.WriteAllBytes(f.File("untracked λ*.bin"), [0x00, 0xff, 0x0d, 0x0a]);
            return f;
        }
        catch { f.Dispose(); throw; }
    }

    private sealed record Material(byte[] Index, byte[] Entries, string Metadata, string Files);
    private static async Task<Material> CaptureAsync(RepositoryFixture f)
    {
        GitBinaryResult entries = await new GitRunner().RunBinaryAsync(f.Repository.Path, ["ls-files", "--stage", "-z"]);
        Check(entries.Success, entries.StandardError);
        string metadata = string.Join("\n", new[] { ".git/HEAD", ".git/ORIG_HEAD", ".git/config", ".git/packed-refs", ".git/index.lock" }.Where(path => File.Exists(f.File(path))).Select(path => FileWitness(f.Repository.Path, f.File(path)))
            .Concat(new[] { ".git/refs", ".git/logs" }.Where(path => Directory.Exists(f.File(path))).SelectMany(path => Directory.GetFiles(f.File(path), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(file => FileWitness(f.Repository.Path, file)))));
        return new(File.ReadAllBytes(f.File(".git/index")), entries.StandardOutput, metadata, WorkingFiles(f));
    }
    private static void SameMetadata(Material before, Material after) => Check(before.Metadata == after.Metadata, "HEAD, refs, reflogs and repository configuration must remain byte/mode exact.");
    private static void SameAll(Material before, Material after)
    {
        SameMetadata(before, after);
        Check(before.Index.SequenceEqual(after.Index) && before.Entries.SequenceEqual(after.Entries) && before.Files == after.Files,
            "A refused or cancelled reset must preserve the raw index, canonical entries and every WT/untracked byte and mode.");
    }
    private static string WorkingFiles(RepositoryFixture f, string? except = null) => string.Join("\n", Directory.GetFiles(f.Repository.Path, "*", SearchOption.AllDirectories)
        .Where(file => !Path.GetRelativePath(f.Repository.Path, file).StartsWith(".git/", StringComparison.Ordinal) && Path.GetRelativePath(f.Repository.Path, file) != except)
        .Order(StringComparer.Ordinal).Select(file => FileWitness(f.Repository.Path, file)));
    private static string Tree(string directory) => string.Join("\n", Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(file => FileWitness(directory, file)));
    private static string FileWitness(string root, string file)
        => JsonSerializer.Serialize(new { Path = Path.GetRelativePath(root, file), Mode = OperatingSystem.IsLinux() ? (int)File.GetUnixFileMode(file) : 0, Bytes = Convert.ToBase64String(File.ReadAllBytes(file)) });
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) { Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory))); }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file)); File.Copy(file, destination);
            if (OperatingSystem.IsLinux()) { File.SetUnixFileMode(destination, File.GetUnixFileMode(file)); }
        }
    }

    private sealed record Wrapper(string Executable, string Log, string Input, string Marker, string Release);
    private static Wrapper CreateWrapper(RepositoryFixture f, string name, bool gated)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The owned Git wrapper fixture requires Linux."); }
        string executable = Path.Combine(f.Root, name);
        Wrapper wrapper = new(executable, executable + ".jsonl", executable + ".stdin", executable + ".actors", executable + ".release");
        string script = $$"""
            #!/usr/bin/python3
            import json,os,pathlib,subprocess,sys,time
            args=sys.argv[1:]
            if 'apply' not in args:
                os.execv('/usr/bin/git',['git',*args])
            with open({{JsonSerializer.Serialize(wrapper.Log)}},'a',encoding='utf-8') as output:
                output.write(json.dumps(args,ensure_ascii=False)+'\n')
            data=sys.stdin.buffer.read()
            pathlib.Path({{JsonSerializer.Serialize(wrapper.Input)}}).write_bytes(data)
            if {{(gated ? "True" : "False")}}:
                subprocess.run(['/usr/bin/git','rev-parse','--verify','HEAD'],stdout=subprocess.DEVNULL,check=True)
                child=subprocess.Popen(['/bin/sleep','60'])
                marker=pathlib.Path({{JsonSerializer.Serialize(wrapper.Marker)}})
                marker.with_suffix(marker.suffix+'.tmp').write_text(str(os.getpid())+' '+str(child.pid),encoding='ascii')
                os.replace(str(marker)+'.tmp',marker)
                while not pathlib.Path({{JsonSerializer.Serialize(wrapper.Release)}}).exists():
                    time.sleep(0.01)
                child.terminate()
                child.wait()
            descriptor=os.open({{JsonSerializer.Serialize(wrapper.Input)}},os.O_RDONLY)
            os.dup2(descriptor,0)
            os.close(descriptor)
            os.execv('/usr/bin/git',['git',*args])
            """;
        File.WriteAllText(executable, script + "\n", new UTF8Encoding(false));
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return wrapper;
    }
    private static IReadOnlyList<string[]> Commands(Wrapper wrapper) => !File.Exists(wrapper.Log) ? [] : File.ReadAllLines(wrapper.Log).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
    private static void AssertOneApply(Wrapper wrapper, byte[] input, bool index)
    {
        IReadOnlyList<string[]> commands = Commands(wrapper);
        Check(commands.Count == 1, "Exactly one apply invocation may be launched by the facade.");
        string[] arguments = commands.Single(); string[] actual = arguments[Array.IndexOf(arguments, "apply")..];
        string[] expected = index ? ["apply", "--whitespace=nowarn", "--reverse", "--index", "-"] : ["apply", "--whitespace=nowarn", "--reverse", "-"];
        Check(actual.SequenceEqual(expected) && File.ReadAllBytes(wrapper.Input).SequenceEqual(input), "The actual argv and raw stdin must match the selected-patch command byte for byte.");
    }
    private sealed record Actor(int Pid, long Start, int Group)
    {
        internal static Actor Read(int pid)
        {
            string text = File.ReadAllText("/proc/" + pid + "/stat"); string[] fields = text[(text.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(pid, long.Parse(fields[19], CultureInfo.InvariantCulture), int.Parse(fields[2], CultureInfo.InvariantCulture));
        }
        internal bool Alive()
        {
            try { Actor current = Read(Pid); return current.Start == Start && LinuxProcessState.IsRunning(Pid); }
            catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
        }
    }
    private static Actor[] ReadActors(Wrapper wrapper) => File.ReadAllText(wrapper.Marker).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => Actor.Read(int.Parse(value, CultureInfo.InvariantCulture))).ToArray();
    private static async Task UntilAsync(Func<bool> condition, Task operation)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition() && !operation.IsCompleted && timer.Elapsed < Deadline) { await Task.Delay(10); }
        Check(condition(), "The owned Git barrier did not become ready before the bounded deadline.");
    }
    private static async Task<T> ExpectAsync<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task DrainAsync(Task operation)
    { try { await operation.WaitAsync(Deadline); } catch (OperationCanceledException) { } }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
