using System.Security.Cryptography;
using System.Text;
using GitExtensions.Linux;

internal static class SubmoduleRemovalScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("submodule removal retains the last worktree registration and modern repository", LastAsync),
        ("submodule removal stages remaining registrations and keeps unrelated index/worktree edits", MultipleAsync),
        ("submodule removal preserves legacy embedded repositories", LegacyAsync),
        ("submodule removal supports literal difficult names and paths", LiteralAsync),
        ("submodule removal rejects stale and foreign selections without changes", SelectionAsync),
        ("submodule removal rejects symlinks and malformed or ambiguous registrations", SafetyAsync),
        ("submodule removal honors native cached-rm safety and live locks", IndexSafetyAsync),
        ("submodule removal cancellation and native errors preserve all live state", CancellationAsync),
        ("submodule removal refuses in-progress and conflicted operations", InProgressAsync),
        ("submodule removal supports split indexes and linked worktrees", AlternateIndexesAsync)
    ];

    private static void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }
    private static async Task ExpectFailureAsync(Func<Task> run)
    {
        try { await run(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or GitCommandException or IOException or OperationCanceledException) { return; }
        throw new Exception("An unsafe removal unexpectedly succeeded.");
    }

    private static async Task LastAsync()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync();
        byte[] modules = File.ReadAllBytes(f.File(".gitmodules"));
        await File.WriteAllTextAsync(f.ModuleFile("untracked λ.txt"), "valuable untracked data");
        await File.WriteAllBytesAsync(f.ModuleFile("source.bin"), [0, 255, 128, 10]);
        string moduleBefore = f.ModuleSnapshot();
        GitSubmoduleRemovalResult result = await f.Repository.RemoveSubmoduleAsync(await f.SelectionAsync());
        Check(result.RemovedModulesFileFromIndex, "Last section must uncache .gitmodules.");
        Check(File.ReadAllBytes(f.File(".gitmodules")).SequenceEqual(modules), "Original last-section behavior keeps worktree .gitmodules unchanged.");
        Check((await f.Repository.GetSubmoduleRemovalSelectionsAsync()).Count == 0, "A retained registration must not resurrect a removed gitlink.");
        Check((await f.Repository.GetSubmodulesAsync()).Count == 0, "The general submodule list must also exclude removed gitlinks.");
        Check(f.ModuleSnapshot() == moduleBefore, "Every module worktree and internal-repository byte must survive.");
        Check((await f.GitAsync("ls-files", "--stage", "-z")).StandardOutput.Split('\0').All(entry => !entry.EndsWith("\t" + f.ModulePath) && !entry.EndsWith("\t.gitmodules")), "Only gitlink and .gitmodules must leave the index.");
        Check(!(await f.GitAsync("config", "--local", "--get", "submodule." + f.Name + ".url", allowed: [1])).Success, "Local registration must be removed.");
    }

    private static async Task MultipleAsync()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync();
        await f.AddAsync("second", "other[2]");
        await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Two modules");
        await File.AppendAllTextAsync(f.File(".gitmodules"), "\n[custom \"keep\"]\n\tvalue = staged\n");
        await f.Repository.StageAsync([".gitmodules"]);
        await File.AppendAllTextAsync(f.File(".gitmodules"), "\tvalue = worktree λ\n# retained comment\n");
        await File.WriteAllTextAsync(f.File("other.txt"), "staged\n"); await f.Repository.StageAsync(["other.txt"]);
        await File.WriteAllTextAsync(f.File("other.txt"), "unstaged\n");
        string otherIndex = (await f.GitAsync("show", ":other.txt")).StandardOutput;
        string retained = File.ReadAllText(f.File(".gitmodules"));
        string moduleBefore = f.ModuleSnapshot();
        GitSubmoduleRemovalResult result = await f.Repository.RemoveSubmoduleAsync(await f.SelectionAsync());
        Check(!result.RemovedModulesFileFromIndex, "Other sections keep .gitmodules staged.");
        string worktree = File.ReadAllText(f.File(".gitmodules"));
        Check(!worktree.Contains("[submodule \"module\"]") && worktree.Contains("# retained comment") && worktree.Contains("worktree λ"), "Only selected config section is rewritten, preserving the working copy's other data.");
        Check((await f.GitAsync("show", ":.gitmodules")).StandardOutput == worktree, "Windows Save+Stage must stage the worktree registration despite existing index divergence.");
        Check((await f.GitAsync("show", ":other.txt")).StandardOutput == otherIndex && File.ReadAllText(f.File("other.txt")) == "unstaged\n", "Unrelated staged and unstaged edits survive.");
        Check(f.ModuleSnapshot() == moduleBefore && (await f.Repository.GetSubmoduleRemovalSelectionsAsync()).Single().Name == "second", "Other module remains and selected data survives.");
        Check(retained != worktree, "Registration should change.");
    }

    private static async Task LegacyAsync()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync();
        string modern = Path.Combine(f.Repository.Path, (await f.GitAsync("rev-parse", "--git-path", "modules/" + f.Name)).StandardOutput.Trim());
        File.Delete(f.ModuleFile(".git")); Directory.Move(modern, f.ModuleFile(".git"));
        await f.GitAsync("config", "--file", f.ModuleFile(".git/config"), "--unset", "core.worktree");
        string before = f.ModuleSnapshot();
        await f.Repository.RemoveSubmoduleAsync(await f.SelectionAsync());
        Check(f.ModuleSnapshot() == before && Directory.Exists(f.ModuleFile(".git")), "Legacy embedded .git and objects must survive.");
        Check((await new GitRepository(f.ModuleDirectory).ResolveRevisionAsync("HEAD")) == f.ModuleHead, "Legacy repository remains usable.");
        using RemovalFixture uninitialized = await RemovalFixture.CreateAsync();
        await uninitialized.GitAsync("submodule", "deinit", "--force", "--", uninitialized.ModulePath);
        GitSubmoduleRemovalSelection pending = await uninitialized.SelectionAsync();
        Check(!pending.IsInitialized, "Fixture must represent an uninitialized module without a local registration.");
        string retained = uninitialized.ModuleSnapshot(); await uninitialized.Repository.RemoveSubmoduleAsync(pending);
        Check(uninitialized.ModuleSnapshot() == retained, "An uninitialized module's retained internal repository must survive removal.");
    }

    private static async Task LiteralAsync()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync("name.λ \"'", "-literal[1]*λ\nsub dir");
        await f.AddAsync("neighbor", "literal1xλ"); await f.Repository.StageAllAsync(); await f.Repository.CommitAsync("Neighbor");
        string before = f.ModuleSnapshot();
        await f.Repository.RemoveSubmoduleAsync(await f.SelectionAsync());
        Check(f.ModuleSnapshot() == before && (await f.Repository.GetSubmoduleRemovalSelectionsAsync()).Single().Path == "literal1xλ", "Difficult paths and subsection names must remain literal and exact.");
    }

    private static async Task SelectionAsync()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync();
        GitSubmoduleRemovalSelection selected = await f.SelectionAsync();
        string before = await f.SnapshotAsync();
        await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected with { RepositoryPath = f.Root }));
        await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected with { Path = "../outside" }));
        Check(await f.SnapshotAsync() == before, "Foreign/forged selections must not mutate live state.");
        await File.AppendAllTextAsync(f.File(".gitmodules"), "# externally changed\n"); before = await f.SnapshotAsync();
        await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == before, "Stale registration must fail before mutation.");
        selected = await f.SelectionAsync();
        await f.GitAsync("update-index", "--cacheinfo", "160000," + (await f.Repository.ResolveRevisionAsync("HEAD")) + "," + f.ModulePath);
        before = await f.SnapshotAsync(); await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == before, "Changed gitlink identity must fail before mutation.");
    }

    private static async Task SafetyAsync()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync();
        GitSubmoduleRemovalSelection selected = await f.SelectionAsync();
        string moved = Path.Combine(f.Root, "saved-module"); Directory.Move(f.ModuleDirectory, moved); Directory.CreateSymbolicLink(f.ModuleDirectory, moved);
        string before = await f.SnapshotAsync(); await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == before, "Symlink module location cannot be traversed.");
        Directory.Delete(f.ModuleDirectory); Directory.Move(moved, f.ModuleDirectory);
        string external = Path.Combine(f.Root, "external.gitmodules"); File.Move(f.File(".gitmodules"), external); File.CreateSymbolicLink(f.File(".gitmodules"), external);
        before = await f.SnapshotAsync(); await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == before && File.ReadAllText(external).Contains("[submodule"), "Linked .gitmodules cannot alter external bytes.");
        File.Delete(f.File(".gitmodules")); File.Move(external, f.File(".gitmodules"));
        await File.AppendAllTextAsync(f.File(".gitmodules"), "[submodule \"duplicate\"]\npath = " + f.ModulePath + "\nurl = bogus\n");
        before = await f.SnapshotAsync(); await ExpectFailureAsync(() => f.Repository.GetSubmoduleRemovalSelectionsAsync());
        Check(await f.SnapshotAsync() == before, "Duplicate path registrations must fail closed.");
        await File.WriteAllTextAsync(f.File(".gitmodules"), "[invalid\n"); before = await f.SnapshotAsync();
        await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == before, "Malformed config must fail before mutation.");
        await File.WriteAllTextAsync(f.File(".gitmodules"), "[submodule \"bad\nname\"]\npath = module\nurl = bogus\n"); before = await f.SnapshotAsync();
        await ExpectFailureAsync(() => f.Repository.GetSubmoduleRemovalSelectionsAsync());
        await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected with { Name = "bad\nname" }));
        Check(await f.SnapshotAsync() == before, "A subsection containing a literal newline is invalid native Git config and cannot mutate registrations.");
    }

    private static async Task IndexSafetyAsync()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync();
        string index = (await f.GitAsync("rev-parse", "--path-format=absolute", "--git-path", "index")).StandardOutput.TrimEnd('\n');
        GitSubmoduleRemovalSelection selected = await f.SelectionAsync();
        await File.WriteAllTextAsync(index + ".lock", "someone else's lock");
        string before = await f.SnapshotAsync(); await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == before && File.ReadAllText(index + ".lock") == "someone else's lock", "Never remove or steal another Git lock.");
        File.Delete(index + ".lock");
        GitRepository module = new(f.ModuleDirectory);
        await module.SetConfigAsync("user.name", "Removal Test"); await module.SetConfigAsync("user.email", "removal@example.invalid"); await module.SetConfigAsync("commit.gpgsign", "false"); await module.SetConfigAsync("core.hooksPath", "/dev/null");
        await File.WriteAllBytesAsync(f.ModuleFile("source.bin"), [1, 2, 3]); await module.StageAllAsync(); await module.CommitAsync("Module staged version");
        string stagedHead = await module.ResolveRevisionAsync("HEAD"); await f.Repository.StageAsync([f.ModulePath]);
        await File.WriteAllBytesAsync(f.ModuleFile("source.bin"), [4, 5, 6]); await module.StageAllAsync(); await module.CommitAsync("Module worktree version");
        selected = await f.SelectionAsync(); before = await f.SnapshotAsync();
        await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == before, "A gitlink different from both HEAD and the module worktree must retain native cached-rm safety.");
        await module.CheckoutAsync(stagedHead, detached: true);
        await File.AppendAllTextAsync(f.File(".gitmodules"), "# staged\n"); await f.Repository.StageAsync([".gitmodules"]);
        await File.AppendAllTextAsync(f.File(".gitmodules"), "# unstaged\n"); selected = await f.SelectionAsync(); before = await f.SnapshotAsync();
        await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == before, "Last .gitmodules cached-rm must retain native staged-divergence safety, including the previously prepared gitlink.");
    }

    private static async Task CancellationAsync()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync();
        GitSubmoduleRemovalSelection selected = await f.SelectionAsync(); string before = await f.SnapshotAsync();
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected, cancelled.Token));
        Check(await f.SnapshotAsync() == before, "Pre-cancellation cannot alter state.");
        string control = Path.Combine(f.Root, "control"); Directory.CreateDirectory(control);
        string script = Path.Combine(control, "git"); string marker = Path.Combine(control, "entered");
        await File.WriteAllTextAsync(script, "#!/bin/sh\ncase \" $* \" in *\" rm --cached -- module \"*) /usr/bin/git \"$@\" || exit $?; touch '" + marker + "'; sleep 30; exit 0;; esac\nexec /usr/bin/git \"$@\"\n");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        GitRepository slow = new(f.Repository.Path, new GitRunner(script));
        string[] existingPreparations = Directory.GetDirectories(Path.GetTempPath(), "gitextensions-submodule-remove-*");
        using CancellationTokenSource cts = new(); Task removal = slow.RemoveSubmoduleAsync(selected, cts.Token);
        string? preparation = null;
        try
        {
            for (int i = 0; i < 300 && !File.Exists(marker) && !removal.IsCompleted; i++) { await Task.Delay(10); }
            Check(File.Exists(marker), "Cancellation must reach the wrapper after Git really removed the selected gitlink from the preparation index.");
            preparation = Directory.GetDirectories(Path.GetTempPath(), "gitextensions-submodule-remove-*").Except(existingPreparations, StringComparer.Ordinal).Single();
            if (!OperatingSystem.IsWindows())
            {
                Check(File.GetUnixFileMode(preparation) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                    "Prepared copies of private repository configuration must be inside an atomically created 0700 directory.");
            }
        }
        finally { cts.Cancel(); await ExpectFailureAsync(() => removal); }
        Check(preparation is not null, "The preparation directory must have been observed.");
        Check(!Directory.Exists(preparation), "Cancellation must clean the private preparation directory.");
        Check(await f.SnapshotAsync() == before && !File.Exists(f.File(".gitmodules.lock")), "In-flight cancellation releases owned locks and changes no live state.");
        await File.WriteAllTextAsync(script, "#!/bin/sh\ncase \" $* \" in *\" rm --cached -- module \"*) /usr/bin/git \"$@\" || exit $?; echo preparation-failed >&2; exit 67;; esac\nexec /usr/bin/git \"$@\"\n");
        await ExpectFailureAsync(() => new GitRepository(f.Repository.Path, new GitRunner(script)).RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == before, "Native error after prepared gitlink removal must have no live effect.");
    }

    private static async Task InProgressAsync()
    {
        using RemovalFixture f = await RemovalFixture.CreateAsync(); GitSubmoduleRemovalSelection selected = await f.SelectionAsync();
        foreach (string marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer" })
        {
            string location = (await f.GitAsync("rev-parse", "--path-format=absolute", "--git-path", marker)).StandardOutput.TrimEnd('\n');
            if (marker.Contains("HEAD")) { await File.WriteAllTextAsync(location, await f.Repository.ResolveRevisionAsync("HEAD")); } else { Directory.CreateDirectory(location); }
            string before = await f.SnapshotAsync(); await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
            Check(await f.SnapshotAsync() == before, "In-progress " + marker + " must remain untouched.");
            if (File.Exists(location)) { File.Delete(location); } else { Directory.Delete(location); }
        }
        string blob = (await f.GitAsync("rev-parse", ":parent.txt")).StandardOutput.Trim();
        await f.GitAsync("update-index", "--force-remove", "--", "parent.txt");
        await new GitRunner().RunAsync(f.Repository.Path, ["update-index", "--index-info"], standardInput: "100644 " + blob + " 1\tparent.txt\n100644 " + blob + " 2\tparent.txt\n");
        string conflicted = await f.SnapshotAsync(); await ExpectFailureAsync(() => f.Repository.RemoveSubmoduleAsync(selected));
        Check(await f.SnapshotAsync() == conflicted, "Unrelated unresolved index entries must block removal.");
    }

    private static async Task AlternateIndexesAsync()
    {
        using (RemovalFixture f = await RemovalFixture.CreateAsync())
        {
            await f.GitAsync("update-index", "--split-index");
            Check((await f.GitAsync("rev-parse", "--shared-index-path")).StandardOutput.Trim().Length > 0, "Fixture must have a real shared index.");
            string before = f.ModuleSnapshot(); await f.Repository.RemoveSubmoduleAsync(await f.SelectionAsync());
            Check((await f.Repository.GetSubmoduleRemovalSelectionsAsync()).Count == 0 && f.ModuleSnapshot() == before, "Split index must keep its shared-index dependencies usable.");
        }
        using (RemovalFixture f = await RemovalFixture.CreateAsync())
        {
            string linked = Path.Combine(f.Root, "linked worktree"); await f.GitAsync("worktree", "add", "-b", "linked", "--", linked);
            GitRepository repo = new(linked); GitSubmoduleRemovalSelection selected = (await repo.GetSubmoduleRemovalSelectionsAsync()).Single();
            string originalIndex = (await f.GitAsync("ls-files", "--stage", "-z")).StandardOutput;
            string moduleBefore = f.ModuleSnapshot(); await repo.RemoveSubmoduleAsync(selected);
            Check((await repo.GetSubmoduleRemovalSelectionsAsync()).Count == 0, "Linked worktree uses its own index.");
            Check((await f.GitAsync("ls-files", "--stage", "-z")).StandardOutput == originalIndex && f.ModuleSnapshot() == moduleBefore, "Primary index and module data must remain unchanged.");
        }
    }

    internal sealed class RemovalFixture : IDisposable
    {
        private RemovalFixture(string root, GitRepository repository, string source, string name, string path, string head)
        { Root = root; Repository = repository; Source = source; Name = name; ModulePath = path; ModuleHead = head; }
        internal string Root { get; }
        internal GitRepository Repository { get; }
        internal string Source { get; }
        internal string Name { get; }
        internal string ModulePath { get; }
        internal string ModuleHead { get; }
        internal string ModuleDirectory => File(ModulePath);
        internal string File(string path) => Path.Combine(Repository.Path, path);
        internal string ModuleFile(string path) => Path.Combine(ModuleDirectory, path);
        internal Task<GitSubmoduleRemovalSelection> SelectionAsync() => SelectAsync();
        private async Task<GitSubmoduleRemovalSelection> SelectAsync() => (await Repository.GetSubmoduleRemovalSelectionsAsync()).Single(item => item.Name == Name);
        internal async Task AddAsync(string name, string path)
        {
            // git-submodule's shell implementation cannot initially add a path beginning '-'.
            string initial = path.StartsWith('-') ? "seed" + path : path;
            await Repository.RunAsync(["-c", "protocol.file.allow=always", "submodule", "add", "--name", name, "--", Source, initial]);
            if (initial != path) { await GitAsync("mv", "--", initial, path); }
        }
        internal async Task<GitResult> GitAsync(params string[] args) => await GitAsync(args, []);
        internal async Task<GitResult> GitAsync(string a, string b, string c, string d, int[] allowed) => await GitAsync([a, b, c, d], allowed);
        private async Task<GitResult> GitAsync(string[] args, int[] allowed)
        {
            GitResult result = await new GitRunner().RunAsync(Repository.Path, args);
            if (!result.Success && !allowed.Contains(result.ExitCode)) { throw new GitCommandException(args, result); } return result;
        }
        internal string ModuleSnapshot()
        {
            string modern = Path.Combine(Repository.Path, ".git", "modules", Name);
            return Tree(ModuleDirectory) + (Directory.Exists(modern) ? Tree(modern) : "");
        }
        private static string Tree(string root) => string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(file)))));
        internal async Task<string> SnapshotAsync()
        {
            string index = (await GitAsync("ls-files", "--stage", "-z")).StandardOutput;
            string config = (await GitAsync("rev-parse", "--path-format=absolute", "--git-path", "config")).StandardOutput.TrimEnd('\n');
            return index + "\n" + Convert.ToHexString(System.IO.File.ReadAllBytes(config)) + "\n" + (await GitAsync("show-ref")).StandardOutput
                + "\n" + string.Join("\n", Directory.GetFiles(Repository.Path, "*", SearchOption.AllDirectories).Where(file => !file.StartsWith(Path.Combine(Repository.Path, ".git") + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
                    .Select(file => Path.GetRelativePath(Repository.Path, file) + ":" + (new FileInfo(file).LinkTarget ?? Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(file))))));
        }
        internal static async Task<RemovalFixture> CreateAsync(string name = "module", string path = "module")
        {
            string root = Path.Combine(Path.GetTempPath(), "gitextensions-submodule-removal-" + Guid.NewGuid().ToString("N"));
            GitRepository source = await GitRepository.InitAsync(Path.Combine(root, "source"));
            GitRepository repo = await GitRepository.InitAsync(Path.Combine(root, "repository"));
            foreach (GitRepository item in new[] { source, repo })
            { await item.SetConfigAsync("user.name", "Removal Test"); await item.SetConfigAsync("user.email", "removal@example.invalid"); await item.SetConfigAsync("commit.gpgsign", "false"); await item.SetConfigAsync("core.hooksPath", "/dev/null"); }
            await System.IO.File.WriteAllBytesAsync(Path.Combine(source.Path, "source.bin"), [0, 128, 255]); await source.StageAllAsync(); await source.CommitAsync("Source");
            await System.IO.File.WriteAllTextAsync(Path.Combine(repo.Path, "parent.txt"), "parent\n"); await repo.StageAllAsync(); await repo.CommitAsync("Parent");
            RemovalFixture fixture = new(root, repo, source.Path, name, path, await source.ResolveRevisionAsync("HEAD"));
            await fixture.AddAsync(name, path); await repo.StageAllAsync(); await repo.CommitAsync("Add module"); return fixture;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
