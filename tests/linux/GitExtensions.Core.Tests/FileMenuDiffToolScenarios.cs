using System.Text.Json;
using GitExtensions.Linux;

internal static class FileMenuDiffToolScenarios
{
    private const string Literal = ":[file]* λ\t\n.txt";
    private const string Other = "file1 λ.txt";
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("file menu custom tools retain configured names and native selected-pair bytes", PairAsync),
        ("file menu two-file difftool reads typed Index and worktree sides without changing either", TwoFilesAsync),
        ("file menu independent difftool edits live worktree files but only private Index and Commit snapshots", LiveWorkingTreeAsync),
        ("file menu committed difftool works in a bare repository", BareAsync),
        ("file menu deletion preserves index and literal neighbors and preflights all selected paths", DeleteAsync),
        ("file menu difftool cancellation drains the real tool and removes its temporary copies", CancellationAsync)
    ];

    private static async Task PairAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write(Literal, "before λ\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Before file action");
        string first = await f.Repository.ResolveRevisionAsync("HEAD");
        f.Write(Literal, "after λ\n"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("After file action");
        string second = await f.Repository.ResolveRevisionAsync("HEAD");
        string log = await ToolAsync(f, "record.bytes");
        await f.Repository.SetConfigAsync("difftool.z-last.cmd", "true");
        await f.Repository.SetConfigAsync("mergetool.not-a-diff.cmd", "false");
        IReadOnlyList<string> tools = await f.Repository.GetCustomDiffToolsAsync();
        Check(tools.SequenceEqual(new[] { "record.bytes", "z-last" }), "Only complete difftool subsections are returned, preserving dots and stable order.");
        f.Write(Literal, "dirty content must not be opened\n"); f.Write(Other, "untouched neighbor\n");
        byte[] index = File.ReadAllBytes(f.File(".git/index"));
        GitComparison comparison = await f.Repository.GetComparisonAsync(new(first), new(second));
        await f.Repository.OpenComparisonDiffToolAsync(comparison, comparison.Files.Single(), false, "record.bytes");
        using JsonDocument record = JsonDocument.Parse(File.ReadAllText(log));
        Check(record.RootElement.GetProperty("left").GetString() == "before λ\n" && record.RootElement.GetProperty("right").GetString() == "after λ\n",
            "A real configured native Git difftool receives the selected commits rather than HEAD/worktree substitutions.");
        Check(File.ReadAllBytes(f.File(".git/index")).SequenceEqual(index) && File.ReadAllText(f.File(Literal)).StartsWith("dirty", StringComparison.Ordinal)
            && File.ReadAllText(f.File(Other)) == "untouched neighbor\n", "Difftool reading leaves index, dirty file and adjacent literal path unchanged.");
    }

    private static async Task TwoFilesAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write(Literal, "index side λ\n"); await f.Repository.StageAsync([Literal]);
        f.Write(Literal, "different dirty left\n"); f.Write(Other, "working side λ\n");
        string log = await ToolAsync(f, "record.bytes");
        byte[] index = File.ReadAllBytes(f.File(".git/index"));
        GitResult result = await f.Repository.OpenFileDiffToolAsync(new(new("", GitCompareRevisionKind.Index), Literal),
            new(new("", GitCompareRevisionKind.WorkingTree), Other), "record.bytes");
        Check(result.ExitCode == 1, "Native no-index differences retain their accepted exit 1 rather than an invented zero.");
        using JsonDocument record = JsonDocument.Parse(File.ReadAllText(log));
        Check(record.RootElement.GetProperty("left").GetString() == "index side λ\n" && record.RootElement.GetProperty("right").GetString() == "working side λ\n",
            "Independent paths and typed endpoints each supply their own actual bytes.");
        string[] copies = record.RootElement.GetProperty("paths").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Check(copies.Length == 2 && !File.Exists(copies[0]) && copies[1] == f.File(Other) && File.Exists(copies[1]),
            "Only the private Index copy is removed; the worktree side is the existing live file.");
        Check(File.ReadAllBytes(f.File(".git/index")).SequenceEqual(index) && File.ReadAllText(f.File(Literal)) == "different dirty left\n"
            && File.ReadAllText(f.File(Other)) == "working side λ\n", "Two-file comparison leaves the live staged/working snapshots unchanged.");
        byte[] binaryLeft = [0, 255, 128, 13, 10], binaryRight = [255, 0, 129, 10];
        File.WriteAllBytes(f.File(Literal), binaryLeft); await f.Repository.StageAsync([Literal]);
        File.WriteAllBytes(f.File(Literal), [1, 2, 3]); File.WriteAllBytes(f.File(Other), binaryRight);
        index = File.ReadAllBytes(f.File(".git/index"));
        await f.Repository.OpenFileDiffToolAsync(new(new("", GitCompareRevisionKind.Index), Literal),
            new(new("", GitCompareRevisionKind.WorkingTree), Other), "record.bytes");
        using JsonDocument binary = JsonDocument.Parse(File.ReadAllText(log));
        Check(Convert.FromBase64String(binary.RootElement.GetProperty("leftBase64").GetString()!).SequenceEqual(binaryLeft)
            && Convert.FromBase64String(binary.RootElement.GetProperty("rightBase64").GetString()!).SequenceEqual(binaryRight)
            && File.ReadAllBytes(f.File(".git/index")).SequenceEqual(index), "Native tools receive exact NUL and invalid UTF8 snapshot bytes without refreshing the index.");
    }

    private static async Task LiveWorkingTreeAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write(Literal, "committed\n"); f.Write(Other, "working\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Live tool inputs");
        string commit = await f.Repository.ResolveRevisionAsync("HEAD");
        f.Write(Literal, "staged\n"); await f.Repository.StageAsync([Literal]); f.Write(Literal, "dirty left\n");
        byte[] index = File.ReadAllBytes(f.File(".git/index"));
        string log = await ToolAsync(f, "edit.inputs", writeInputs: true);
        byte[] editedLeft = [116, 111, 111, 108, 45, 108, 101, 102, 116, 0, 255, 10], editedRight = [116, 111, 111, 108, 45, 114, 105, 103, 104, 116, 0, 254, 10];
        foreach (GitCompareRevision snapshot in new[] { new GitCompareRevision("", GitCompareRevisionKind.Index), new GitCompareRevision(commit) })
        {
            f.Write(Other, "working before native edit\n");
            await f.Repository.OpenFileDiffToolAsync(new(snapshot, Literal), new(new("", GitCompareRevisionKind.WorkingTree), Other), "edit.inputs");
            using JsonDocument record = JsonDocument.Parse(File.ReadAllText(log));
            string[] inputs = record.RootElement.GetProperty("paths").EnumerateArray().Select(path => path.GetString()!).ToArray();
            Check(File.ReadAllBytes(f.File(Other)).SequenceEqual(editedRight) && inputs[1] == f.File(Other),
                "The actual configured native tool edits the selected live worktree file rather than a discarded copy.");
            Check(inputs[0] != f.File(Literal) && !File.Exists(inputs[0]) && File.ReadAllText(f.File(Literal)) == "dirty left\n"
                && File.ReadAllBytes(f.File(".git/index")).SequenceEqual(index), "The edited Commit/Index side is a private copy and never stages or overwrites its dirty local counterpart.");
            Check(System.Text.Encoding.UTF8.GetString(await f.Repository.GetComparisonFileBytesAsync(snapshot, Literal)) == (snapshot.Kind == GitCompareRevisionKind.Index ? "staged\n" : "committed\n"),
                "Writing a snapshot copy leaves the actual staged or committed blob unchanged.");
        }
        f.Write("new-untracked.txt", "new existing\n"); f.Write(Other, "working before two live edits\n");
        await f.Repository.OpenFileDiffToolAsync(new(new("", GitCompareRevisionKind.WorkingTree), "new-untracked.txt"),
            new(new("", GitCompareRevisionKind.WorkingTree), Other), "edit.inputs");
        Check(File.ReadAllBytes(f.File("new-untracked.txt")).SequenceEqual(editedLeft) && File.ReadAllBytes(f.File(Other)).SequenceEqual(editedRight)
            && File.ReadAllBytes(f.File(".git/index")).SequenceEqual(index), "Both existing worktree sides, including an untracked file, remain genuinely editable without touching the index.");
        File.Delete(log);
        await ExpectAsync<FileNotFoundException>(() => f.Repository.OpenFileDiffToolAsync(new(new("", GitCompareRevisionKind.WorkingTree), Other),
            new(new("", GitCompareRevisionKind.WorkingTree), "not-created.txt"), "edit.inputs"));
        Directory.CreateDirectory(f.File("directory"));
        await ExpectAsync<InvalidOperationException>(() => f.Repository.OpenFileDiffToolAsync(new(new("", GitCompareRevisionKind.WorkingTree), Other),
            new(new("", GitCompareRevisionKind.WorkingTree), "directory"), "edit.inputs"));
        string outside = Path.Combine(f.Root, "outside-tool.txt"); File.WriteAllText(outside, "outside\n");
        File.CreateSymbolicLink(f.File("final-link"), outside); Directory.CreateSymbolicLink(f.File("parent-link"), f.Root);
        foreach (string invalid in new[] { "final-link", "parent-link/outside-tool.txt" })
        { await ExpectAsync<InvalidOperationException>(() => f.Repository.OpenFileDiffToolAsync(new(new("", GitCompareRevisionKind.WorkingTree), Other),
            new(new("", GitCompareRevisionKind.WorkingTree), invalid), "edit.inputs")); }
        Check(!File.Exists(log) && !File.Exists(f.File("not-created.txt")) && File.ReadAllText(outside) == "outside\n"
            && File.ReadAllBytes(f.File(".git/index")).SequenceEqual(index), "Both sides are preflighted before any tool launch; absent files, directories and symbolic traversal never become writable substitutes.");
    }

    private static async Task BareAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write(Literal, "one\n"); f.Write("deleted.txt", "deleted\n"); f.Write("old.txt", "rename1\nrename2\nrename3\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("First");
        string first = await f.Repository.ResolveRevisionAsync("HEAD");
        f.Write(Literal, "two\n"); File.Delete(f.File("deleted.txt")); File.Move(f.File("old.txt"), f.File("new.txt"));
        File.AppendAllText(f.File("new.txt"), "rename4\n"); f.Write("added.txt", "added\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Second");
        string second = await f.Repository.ResolveRevisionAsync("HEAD");
        string barePath = Path.Combine(f.Root, "bare.git"); await f.Repository.RunAsync(["clone", "--bare", "--", f.Repository.Path, barePath]);
        GitRepository bare = await GitRepository.OpenAsync(barePath);
        string log = await ToolAsync(f, "record.bytes", bare);
        GitComparison comparison = await bare.GetComparisonAsync(new(first), new(second));
        await bare.OpenComparisonDiffToolAsync(comparison, comparison.Files.Single(file => file.Path == Literal), false, "record.bytes");
        using JsonDocument record = JsonDocument.Parse(File.ReadAllText(log));
        Check(record.RootElement.GetProperty("left").GetString() == "one\n" && record.RootElement.GetProperty("right").GetString() == "two\n",
            "Committed file comparisons in a bare repository use real objects and a native difftool without a worktree.");
        await bare.OpenFileDiffToolAsync(new(new(first), Literal), new(new(second), Literal), "record.bytes");
        foreach (GitRevisionFile file in comparison.Files.Where(file => file.Path != Literal))
        {
            await bare.OpenComparisonDiffToolAsync(comparison, file, false, "record.bytes");
            using JsonDocument changed = JsonDocument.Parse(File.ReadAllText(log));
            string left = changed.RootElement.GetProperty("left").GetString()!, right = changed.RootElement.GetProperty("right").GetString()!;
            Check(file.Status switch { 'A' => left == "" && right == "added\n", 'D' => left == "deleted\n" && right == "", 'R' => left == "rename1\nrename2\nrename3\n" && right == "rename1\nrename2\nrename3\nrename4\n", _ => false },
                "Bare additions deletions and renames retain their actual oriented content and absent-side emptiness.");
        }
        Check(await bare.ResolveRevisionAsync("HEAD") == second && !File.Exists(Path.Combine(barePath, "index")), "Bare tool use creates neither a worktree nor an index.");
    }

    private static async Task DeleteAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync();
        f.Write(Literal, "tracked\n"); f.Write(Other, "neighbor\n"); f.Write("tracked-dir/anchor.txt", "retained\n");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Files to delete");
        f.Write("untracked-dir/a.txt", "untracked\n");
        byte[] index = File.ReadAllBytes(f.File(".git/index"));
        await f.Repository.DeleteWorkingTreeFilesAsync([Literal, "untracked-dir"]);
        Check(!File.Exists(f.File(Literal)) && !Directory.Exists(f.File("untracked-dir")) && File.ReadAllText(f.File(Other)) == "neighbor\n",
            "Only the chosen literal file and explicitly selected untracked directory are deleted.");
        Check(File.ReadAllBytes(f.File(".git/index")).SequenceEqual(index), "Deleting working files does not stage their deletion or refresh the index.");
        await ExpectAsync<InvalidOperationException>(() => f.Repository.DeleteWorkingTreeFilesAsync([Other, "tracked-dir"]));
        Check(File.Exists(f.File(Other)) && File.Exists(f.File("tracked-dir/anchor.txt")), "An invalid directory selection is rejected before any valid selected file is deleted.");
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        await ExpectAsync<OperationCanceledException>(() => f.Repository.DeleteWorkingTreeFilesAsync([Other], cancel.Token));
        await ExpectAsync<ArgumentException>(() => f.Repository.DeleteWorkingTreeFilesAsync(["../outside"]));
        string outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside\n");
        Directory.CreateSymbolicLink(f.File("link"), outside);
        await ExpectAsync<InvalidOperationException>(() => f.Repository.DeleteWorkingTreeFilesAsync([Other, "link/keep.txt"]));
        Check(File.Exists(f.File(Other)) && File.Exists(Path.Combine(outside, "keep.txt")), "A symbolic-link parent cannot redirect deletion, and preflight is selection-wide.");
        await f.Repository.DeleteWorkingTreeFilesAsync(["link"]);
        Check(new DirectoryInfo(f.File("link")).LinkTarget is null && File.Exists(Path.Combine(outside, "keep.txt")), "Deleting the link itself preserves its external directory.");
    }

    private static async Task CancellationAsync()
    {
        using RepositoryFixture f = await RepositoryFixture.CreateAsync(); f.Write(Literal, "left\n"); f.Write(Other, "right\n");
        await f.Repository.StageAsync([Literal]);
        string log = await ToolAsync(f, "record.bytes", hold: true);
        using CancellationTokenSource cancel = new();
        Task<GitResult> operation = f.Repository.OpenFileDiffToolAsync(new(new("", GitCompareRevisionKind.Index), Literal),
            new(new("", GitCompareRevisionKind.WorkingTree), Other), "record.bytes", cancel.Token);
        int observedPid = 0;
        try
        {
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (observedPid == 0)
            {
                if (File.Exists(log))
                {
                    try { using JsonDocument started = JsonDocument.Parse(File.ReadAllText(log)); observedPid = started.RootElement.GetProperty("pid").GetInt32(); }
                    catch (JsonException) { }
                }
                if (operation.IsCompleted) { await operation; }
                if (DateTime.UtcNow > until) { throw new TimeoutException("The native difftool did not start."); }
                if (observedPid == 0) { await Task.Delay(15); }
            }
            Check(Directory.Exists("/proc/" + observedPid), "The actual configured tool PID is witnessed alive before cancellation.");
            Console.WriteLine("WITNESS native-difftool pid=" + observedPid + " before-cancel=alive");
        }
        finally
        {
            cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(async () => await operation);
        }
        using JsonDocument record = JsonDocument.Parse(File.ReadAllText(log));
        int pid = record.RootElement.GetProperty("pid").GetInt32();
        Check(pid == observedPid && !Directory.Exists("/proc/" + pid), "Cancellation waits for the same witnessed configured tool process to exit.");
        Console.WriteLine("WITNESS native-difftool pid=" + pid + " after-drain=stopped");
        string[] inputs = record.RootElement.GetProperty("paths").EnumerateArray().Select(path => path.GetString()!).ToArray();
        Check(!File.Exists(inputs[0]) && inputs[1] == f.File(Other) && File.ReadAllText(f.File(Other)) == "right\n",
            "Temporary copies remain owned until drain, then are removed while live worktree files stay present.");
    }

    private static async Task<string> ToolAsync(RepositoryFixture f, string name, GitRepository? repository = null, bool hold = false, bool writeInputs = false)
    {
        string helper = Path.Combine(f.Root, "native-diff-tool"), output = Path.Combine(f.Root, "tool-result.json");
        string script = "#!/usr/bin/python3\nimport base64,json,os,sys,time\n" +
            "left=open(sys.argv[1],'rb').read();right=open(sys.argv[2],'rb').read()\n" +
            "data={'pid':os.getpid(),'paths':sys.argv[1:],'left':left.decode('utf-8','replace'),'right':right.decode('utf-8','replace'),'leftBase64':base64.b64encode(left).decode(),'rightBase64':base64.b64encode(right).decode()}\n" +
            "with open(" + JsonSerializer.Serialize(output) + ",'w') as f:json.dump(data,f)\n" +
            (writeInputs ? "open(sys.argv[1],'wb').write(b'tool-left\\x00\\xff\\n');open(sys.argv[2],'wb').write(b'tool-right\\x00\\xfe\\n')\n" : "") +
            (hold ? "while True:time.sleep(0.05)\n" : "");
        await File.WriteAllTextAsync(helper, script);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        await (repository ?? f.Repository).SetConfigAsync("difftool." + name + ".cmd", Quote(helper) + " \"$LOCAL\" \"$REMOTE\"");
        return output;
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
}
