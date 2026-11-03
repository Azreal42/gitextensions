using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using GitCommands.Git;
using GitExtensions.Linux;

internal static partial class GitProcessStreamingScenarios
{
    private readonly record struct ProcessIdentity(int Pid, int Parent, int Group, int Session, long Started);
    private static ProcessIdentity Identity(string file)
    {
        string stat = File.ReadAllText(file); int name = stat.IndexOf('('), end = stat.LastIndexOf(')');
        string[] fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new(int.Parse(stat[..name].Trim(), CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture),
            int.Parse(fields[2], CultureInfo.InvariantCulture), int.Parse(fields[3], CultureInfo.InvariantCulture), long.Parse(fields[19], CultureInfo.InvariantCulture));
    }
    private static ProcessIdentity WitnessSupervisor(ProcessIdentity primary)
    {
        ProcessIdentity owner = Identity("/proc/" + primary.Parent + "/stat");
        Check(owner.Parent == Environment.ProcessId && owner.Pid == owner.Group && owner.Pid == owner.Session
            && primary.Started >= owner.Started && LinuxProcessState.IsRunning(owner.Pid)
            && new FileInfo("/proc/" + owner.Pid + "/exe").LinkTarget == Path.Combine(AppContext.BaseDirectory, "gitextensions-posix"),
            "The primary must belong to the actual live, directly spawned private native supervisor.");
        return owner;
    }
    private static string OrphanWrapper(RepositoryFixture fixture)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This fixture witnesses Linux sessions and orphaned pipes."); }
        string wrapper = Path.Combine(fixture.Root, "orphaned Git λ " + Guid.NewGuid().ToString("N"));
        File.WriteAllText(wrapper, "#!/bin/sh\ncase \" $* \" in *' bisect good '*) ;; *) exec /usr/bin/env git \"$@\";; esac\n"
            + "/usr/bin/env git \"$@\"\nstatus=$?\ncat /proc/$$/stat > " + Quote(wrapper + ".parent-stat") + "\n"
            + "( while test ! -f " + Quote(wrapper + ".release") + "; do sleep 0.02; done\n"
            + " /usr/bin/env git log -1 --format=%H\n /usr/bin/env git rev-parse --verify missing-owned-orphan-ref >&2 || :\n sleep 60 ) <&0 &\n"
            + "child=$!\nprintf '%s %s\\n' \"$$\" \"$child\" > " + Quote(wrapper + ".pids") + "\nexit \"$status\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return wrapper;
    }
    private static async Task<(ProcessIdentity Parent, ProcessIdentity Child)> WitnessOrphan(string wrapper)
    {
        await Until(() => File.Exists(wrapper + ".pids") && File.ReadAllText(wrapper + ".pids").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length == 2);
        int[] ids = File.ReadAllText(wrapper + ".pids").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        ProcessIdentity parent = Identity(wrapper + ".parent-stat"), child = Identity("/proc/" + ids[1] + "/stat");
        ProcessIdentity supervisor = WitnessSupervisor(parent);
        Check(parent.Pid == ids[0] && parent.Parent == supervisor.Pid && parent.Group == parent.Pid && parent.Session == parent.Pid,
            "The authenticated exec child must retain its actual PID and private SID/PGID beneath the witnessed supervisor.");
        Check(child.Pid == ids[1] && child.Group == parent.Group && child.Session == parent.Session && child.Started >= parent.Started,
            "The pipe holder must belong to this invocation's private group and session.");
        await Until(() => !LinuxProcessState.IsRunning(parent.Pid));
        Check(LinuxProcessState.IsRunning(child.Pid), "The fixture must have a live orphan after the parent has really exited.");
        return (parent, child);
    }
    private static void CleanupOwnedOrphan(ProcessIdentity? witness)
    {
        if (witness is not ProcessIdentity owned || !LinuxProcessState.IsRunning(owned.Pid)) { return; }
        try
        {
            ProcessIdentity current = Identity("/proc/" + owned.Pid + "/stat");
            if (current.Started != owned.Started || current.Group != owned.Group || current.Session != owned.Session) { return; }
            using Process child = Process.GetProcessById(owned.Pid); child.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    private static async Task OrphanCancellationAsync()
    {
        foreach (bool observed in new[] { false, true })
        {
            using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); await fixture.Repository.StartBisectAsync();
            string head = await Head(fixture), wrapper = OrphanWrapper(fixture); byte[] index = File.ReadAllBytes(fixture.File(".git/index")), seed = File.ReadAllBytes(fixture.File("seed.txt")), config = File.ReadAllBytes(fixture.File(".git/config"));
            Pipes pipes = new(); using CancellationTokenSource cancellation = new(); ProcessIdentity? child = null;
            GitRepository gated = new(fixture.Repository.Path, new GitRunner(wrapper));
            Task<GitResult> command = gated.MarkBisectAsync(GitBisectOption.Good, head, cancellation.Token, observed ? pipes.Receive : null);
            try
            {
                (ProcessIdentity parent, ProcessIdentity holder) = await WitnessOrphan(wrapper); child = holder;
                Check(!command.IsCompleted && (await fixture.Repository.GetBisectStateAsync()).GoodRevisions.Contains(head), "The pipe wait must outlive a real, completed Good effect.");
                cancellation.Cancel();
                try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Late cancellation returned a fabricated Git result."); }
                catch (OperationCanceledException) { }
                await Until(() => !LinuxProcessState.IsRunning(holder.Pid));
                Check(!LinuxProcessState.IsRunning(parent.Pid) && command.IsCanceled, "The parent and orphan must stop before cancellation completes.");
                if (observed) { Check(pipes.Output.Length > 0 && pipes.Error.Length == 0, "Observed native Good bytes were discarded or invented."); }
                GitBisectState state = await fixture.Repository.GetBisectStateAsync();
                Check(state.GoodRevisions.SequenceEqual(new[] { head }) && state.BadRevision is null && await Head(fixture) == head,
                    "Late cancellation changed or rolled back the native Good refs.");
                Check(File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index) && File.ReadAllBytes(fixture.File("seed.txt")).SequenceEqual(seed)
                    && File.ReadAllBytes(fixture.File(".git/config")).SequenceEqual(config), "Late cancellation changed raw index, worktree or config.");
            }
            finally
            {
                cancellation.Cancel(); CleanupOwnedOrphan(child);
                try { await command.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            }
        }
    }
    private static async Task OrphanObserverAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); await fixture.Repository.StartBisectAsync();
        string head = await Head(fixture), wrapper = OrphanWrapper(fixture); Pipes pipes = new(); int armed = 0;
        byte[] index = File.ReadAllBytes(fixture.File(".git/index")), seed = File.ReadAllBytes(fixture.File("seed.txt"));
        InvalidOperationException sentinel = new("late orphan observer witness"); using CancellationTokenSource cleanup = new(); ProcessIdentity? child = null;
        Task<GitResult> command = new GitRepository(fixture.Repository.Path, new GitRunner(wrapper)).MarkBisectAsync(GitBisectOption.Good, head, cleanup.Token, chunk =>
        { pipes.Receive(chunk); if (Volatile.Read(ref armed) == 1) { throw sentinel; } });
        try
        {
            (ProcessIdentity parent, ProcessIdentity holder) = await WitnessOrphan(wrapper); child = holder;
            await Until(() => pipes.Output.Length > 0); Check(!command.IsCompleted, "The late observer test requires an exited parent and live pipe holder.");
            Volatile.Write(ref armed, 1); File.WriteAllText(wrapper + ".release", "release\n");
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("The late observer failure was hidden."); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, sentinel)) { }
            await Until(() => !LinuxProcessState.IsRunning(holder.Pid));
            Check(!LinuxProcessState.IsRunning(parent.Pid) && command.IsFaulted, "The original observer fault must complete only after its orphan stopped.");
            GitBisectState state = await fixture.Repository.GetBisectStateAsync();
            Check(state.GoodRevisions.SequenceEqual(new[] { head }) && state.BadRevision is null && await Head(fixture) == head,
                "A late observer fault dispatched another classification or lost Good.");
            Check(File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index) && File.ReadAllBytes(fixture.File("seed.txt")).SequenceEqual(seed), "The late output fault changed actual file bytes.");
            Check(Encoding.UTF8.GetString(pipes.Output).Contains(head, StringComparison.Ordinal) || pipes.Error.Length > 0,
                "The failing callback must observe the orphan's genuine Git output.");
        }
        finally
        {
            cleanup.Cancel(); CleanupOwnedOrphan(child);
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, sentinel)) { }
            catch (OperationCanceledException) { }
        }
    }
    private static async Task LiteralBisectPathsAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string[] paths = [":(glob)pick*.txt", "literal[one].txt"];
        foreach (string path in paths) { fixture.Write(path, "one\n"); }
        await Git(fixture, ["--literal-pathspecs", "add", "--", .. paths]); await Git(fixture, "commit", "-m", "Literal names"); string first = await Head(fixture);
        fixture.Write("pick-neighbor.txt", "unrelated\n"); await Git(fixture, "add", "--", "pick-neighbor.txt"); await Git(fixture, "commit", "-m", "Neighbor only");
        foreach (string path in paths) { fixture.Write(path, "two\n"); }
        await Git(fixture, ["--literal-pathspecs", "add", "--", .. paths]); await Git(fixture, "commit", "-m", "Literal update"); string last = await Head(fixture);
        string oracle = Path.Combine(fixture.Root, "unobserved oracle");
        GitResult clone = await new GitRunner().RunAsync(fixture.Root, ["clone", "--quiet", "--no-hardlinks", "--", fixture.Repository.Path, oracle]); Check(clone.Success, clone.StandardError);
        GitRepository normal = new(oracle); await RepositoryFixture.ConfigureAsync(normal); Pipes pipes = new();
        GitResult expected = await normal.StartBisectAsync(last, [first], paths);
        GitResult actual = await fixture.Repository.StartBisectAsync(last, [first], paths, outputReceived: pipes.Receive);
        Check(actual.ExitCode == expected.ExitCode && actual.StandardOutputBytes.SequenceEqual(expected.StandardOutputBytes)
            && actual.StandardErrorBytes.SequenceEqual(expected.StandardErrorBytes) && pipes.Output.SequenceEqual(actual.StandardOutputBytes)
            && pipes.Error.SequenceEqual(actual.StandardErrorBytes), "Observed bisect changed native arguments or bytes for literal magic paths.");
        GitBisectState expectedState = await normal.GetBisectStateAsync(), actualState = await fixture.Repository.GetBisectStateAsync();
        Check(actualState.Log == expectedState.Log && actualState.CurrentRevision == expectedState.CurrentRevision && actualState.StartingRevision == expectedState.StartingRevision
            && actualState.IsActive == expectedState.IsActive && actualState.GoodRevisions.SequenceEqual(expectedState.GoodRevisions) && actualState.BadRevision == expectedState.BadRevision
            && actualState.FirstBadRevision == expectedState.FirstBadRevision && actualState.SkippedRevisions.SequenceEqual(expectedState.SkippedRevisions)
            && actualState.NoCheckout == expectedState.NoCheckout && actualState.FirstParent == expectedState.FirstParent,
            "Literal paths must select the same commits and write the same native bisect log.");
        GitResult expectedIndex = await new GitRunner().RunAsync(oracle, ["ls-files", "--stage", "-z"]), actualIndex = await Git(fixture, "ls-files", "--stage", "-z");
        Check(expectedIndex.Success && expectedIndex.StandardOutputBytes.SequenceEqual(actualIndex.StandardOutputBytes), "Observed literal-path bisect changed the selected index entries.");
        foreach (string path in paths) { Check(File.ReadAllBytes(Path.Combine(oracle, path)).SequenceEqual(File.ReadAllBytes(fixture.File(path))), "Observed bisect changed literal worktree bytes."); }
        await normal.ResetBisectAsync(); await fixture.Repository.ResetBisectAsync();
    }
    private static async Task<GitResult> DirectLaunch(string executable, string directory, string[] arguments, string? childPath = null)
    {
        ProcessStartInfo info = new(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
        info.Environment["LC_ALL"] = "C.UTF-8"; if (childPath is not null) { info.Environment["PATH"] = childPath; }
        using Process process = Process.Start(info)!; process.StandardInput.Close(); using MemoryStream stdout = new(), stderr = new();
        Task output = process.StandardOutput.BaseStream.CopyToAsync(stdout), error = process.StandardError.BaseStream.CopyToAsync(stderr);
        await process.WaitForExitAsync(); await Task.WhenAll(output, error);
        return new(process.ExitCode, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray())) { StandardOutputBytes = stdout.ToArray(), StandardErrorBytes = stderr.ToArray() };
    }
    private static string Executable(RepositoryFixture fixture, string directory, string name, string? canary = null)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("These launch oracles exercise Linux executable resolution."); }
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + (canary is null ? "" : "printf 'wrong executable\\n' > " + Quote(canary) + "\n") + "exec /usr/bin/git \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return path;
    }
    private static async Task ParentExecutablePathAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string parent = Path.Combine(fixture.Root, "parent bin λ"), child = Path.Combine(fixture.Root, "child bin");
        string name = "git-literal-" + Guid.NewGuid().ToString("N"), canary = Path.Combine(fixture.Root, "wrong-program-ran"), injection = Path.Combine(fixture.Root, "shell-interpolation-ran");
        string parentExecutable = Executable(fixture, parent, name); Executable(fixture, child, name, canary);
        string? previous = Environment.GetEnvironmentVariable("PATH"); Environment.SetEnvironmentVariable("PATH", parent + ":" + previous);
        try
        {
            string[] arguments = ["log", "-1", "--format=%H;$(touch " + injection + ") [*]"];
            foreach (UnixFileMode mode in new[] { UnixFileMode.UserRead | UnixFileMode.UserExecute,
                UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute })
            {
                if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The mode-selection oracle requires Linux."); }
                File.SetUnixFileMode(parentExecutable, mode);
                GitResult expected = await DirectLaunch(name, fixture.Repository.Path, arguments, child);
                GitResult actual = await new GitRunner(name).RunAsync(fixture.Repository.Path, arguments, environment: new Dictionary<string, string?> { ["PATH"] = child });
                Check(expected.Success && actual.ExitCode == expected.ExitCode && actual.StandardOutputBytes.SequenceEqual(expected.StandardOutputBytes)
                    && actual.StandardErrorBytes.SequenceEqual(expected.StandardErrorBytes) && !File.Exists(canary) && !File.Exists(injection),
                    "The child PATH, executable modes or shell syntax changed literal executable/argv resolution.");
            }
            // An actual native shell is an argv0 oracle; its fixed script does not contain any user input.
            string[] nameArguments = ["-c", "printf '%s\\n' \"$0\""];
            GitResult expectedName = await DirectLaunch("sh", fixture.Repository.Path, nameArguments);
            GitResult actualName = await new GitRunner("sh").RunAsync(fixture.Repository.Path, nameArguments);
            Check(expectedName.Success && actualName.Success && actualName.StandardOutputBytes.SequenceEqual(expectedName.StandardOutputBytes)
                && actualName.StandardErrorBytes.SequenceEqual(expectedName.StandardErrorBytes), "Resolving the executable changed its original argv0.");
            if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The mode-selection oracle requires Linux."); }
            File.SetUnixFileMode(parentExecutable, UnixFileMode.UserRead); Win32Exception? missing = null; int callbacks = 0;
            try { await DirectLaunch(name, fixture.Repository.Path, arguments, child); throw new InvalidOperationException("The native PATH oracle unexpectedly selected a non-executable file."); }
            catch (Win32Exception exception) { missing = exception; }
            try
            {
                await new GitRunner(name).RunAsync(fixture.Repository.Path, arguments, environment: new Dictionary<string, string?> { ["PATH"] = child }, outputReceived: _ => callbacks++);
                throw new InvalidOperationException("An absent parent PATH executable fell back to the conflicting child PATH.");
            }
            catch (Win32Exception exception) { Check(exception.NativeErrorCode == missing!.NativeErrorCode, "PATH selection changed the native missing-executable error."); }
            Check(callbacks == 0 && !File.Exists(canary) && !File.Exists(injection), "Rejected parent PATH selection ran the child executable or invented output.");
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }
    private static async Task RelativeExecutablePathAsync()
    {
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string name = "relative Git λ " + Guid.NewGuid().ToString("N"), canary = Path.Combine(fixture.Root, "wrong-relative-ran");
        Executable(fixture, Path.Combine(fixture.Root, "relative bin"), name); Executable(fixture, Path.Combine(fixture.Repository.Path, "relative bin"), name, canary);
        string before = Directory.GetCurrentDirectory(); Directory.SetCurrentDirectory(fixture.Root);
        try
        {
            string relative = Path.Combine("relative bin", name); string[] arguments = ["log", "-1", "--format=%H"];
            GitResult expected = await DirectLaunch(relative, fixture.Repository.Path, arguments);
            GitResult actual = await new GitRunner(relative).RunAsync(fixture.Repository.Path, arguments);
            Check(expected.Success && actual.Success && actual.StandardOutputBytes.SequenceEqual(expected.StandardOutputBytes)
                && actual.StandardErrorBytes.SequenceEqual(expected.StandardErrorBytes) && !File.Exists(canary), "WorkingDirectory reinterpreted the relative executable path.");
            string? previousPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", "relative bin:" + previousPath);
            try
            {
                // Relative PATH entries are selected in the parent, then interpreted after the native child's chdir.
                GitResult expectedRelativePath = await DirectLaunch(name, fixture.Repository.Path, arguments);
                Check(File.Exists(canary), "The direct Process.Start oracle must exercise its relative PATH/chdir behavior.");
                byte[] expectedCanary = File.ReadAllBytes(canary); File.Delete(canary);
                GitResult actualRelativePath = await new GitRunner(name).RunAsync(fixture.Repository.Path, arguments);
                Check(expectedRelativePath.Success && actualRelativePath.Success && actualRelativePath.StandardOutputBytes.SequenceEqual(expectedRelativePath.StandardOutputBytes)
                    && actualRelativePath.StandardErrorBytes.SequenceEqual(expectedRelativePath.StandardErrorBytes)
                    && File.ReadAllBytes(canary).SequenceEqual(expectedCanary), "Relative PATH resolution diverged from the actual Process.Start/chdir oracle.");
                string scriptName = name + " argv0";
                Executable(fixture, Path.Combine(fixture.Root, "relative bin"), scriptName);
                string childScript = Executable(fixture, Path.Combine(fixture.Repository.Path, "relative bin"), scriptName);
                File.WriteAllText(childScript, "#!/bin/sh\nprintf '%s\\n' \"$0\"\nexec /usr/bin/git \"$@\"\n");
                GitResult expectedScript = await DirectLaunch(scriptName, fixture.Repository.Path, arguments);
                GitResult actualScript = await new GitRunner(scriptName).RunAsync(fixture.Repository.Path, arguments);
                Check(expectedScript.Success && actualScript.Success && expectedScript.StandardOutput.Split('\n')[0] == Path.Combine("relative bin", scriptName)
                    && actualScript.StandardOutputBytes.SequenceEqual(expectedScript.StandardOutputBytes)
                    && actualScript.StandardErrorBytes.SequenceEqual(expectedScript.StandardErrorBytes), "Absolutizing the selected PATH shebang changed the kernel-provided script pathname/$0.");
            }
            finally { Environment.SetEnvironmentVariable("PATH", previousPath); }
        }
        finally { Directory.SetCurrentDirectory(before); }
        string real = Path.Combine(fixture.Root, "real executable directory"), inner = Path.Combine(real, "inner"); Directory.CreateDirectory(inner);
        Executable(fixture, real, name); Executable(fixture, fixture.Root, name, canary); File.Delete(canary);
        string alias = Path.Combine(fixture.Root, "executable link"); Directory.CreateSymbolicLink(alias, inner);
        string absoluteWithLink = Path.Combine(alias, "..", name);
        GitResult expectedLink = await DirectLaunch(absoluteWithLink, fixture.Repository.Path, ["log", "-1", "--format=%H"]);
        GitResult actualLink = await new GitRunner(absoluteWithLink).RunAsync(fixture.Repository.Path, ["log", "-1", "--format=%H"]);
        Check(expectedLink.Success && actualLink.Success && actualLink.StandardOutputBytes.SequenceEqual(expectedLink.StandardOutputBytes)
            && actualLink.StandardErrorBytes.SequenceEqual(expectedLink.StandardErrorBytes) && !File.Exists(canary), "Normalizing an absolute symlink/.. changed its native executable target.");
    }
    private static async Task LaunchErrorsAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This native permissions oracle requires Linux."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string executable = Executable(fixture, fixture.Root, "non-executable Git");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite); string head = await Head(fixture); int callbacks = 0;
        foreach (string path in new[] { executable, Path.Combine(fixture.Root, "missing Git") })
        {
            Win32Exception? expected = null;
            try { await DirectLaunch(path, fixture.Repository.Path, ["log", "-1"]); throw new InvalidOperationException("The direct native launch unexpectedly succeeded."); }
            catch (Win32Exception exception) { expected = exception; }
            try { await new GitRunner(path).RunAsync(fixture.Repository.Path, ["log", "-1"], outputReceived: _ => callbacks++); throw new InvalidOperationException("A launch failure was converted to a Git result."); }
            catch (Win32Exception exception) { Check(exception.NativeErrorCode == expected!.NativeErrorCode, "The original native launch error code changed."); }
        }
        Check(callbacks == 0 && await Head(fixture) == head && !(await fixture.Repository.GetBisectStateAsync()).IsActive, "Launch validation emitted output or changed Git.");
        string realExit = Path.Combine(fixture.Root, "real Git exit 127");
        File.WriteAllText(realExit, "#!/bin/sh\n/usr/bin/git \"$@\"\nexit 127\n");
        File.SetUnixFileMode(realExit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); Pipes pipes = new();
        GitResult nativeExit = await new GitRunner(realExit).RunAsync(fixture.Repository.Path, ["log", "-1", "--format=%H"], outputReceived: pipes.Receive);
        Check(nativeExit.ExitCode == 127 && nativeExit.StandardOutput.Trim() == head && pipes.Output.SequenceEqual(nativeExit.StandardOutputBytes)
            && nativeExit.StandardErrorBytes.Length == 0 && pipes.Error.Length == 0 && await Head(fixture) == head,
            "A genuine executed Git wrapper's exit127 must remain a real result, distinct from any launcher infrastructure error.");
    }
    private static Task MissingInterpreterAsync() => NativeExecErrorAsync(true);
    private static Task InvalidFormatAsync() => NativeExecErrorAsync(false);
    private static async Task NativeExecErrorAsync(bool missingInterpreter)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("This native exec oracle requires Linux."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string executable = Path.Combine(fixture.Root, "native exec error λ");
        File.WriteAllText(executable, missingInterpreter ? "#!" + Path.Combine(fixture.Root, "missing-interpreter") + "\nexit 0\n" : "This is not an executable image.\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        byte[] index = File.ReadAllBytes(fixture.File(".git/index")), seed = File.ReadAllBytes(fixture.File("seed.txt")); string head = await Head(fixture); int callbacks = 0;
        Win32Exception? expected = null;
        try { await DirectLaunch(executable, fixture.Repository.Path, ["log", "-1"]); throw new InvalidOperationException("The native exec oracle unexpectedly succeeded."); }
        catch (Win32Exception exception) { expected = exception; }
        try { await new GitRunner(executable).RunAsync(fixture.Repository.Path, ["log", "-1"], outputReceived: _ => callbacks++); throw new InvalidOperationException("An exec error became a fabricated Git result."); }
        catch (Win32Exception exception) { Check(exception.NativeErrorCode == expected!.NativeErrorCode, "The actual exec errno changed."); }
        Check(expected!.NativeErrorCode == (missingInterpreter ? 2 : 8) && callbacks == 0 && await Head(fixture) == head,
            "Native exec failure must preserve ENOENT/ENOEXEC and emit no Git output or mutation.");
        Check(File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index) && File.ReadAllBytes(fixture.File("seed.txt")).SequenceEqual(seed), "Native exec failure changed raw repository materials.");
    }
}
