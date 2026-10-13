using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GitCommands;
using GitExtensions.Linux;

internal static class CleanScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("clean default preview is native all directories and completely readonly", DefaultPreviewAsync),
        ("clean previews all ignored and nonignored modes match native output", PreviewModesAsync),
        ("clean execution modes match native files while preserving tracked index refs and config", ExecuteModesAsync),
        ("clean directories toggle and scoped directory paths retain native behavior", DirectoriesAsync),
        ("clean include lines preserve spaces quotes wildcards and option-looking names as argv", IncludesAsync),
        ("clean exclusions convert once and preserve native all ignored nonignored semantics", ExclusionsAsync),
        ("clean options and caller-owned arrays are snapshotted before discovery awaits", SnapshotAsync),
        ("clean validation bare and pre-cancellation reject without changing materials", ValidationAsync),
        ("clean execution uses current files rather than a stale preview deletion list", CurrentFilesAsync),
        ("clean one force protects nested repositories and never follows untracked symlinks", NestedRepositoryAsync),
        ("clean native recursive traversal covers initialized nested but not inactive submodules", RecursiveAsync),
        ("clean recursive include values stay data and excludes apply only to parent", RecursiveArgumentsAsync),
        ("clean nonzero parent still invokes native recursive cleanup and retains both results", ParentErrorAsync),
        ("clean native child failure retains parent effects and foreach stop behavior", ChildErrorAsync),
        ("cancelled clean preview terminates descendants with no fabricated output or effects", CancelPreviewAsync),
        ("cancelled clean after real parent deletion never starts recursive children", CancelExecuteAsync),
        ("unlaunchable clean commands retain unknown outcomes and conservative mutation marker", LaunchFailureAsync),
    ];

    private static async Task DefaultPreviewAsync()
    {
        using RepositoryFixture fixture = await FilesFixture();
        string before = Materials(fixture.Repository.Path);
        GitCleanResult preview = await fixture.Repository.PreviewCleanAsync();
        GitResult native = await Git(fixture.Repository, ["clean", "-x", "-d", "--dry-run", "--"]);
        Check(preview.Success && preview.IsPreview && !preview.MutationStarted && !preview.Cancelled && preview.Commands.Count == 1,
            "Default Preview is a completed readonly parent command, without a recursive command.");
        Matches(preview.Commands[0], native, GitCleanCommandScope.Worktree);
        Check(preview.Commands[0].Arguments.SequenceEqual(["clean", "-x", "-d", "--dry-run", "--"]), "Preview uses original All/directories defaults, no force/literal option.");
        Check(Materials(fixture.Repository.Path) == before, "Preview preserves every file, directory, raw index, config, ref and object byte/mode.");
    }

    private static async Task PreviewModesAsync()
    {
        using RepositoryFixture fixture = await FilesFixture();
        string before = Materials(fixture.Repository.Path);
        foreach (CleanMode mode in Enum.GetValues<CleanMode>())
        {
            GitCleanOptions options = new(mode);
            GitCleanResult preview = await fixture.Repository.PreviewCleanAsync(options);
            GitResult native = await Git(fixture.Repository, NativeArguments(options, preview: true));
            Matches(preview.Commands.Single(), native, GitCleanCommandScope.Worktree);
            Check(preview.Success && !preview.MutationStarted && !preview.Commands[0].Arguments.Contains("-f"), "Each preview has native dry-run semantics without a destructive force flag.");
            string output = preview.Commands[0].StandardOutput!;
            Check(output.Contains("ignored.ignored", StringComparison.Ordinal) == (mode != CleanMode.OnlyNonIgnored)
                && output.Contains("ordinary.txt", StringComparison.Ordinal) == (mode != CleanMode.OnlyIgnored), "Modes select actual ignored/nonignored candidates, not a filtered status list.");
        }
        Check(Materials(fixture.Repository.Path) == before, "All three preview modes preserve the repository preimage exactly.");
    }

    private static async Task ExecuteModesAsync()
    {
        foreach (CleanMode mode in Enum.GetValues<CleanMode>())
        {
            using RepositoryFixture fixture = await FilesFixture();
            GitCleanOptions options = new(mode);
            GitRepository twin = Twin(fixture);
            string metadata = Metadata(fixture.Repository), tracked = TrackedFiles(fixture.Repository);
            GitCleanResult cleaned = await fixture.Repository.CleanWorkingDirectoryAsync(options);
            GitResult native = await Git(twin, NativeArguments(options, preview: false));
            Matches(cleaned.Commands.Single(), native, GitCleanCommandScope.Worktree);
            Check(cleaned.Success && cleaned.MutationStarted && !cleaned.IsPreview && !cleaned.Cancelled, "Cleanup returns the actual successful mutation-capable command.");
            Check(Materials(fixture.Repository.Path) == Materials(twin.Path), "Remaining files, directories and Git bytes/modes match native cleanup from an identical preimage.");
            Check(Metadata(fixture.Repository) == metadata && TrackedFiles(fixture.Repository) == tracked, "All modes preserve staged/unstaged tracked bytes, raw index, refs, objects, configuration and journals.");
            Check(File.Exists(fixture.File("ignored.ignored")) == (mode == CleanMode.OnlyNonIgnored)
                && File.Exists(fixture.File("ordinary.txt")) == (mode == CleanMode.OnlyIgnored), "The deletion effects agree with the mode, including ignored data.");
        }
    }

    private static async Task DirectoriesAsync()
    {
        foreach (bool directories in new[] { false, true })
        {
            using RepositoryFixture fixture = await FilesFixture();
            GitCleanOptions options = new(RemoveDirectories: directories);
            GitRepository twin = Twin(fixture);
            GitCleanResult result = await fixture.Repository.CleanWorkingDirectoryAsync(options);
            Matches(result.Commands.Single(), await Git(twin, NativeArguments(options, false)), GitCleanCommandScope.Worktree);
            Check(Materials(fixture.Repository.Path) == Materials(twin.Path) && Directory.Exists(fixture.File("ordinary-dir")) == !directories,
                "The unscoped directories checkbox has the same actual effect as native Git -d.");
        }
        using RepositoryFixture scoped = await FilesFixture();
        GitCleanOptions scopedOptions = new(RemoveDirectories: false) { IncludePaths = ["ordinary-dir/"] };
        GitRepository oracle = Twin(scoped);
        GitCleanResult scopedResult = await scoped.Repository.CleanWorkingDirectoryAsync(scopedOptions);
        Matches(scopedResult.Commands.Single(), await Git(oracle, NativeArguments(scopedOptions, false)), GitCleanCommandScope.Worktree);
        Check(Materials(scoped.Repository.Path) == Materials(oracle.Path), "A directory pathspec retains Git's native behavior even when -d is omitted.");
    }

    private static async Task IncludesAsync()
    {
        using RepositoryFixture fixture = await FilesFixture();
        foreach (string name in new[] { " leading trailing ", " ", "quote'\";$(touch canary)", "-danger.txt", "α.tmp", "β.tmp", "keep.log" }) { fixture.Write(name, name + "\n"); }
        GitCleanOptions absolute = new() { IncludePaths = [fixture.File(" leading trailing ")] };
        GitCleanResult absolutePreview = await fixture.Repository.PreviewCleanAsync(absolute);
        Matches(absolutePreview.Commands.Single(), await Git(fixture.Repository, NativeArguments(absolute, true)), GitCleanCommandScope.Worktree);
        Check(absolutePreview.Commands[0].Arguments[^1] == fixture.File(" leading trailing ") && File.Exists(fixture.File(" leading trailing ")),
            "Picker-style absolute inclusions remain exact arguments within the current worktree.");
        GitCleanOptions options = new() { IncludePaths = ["", " leading trailing ", " ", "quote'\";$(touch canary)", "-danger.txt", "*.tmp"] };
        GitRepository twin = Twin(fixture);
        string metadata = Metadata(fixture.Repository);
        GitCleanResult result = await fixture.Repository.CleanWorkingDirectoryAsync(options);
        Matches(result.Commands.Single(), await Git(twin, NativeArguments(options, false)), GitCleanCommandScope.Worktree);
        Check(Materials(fixture.Repository.Path) == Materials(twin.Path), "Multiple paths remain independent argv data and native Git performs its own wildcard matching.");
        Check(!File.Exists(fixture.File(" leading trailing ")) && !File.Exists(fixture.File(" ")) && !File.Exists(fixture.File("-danger.txt"))
            && !File.Exists(fixture.File("α.tmp")) && File.Exists(fixture.File("keep.log")), "Empty lines disappear, whitespace-only/nontrimmed names stay literal and option-looking filenames are data.");
        Check(!File.Exists(fixture.File("canary")) && Metadata(fixture.Repository) == metadata, "Shell syntax in the selected filename executes no program or Git mutation outside native cleanup.");
    }

    private static async Task ExclusionsAsync()
    {
        foreach (CleanMode mode in Enum.GetValues<CleanMode>())
        {
            using RepositoryFixture fixture = await FilesFixture();
            fixture.Write("keep me.tmp", "space\n"); fixture.Write("keepXme.tmp", "wildcard\n");
            fixture.Write("scope/excluded.txt", "excluded\n"); fixture.Write("scope/remove.txt", "remove\n");
            GitCleanOptions options = new(mode) { ExcludePaths = ["", "keep me.tmp", "scope" + System.IO.Path.DirectorySeparatorChar + "excluded.txt"] };
            GitRepository twin = Twin(fixture);
            GitCleanResult result = await fixture.Repository.CleanWorkingDirectoryAsync(options);
            Matches(result.Commands.Single(), await Git(twin, NativeArguments(options, false)), GitCleanCommandScope.Worktree);
            Check(Materials(fixture.Repository.Path) == Materials(twin.Path), "--exclude interpretation, including -X, is native ignore-rule semantics rather than an invented post-filter.");
            Check(result.Commands[0].Arguments.Contains("--exclude=keep?me.tmp") && result.Commands[0].Arguments.Contains("--exclude=scope/excluded.txt")
                && !result.Commands[0].Arguments.Contains("--exclude="), "Only Core transforms nonempty exclusion lines, exactly once.");
        }
        if (OperatingSystem.IsLinux())
        {
            using RepositoryFixture fixture = await FilesFixture();
            fixture.Write("scope\\excluded.txt", "literal backslash\n"); fixture.Write("scope/excluded.txt", "slash neighbour\n");
            GitCleanOptions options = new() { ExcludePaths = ["scope\\\\excluded.txt"] };
            GitRepository twin = Twin(fixture);
            GitCleanResult result = await fixture.Repository.CleanWorkingDirectoryAsync(options);
            Matches(result.Commands.Single(), await Git(twin, ["clean", "-x", "-d", "-f", "--exclude=scope\\\\excluded.txt", "--"]), GitCleanCommandScope.Worktree);
            Check(result.Success && result.Commands[0].Arguments.Contains("--exclude=scope\\\\excluded.txt")
                && File.Exists(fixture.File("scope\\excluded.txt")) && !File.Exists(fixture.File("scope/excluded.txt"))
                && Materials(fixture.Repository.Path) == Materials(twin.Path),
                "Linux backslashes retain their native ignore-pattern escaping; a neighbouring slash path is not accidentally excluded instead.");
        }
    }

    private static async Task SnapshotAsync()
    {
        RequireLinux();
        using RepositoryFixture fixture = await FilesFixture();
        string ready = System.IO.Path.Combine(fixture.Root, "discovery-ready"), release = System.IO.Path.Combine(fixture.Root, "discovery-release");
        string wrapper = Wrapper(fixture, "snapshot-git", "case \"$*\" in\n*rev-parse*) if [ ! -e " + Quote(ready) + " ]; then echo ready > " + Quote(ready)
            + "; while [ ! -e " + Quote(release) + " ]; do sleep 0.02; done; fi;;\nesac\nexec " + Quote(GitExecutable()) + " \"$@\"\n");
        List<string> includes = ["ordinary.txt"], excludes = ["protected.txt"];
        GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
        Task<GitCleanResult> operation = repository.PreviewCleanAsync(new() { IncludePaths = includes, ExcludePaths = excludes });
        try
        {
            await WaitFor(() => File.Exists(ready), TimeSpan.FromSeconds(10));
            includes.Clear(); includes.Add("ignored.ignored"); excludes.Clear(); excludes.Add("ordinary.txt");
            File.WriteAllText(release, "release");
            GitCleanResult result = await operation.WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.Success && result.Commands.Single().Arguments.SequenceEqual(["clean", "-x", "-d", "--dry-run", "--exclude=protected.txt", "--", "ordinary.txt"])
                && result.Commands[0].StandardOutput!.Contains("ordinary.txt", StringComparison.Ordinal), "Caller-owned list changes during discovery do not change the snapshotted operation.");
            Check(File.Exists(fixture.File("ordinary.txt")) && File.Exists(fixture.File("ignored.ignored")), "The snapshot proof remains a true readonly preview.");
        }
        finally { File.WriteAllText(release, "release"); await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private static async Task ValidationAsync()
    {
        using RepositoryFixture fixture = await FilesFixture();
        string before = Materials(fixture.Repository.Path);
        await Expect<ArgumentOutOfRangeException>(() => fixture.Repository.CleanWorkingDirectoryAsync(new((CleanMode)999)));
        await Expect<ArgumentException>(() => fixture.Repository.CleanWorkingDirectoryAsync(new() { IncludePaths = ["ordinary.txt", "bad\0path"] }));
        await Expect<ArgumentException>(() => fixture.Repository.PreviewCleanAsync(new() { ExcludePaths = ["bad\0pattern"] }));
        await Expect<ArgumentNullException>(() => fixture.Repository.CleanWorkingDirectoryAsync(new() { IncludePaths = null! }));
        using CancellationTokenSource token = new(); token.Cancel();
        await Expect<OperationCanceledException>(() => fixture.Repository.CleanWorkingDirectoryAsync(cancellationToken: token.Token));
        Check(Materials(fixture.Repository.Path) == before, "Invalid and pre-cancelled requests leave all worktree/Git materials exact.");
        GitRepository bare = await GitRepository.InitAsync(System.IO.Path.Combine(fixture.Root, "bare.git"), bare: true);
        string bareBefore = Materials(bare.Path);
        await Expect<InvalidOperationException>(() => bare.CleanWorkingDirectoryAsync());
        await Expect<InvalidOperationException>(() => bare.PreviewCleanAsync());
        Check(Materials(bare.Path) == bareBefore, "Bare rejection creates no worktree or database state.");
        using RepositoryFixture unborn = await RepositoryFixture.CreateAsync(seed: false);
        unborn.Write("new.txt", "unborn\n");
        string metadata = Metadata(unborn.Repository);
        Check((await unborn.Repository.PreviewCleanAsync()).Success && (await unborn.Repository.CleanWorkingDirectoryAsync()).Success
            && !File.Exists(unborn.File("new.txt")) && Metadata(unborn.Repository) == metadata, "Cleanup requires a worktree, not an existing HEAD or index.");
    }

    private static async Task CurrentFilesAsync()
    {
        using RepositoryFixture fixture = await FilesFixture();
        GitCleanOptions options = new() { IncludePaths = ["*.tmp"] };
        fixture.Write("before.tmp", "before\n");
        GitCleanResult preview = await fixture.Repository.PreviewCleanAsync(options);
        fixture.Write("after.tmp", "after\n");
        GitCleanResult cleaned = await fixture.Repository.CleanWorkingDirectoryAsync(options);
        Check(preview.Success && !preview.Commands[0].StandardOutput!.Contains("after.tmp", StringComparison.Ordinal)
            && cleaned.Success && cleaned.Commands[0].StandardOutput!.Contains("after.tmp", StringComparison.Ordinal)
            && !File.Exists(fixture.File("before.tmp")) && !File.Exists(fixture.File("after.tmp")), "Execution asks Git about current files; the old preview is never used as a deletion list.");
    }

    private static async Task NestedRepositoryAsync()
    {
        RequireLinux();
        using RepositoryFixture fixture = await FilesFixture();
        GitRepository nested = await GitRepository.InitAsync(fixture.File("nested.git"));
        await RepositoryFixture.ConfigureAsync(nested);
        File.WriteAllText(System.IO.Path.Combine(nested.Path, "keep.txt"), "nested\n");
        string protectedRepo = Materials(nested.Path);
        string outside = System.IO.Path.Combine(fixture.Root, "outside-owned.txt"); File.WriteAllText(outside, "outside\n");
        File.CreateSymbolicLink(fixture.File("untracked-link"), outside);
        GitRepository twin = Twin(fixture);
        GitCleanResult result = await fixture.Repository.CleanWorkingDirectoryAsync();
        Matches(result.Commands.Single(), await Git(twin, ["clean", "-x", "-d", "-f", "--"]), GitCleanCommandScope.Worktree);
        Check(Materials(fixture.Repository.Path) == Materials(twin.Path) && Materials(nested.Path) == protectedRepo,
            "One force matches native protection of a nested repository, without adding -ff.");
        Check(!File.Exists(fixture.File("untracked-link")) && File.ReadAllText(outside) == "outside\n", "Deleting an untracked symlink does not follow it into an outside owned file.");
    }

    private static async Task RecursiveAsync()
    {
        using RepositoryFixture fixture = await ModulesFixture();
        GitRepository twin = Twin(fixture);
        GitCleanOptions options = new(CleanSubmodules: true);
        string before = Materials(fixture.Repository.Path), metadata = Metadata(fixture.Repository);
        GitCleanResult preview = await fixture.Repository.PreviewCleanAsync(options);
        Check(preview.Commands.Count == 2 && preview.Success && !preview.MutationStarted, "Recursive preview has parent and native foreach results separately.");
        Matches(preview.Commands[0], await Git(twin, ["clean", "-x", "-d", "--dry-run", "--"]), GitCleanCommandScope.Worktree);
        Matches(preview.Commands[1], await Git(twin, ["submodule", "foreach", "--recursive", "git --no-pager -c color.ui=false -c core.quotepath=false clean -x -d --dry-run --"]), GitCleanCommandScope.Submodules);
        Check(Materials(fixture.Repository.Path) == before, "Preview preserves the complete superproject/submodule material preimage.");
        string output = preview.Commands[1].StandardOutput!;
        Check(output.Contains("Entering 'sub'", StringComparison.Ordinal) && output.Contains("Entering 'sub/nested'", StringComparison.Ordinal)
            && !output.Contains("Entering 'inactive'", StringComparison.Ordinal), "Native traversal includes initialized nested modules and excludes deinitialized ones.");
        GitCleanResult cleaned = await fixture.Repository.CleanWorkingDirectoryAsync(options);
        Matches(cleaned.Commands[0], await Git(twin, ["clean", "-x", "-d", "-f", "--"]), GitCleanCommandScope.Worktree);
        Matches(cleaned.Commands[1], await Git(twin, ["submodule", "foreach", "--recursive", "git --no-pager -c color.ui=false -c core.quotepath=false clean -x -d -f --"]), GitCleanCommandScope.Submodules);
        Check(cleaned.Success && Materials(fixture.Repository.Path) == Materials(twin.Path) && Metadata(fixture.Repository) == metadata,
            "Recursive cleanup matches native visitation/cwd and leaves Git databases, indexes and checked-out tracked files intact.");
    }

    private static async Task RecursiveArgumentsAsync()
    {
        using RepositoryFixture fixture = await ModulesFixture();
        string dangerous = "quote'\";$(touch injected-marker)", unchanged = "keep.txt";
        string[] worktrees = [fixture.Repository.Path, fixture.File("sub"), fixture.File("sub/nested")];
        foreach (string path in worktrees)
        {
            File.WriteAllText(System.IO.Path.Combine(path, dangerous), "literal\n");
            File.WriteAllText(System.IO.Path.Combine(path, "parent-excluded.txt"), "excluded\n");
            File.WriteAllText(System.IO.Path.Combine(path, unchanged), "not selected\n");
        }
        GitRepository twin = Twin(fixture);
        GitCleanOptions options = new(CleanSubmodules: true) { IncludePaths = [dangerous, "parent-excluded.txt", "*.tmp"], ExcludePaths = ["parent-excluded.txt"] };
        GitCleanResult result = await fixture.Repository.CleanWorkingDirectoryAsync(options);
        GitResult parent = await Git(twin, ["clean", "-x", "-d", "-f", "--exclude=parent-excluded.txt", "--", dangerous, "parent-excluded.txt", "*.tmp"]);
        Matches(result.Commands[0], parent, GitCleanCommandScope.Worktree);
        foreach (string relative in new[] { "sub", "sub/nested" })
        {
            await Git(new GitRepository(System.IO.Path.Combine(twin.Path, relative)), ["clean", "-x", "-d", "-f", "--", dangerous, "parent-excluded.txt", "*.tmp"]);
        }
        Check(result.Success && result.Commands.Count == 2 && Materials(fixture.Repository.Path) == Materials(twin.Path),
            "Quoted environment expansion transports identical raw include values to each child; excluded parent files are not implicitly protected in children.");
        Check(File.Exists(fixture.File("parent-excluded.txt")) && !File.Exists(fixture.File("sub/parent-excluded.txt")), "The historical absence of child exclusions is observable.");
        Check(worktrees.All(path => File.Exists(System.IO.Path.Combine(path, unchanged)) && !File.Exists(System.IO.Path.Combine(path, "injected-marker"))),
            "Unselected files survive and shell-looking pattern values never run a command in any worktree.");
        Check(!result.Commands[1].Arguments[^1].Contains(dangerous, StringComparison.Ordinal)
            && result.Commands[1].Arguments[^1].Contains("\"${GITEXTENSIONS_CLEAN_INCLUDE_0}\"", StringComparison.Ordinal),
            "The foreach script contains only fixed operations and variable references, not user content.");
    }

    private static async Task ParentErrorAsync()
    {
        RequireLinux();
        using RepositoryFixture fixture = await ModulesFixture();
        string git = GitExecutable();
        string wrapper = Wrapper(fixture, "parent-error-git", CommandSelector + "case \"$clean_command\" in\nclean) exec " + Quote(git)
            + " clean --invalid-option-for-clean-fixture;;\n*) exec " + Quote(git) + " \"$@\";;\nesac\n");
        GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
        GitCleanResult result = await repository.CleanWorkingDirectoryAsync(new(CleanSubmodules: true));
        GitResult expected = await Git(fixture.Repository, ["clean", "--invalid-option-for-clean-fixture"], requireSuccess: false);
        Matches(result.Commands[0], expected, GitCleanCommandScope.Worktree);
        Check(!result.Success && result.MutationStarted && !result.Cancelled && result.Commands.Count == 2 && result.Commands[1].Success
            && File.Exists(fixture.File("ordinary.txt")) && !File.Exists(fixture.File("sub/ordinary.txt")) && !File.Exists(fixture.File("sub/nested/ordinary.txt")),
            "A real native parent failure does not suppress the separately successful native recursion; effects are not counted as all-or-nothing.");
    }

    private static async Task ChildErrorAsync()
    {
        RequireLinux();
        using RepositoryFixture fixture = await ModulesFixture();
        string git = GitExecutable();
        string wrapper = Wrapper(fixture, "child-error-git", CommandSelector + "case \"$clean_command\" in\nsubmodule) exec " + Quote(git)
            + " submodule foreach --recursive 'git clean --invalid-option-for-clean-fixture';;\n*) exec " + Quote(git) + " \"$@\";;\nesac\n");
        GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
        GitCleanResult result = await repository.CleanWorkingDirectoryAsync(new(CleanSubmodules: true));
        GitResult expected = await Git(fixture.Repository, ["submodule", "foreach", "--recursive", "git clean --invalid-option-for-clean-fixture"], requireSuccess: false);
        Matches(result.Commands[1], expected, GitCleanCommandScope.Submodules);
        Check(!result.Success && result.Commands[0].Success && result.Commands[1].ExitCode != 0 && result.MutationStarted
            && !File.Exists(fixture.File("ordinary.txt")) && File.Exists(fixture.File("sub/ordinary.txt")) && File.Exists(fixture.File("sub/nested/ordinary.txt")),
            "A real child failure preserves parent effects and native foreach stops before the nested child's cleanup.");
        Check(!result.Commands[1].StandardOutput!.Contains("Entering 'sub/nested'", StringComparison.Ordinal), "The failing child does not silently permit further native traversal.");
    }

    private static Task CancelPreviewAsync() => CancelAsync(preview: true);
    private static Task CancelExecuteAsync() => CancelAsync(preview: false);
    private static async Task CancelAsync(bool preview)
    {
        RequireLinux();
        using RepositoryFixture fixture = await ModulesFixture();
        string ready = System.IO.Path.Combine(fixture.Root, "clean-ready"), pidPath = System.IO.Path.Combine(fixture.Root, "clean-child-pid"), foreachPath = System.IO.Path.Combine(fixture.Root, "foreach-started");
        string git = GitExecutable();
        string wrapper = Wrapper(fixture, "cancel-git", CommandSelector + "case \"$clean_command\" in\nclean) " + Quote(git) + " \"$@\" || exit $?; sleep 60 & echo $! > "
            + Quote(pidPath) + "; echo ready > " + Quote(ready) + "; wait;;\nsubmodule) echo started > " + Quote(foreachPath)
            + "; exec " + Quote(git) + " \"$@\";;\n*) exec " + Quote(git) + " \"$@\";;\nesac\n");
        string before = Materials(fixture.Repository.Path), metadata = Metadata(fixture.Repository);
        GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
        using CancellationTokenSource cancel = new();
        Task<GitCleanResult> operation = preview ? repository.PreviewCleanAsync(new(CleanSubmodules: true), cancel.Token)
            : repository.CleanWorkingDirectoryAsync(new(CleanSubmodules: true), cancel.Token);
        int child = 0;
        try
        {
            await WaitFor(() => File.Exists(ready), TimeSpan.FromSeconds(10));
            child = int.Parse(File.ReadAllText(pidPath).Trim(), CultureInfo.InvariantCulture);
            cancel.Cancel();
            GitCleanResult result = await operation.WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.Cancelled && !result.Success && result.IsPreview == preview && result.MutationStarted == !preview && result.Commands.Count == 1
                && result.Commands[0].Cancelled && result.Commands[0].Result is null && result.Commands[0].ExitCode is null
                && result.Commands[0].StandardOutput is null && result.Commands[0].StandardError is null,
                "An interrupted runner has no fabricated exit/output; conservative mutation state distinguishes Preview from Cleanup.");
            Check(!File.Exists(foreachPath) && File.Exists(fixture.File("sub/ordinary.txt")) && File.Exists(fixture.File("sub/nested/ordinary.txt")),
                "Cancellation prevents the second native command and all child cleanup, even after actual parent effects.");
            Check(Metadata(fixture.Repository) == metadata && (preview ? Materials(fixture.Repository.Path) == before : !File.Exists(fixture.File("ordinary.txt"))),
                "Readonly preview is exact; executed cleanup retains its real parent deletion without changing Git metadata.");
            await WaitFor(() => !LinuxProcessState.IsRunning(child), TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancel.Cancel();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            if (child != 0) { await WaitFor(() => !LinuxProcessState.IsRunning(child), TimeSpan.FromSeconds(5)); }
        }
    }

    private static async Task LaunchFailureAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Executable permission checks require Linux."); }
        using RepositoryFixture fixture = await FilesFixture();
        string wrapper = Wrapper(fixture, "unlaunchable-git", "exec " + Quote(GitExecutable()) + " \"$@\"\n");
        GitRepository repository = new(fixture.Repository.Path, new GitRunner(wrapper));
        await repository.GetInfoAsync();
        string before = Materials(fixture.Repository.Path);
        File.SetUnixFileMode(wrapper, (UnixFileMode)0);
        try
        {
            GitCleanResult result = await repository.CleanWorkingDirectoryAsync(new(CleanSubmodules: true));
            Check(!result.Success && !result.Cancelled && result.MutationStarted && result.Commands.Count == 2
                && result.Commands.All(command => command.Result is null && command.ExitCode is null && command.StandardOutput is null
                    && command.StandardError is null && !string.IsNullOrEmpty(command.Error)),
                "Launch errors retain unknown command outcomes and a conservative mutation marker, not exit0/empty-output success.");
            Check(Materials(fixture.Repository.Path) == before, "The normal Git material oracle confirms that this launch failure actually changed no files.");
        }
        finally { File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    private static async Task<RepositoryFixture> FilesFixture()
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        fixture.Write(".gitignore", "*.ignored\nignored-dir/\n");
        fixture.Write("tracked.txt", "committed\n");
        await Git(fixture.Repository, ["add", "--", ".gitignore", "tracked.txt"]);
        await Git(fixture.Repository, ["commit", "-m", "Cleanup fixture"]);
        fixture.Write("tracked.txt", "staged\n"); await Git(fixture.Repository, ["add", "--", "tracked.txt"]); fixture.Write("tracked.txt", "unstaged\n");
        fixture.Write("ordinary.txt", "ordinary\n"); fixture.Write("ignored.ignored", "ignored\n");
        fixture.Write("ordinary-dir/file.txt", "directory\n"); fixture.Write("ignored-dir/file.txt", "ignored directory\n");
        fixture.Write("from-info.ignored-by-info", "info exclude\n"); File.AppendAllText(fixture.File(".git/info/exclude"), "\n*.ignored-by-info\n");
        return fixture;
    }

    private static async Task<RepositoryFixture> ModulesFixture()
    {
        RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        GitRepository nestedSource = await ModuleSource(fixture, "nested-source"), source = await ModuleSource(fixture, "module-source"), inactiveSource = await ModuleSource(fixture, "inactive-source");
        await Git(source, ["-c", "protocol.file.allow=always", "submodule", "add", "--", nestedSource.Path, "nested"]);
        await Git(source, ["commit", "-m", "Nested module"]);
        await Git(fixture.Repository, ["-c", "protocol.file.allow=always", "submodule", "add", "--", source.Path, "sub"]);
        await Git(fixture.Repository, ["-c", "protocol.file.allow=always", "submodule", "add", "--", inactiveSource.Path, "inactive"]);
        await Git(fixture.Repository, ["commit", "-m", "Modules fixture"]);
        await Git(fixture.Repository, ["-c", "protocol.file.allow=always", "submodule", "update", "--init", "--recursive"]);
        await Git(fixture.Repository, ["submodule", "deinit", "--force", "--", "inactive"]);
        foreach (string directory in new[] { fixture.Repository.Path, fixture.File("sub"), fixture.File("sub/nested") })
        {
            File.WriteAllText(System.IO.Path.Combine(directory, "ordinary.txt"), "ordinary\n");
            File.WriteAllText(System.IO.Path.Combine(directory, "ignored.ignored"), "ignored\n");
            File.WriteAllText(System.IO.Path.Combine(directory, "extra.tmp"), "pattern\n");
            Directory.CreateDirectory(System.IO.Path.Combine(directory, "untracked-dir")); File.WriteAllText(System.IO.Path.Combine(directory, "untracked-dir", "file.txt"), "child\n");
        }
        return fixture;
    }

    private static async Task<GitRepository> ModuleSource(RepositoryFixture fixture, string name)
    {
        GitRepository source = await GitRepository.InitAsync(System.IO.Path.Combine(fixture.Root, name));
        await RepositoryFixture.ConfigureAsync(source);
        File.WriteAllText(System.IO.Path.Combine(source.Path, "seed.txt"), "seed\n");
        File.WriteAllText(System.IO.Path.Combine(source.Path, ".gitignore"), "*.ignored\n");
        await Git(source, ["add", "--", "seed.txt", ".gitignore"]); await Git(source, ["commit", "-m", "Source"]);
        return source;
    }

    private static GitRepository Twin(RepositoryFixture fixture)
    {
        string destination = System.IO.Path.Combine(fixture.Root, "native-oracle");
        CopyDirectory(fixture.Repository.Path, destination); return new(destination);
    }

    private static List<string> NativeArguments(GitCleanOptions options, bool preview)
    {
        List<string> arguments = ["clean"];
        if (options.Mode == CleanMode.All) { arguments.Add("-x"); }
        if (options.Mode == CleanMode.OnlyIgnored) { arguments.Add("-X"); }
        if (options.RemoveDirectories) { arguments.Add("-d"); }
        arguments.Add(preview ? "--dry-run" : "-f");
        foreach (string line in options.ExcludePaths.Where(line => line.Length != 0)) { arguments.Add("--exclude=" + line.Replace(' ', '?').Replace(System.IO.Path.DirectorySeparatorChar, '/')); }
        arguments.Add("--"); arguments.AddRange(options.IncludePaths.Where(line => line.Length != 0)); return arguments;
    }

    private static Task<GitResult> Git(GitRepository repository, IReadOnlyList<string> arguments, bool requireSuccess = true)
        => GitCore(repository, arguments, requireSuccess);
    private static async Task<GitResult> GitCore(GitRepository repository, IReadOnlyList<string> arguments, bool requireSuccess)
    {
        GitResult result = await new GitRunner().RunAsync(repository.Path, ["--no-pager", "-c", "color.ui=false", "-c", "core.quotepath=false", .. arguments],
            environment: new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null" });
        if (requireSuccess) { Check(result.Success, result.StandardError); } return result;
    }
    private static void Matches(GitCleanCommandResult actual, GitResult native, GitCleanCommandScope scope)
    {
        Check(actual.Scope == scope && actual.Result is not null && actual.ExitCode == native.ExitCode && actual.StandardOutput == native.StandardOutput
            && actual.StandardError == native.StandardError && actual.Result.StandardOutputBytes.SequenceEqual(native.StandardOutputBytes)
            && actual.Result.StandardErrorBytes.SequenceEqual(native.StandardErrorBytes), "The command preserves actual native exit, stdout/stderr bytes and scope.");
    }
    private static string Metadata(GitRepository repository) => Materials(System.IO.Path.Combine(repository.Path, ".git"));
    private static string TrackedFiles(GitRepository repository) => JsonSerializer.Serialize(new[] { "seed.txt", "tracked.txt", ".gitignore" }
        .Select(name => name + ":" + Convert.ToHexString(File.ReadAllBytes(System.IO.Path.Combine(repository.Path, name)))).ToArray());

    private static string Materials(string root)
    {
        List<string> entries = [];
        Walk(root);
        return JsonSerializer.Serialize(entries);
        void Walk(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                FileInfo info = new(path); string relative = System.IO.Path.GetRelativePath(root, path);
                if (info.LinkTarget is string target) { entries.Add(relative + ":link:" + target); continue; }
                string mode = OperatingSystem.IsWindows() ? "" : ((int)File.GetUnixFileMode(path)).ToString(CultureInfo.InvariantCulture);
                if (Directory.Exists(path)) { entries.Add(relative + ":dir:" + mode); Walk(path); }
                else { entries.Add(relative + ":" + mode + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))); }
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            string copied = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(path)); FileInfo info = new(path);
            if (info.LinkTarget is string target)
            {
                if (Directory.Exists(path)) { Directory.CreateSymbolicLink(copied, target); }
                else { File.CreateSymbolicLink(copied, target); }
            }
            else if (Directory.Exists(path)) { CopyDirectory(path, copied); }
            else { File.Copy(path, copied); if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(copied, File.GetUnixFileMode(path)); } }
        }
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(destination, File.GetUnixFileMode(source)); }
    }

    private static string GitExecutable() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
        .Select(directory => System.IO.Path.Combine(directory, "git")).First(path => File.Exists(path));
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private const string CommandSelector = "clean_command=\nfor clean_arg in \"$@\"; do\ncase \"$clean_arg\" in clean|submodule|rev-parse) clean_command=$clean_arg; break;; esac\ndone\n";
    private static string Wrapper(RepositoryFixture fixture, string name, string script)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The native wrapper requires Linux."); }
        string path = System.IO.Path.Combine(fixture.Root, name); File.WriteAllText(path, "#!/bin/sh\n" + script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
    }
    private static void RequireLinux() { if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This scenario requires Linux process/file semantics."); } }
    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout) { await Task.Delay(20); }
        Check(condition(), "The native fixture did not reach its bounded readiness condition.");
    }
    private static async Task Expect<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
