using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using GitCommands.Git;
using GitExtensions.Linux;

internal static partial class GitProcessStreamingScenarios
{
    private sealed record PipeHolder(ProcessIdentity Identity, int Descriptor, ulong Device, ulong Inode, string Marker);

    private static async Task IndependentOrphanPipesAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The independent pipe-holder fixture requires Linux."); }
        // Adopt just this fixture's orphaned children so its real wait statuses remain observable; never assume host init is PID1.
        if (GetSubreaper(37, out int previous, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
        { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the test's child-subreaper setting."); }
        if (SetSubreaper(36, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
        { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot adopt this fixture's orphaned pipe holders."); }
        try { await RunIndependentOrphanPipesAsync(); }
        finally
        {
            if (SetSubreaper(36, (nuint)previous, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
            { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot restore the test's child-subreaper setting."); }
        }
    }

    private static async Task RunIndependentOrphanPipesAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The independent pipe-holder fixture requires Linux."); }
        List<PipeHolder> holders = []; HashSet<int> reaped = [];
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); await fixture.Repository.StartBisectAsync();
        string head = await Head(fixture), wrapper = Path.Combine(fixture.Root, "independent orphan Git λ");
        byte[] index = File.ReadAllBytes(fixture.File(".git/index")), seed = File.ReadAllBytes(fixture.File("seed.txt")), config = File.ReadAllBytes(fixture.File(".git/config"));
        File.WriteAllText(wrapper, "#!/usr/bin/python3\n" + $$"""
            import json, os, pathlib, signal, subprocess, sys, time
            if not any(a == 'bisect' and b == 'good' for a, b in zip(sys.argv[1:], sys.argv[2:])):
                os.execv('/usr/bin/git', ['git'] + sys.argv[1:])
            prefix = pathlib.Path({{JsonSerializer.Serialize(wrapper)}})
            def publish(name, value):
                temp = pathlib.Path(str(prefix) + name + '.tmp')
                temp.write_text(json.dumps(value), encoding='utf-8')
                os.replace(temp, str(prefix) + name)
            def stat():
                return pathlib.Path('/proc/' + str(os.getpid()) + '/stat').read_text()
            with subprocess.Popen(['/usr/bin/git'] + sys.argv[1:], stdout=subprocess.PIPE, stderr=subprocess.PIPE) as git:
                output, error = git.communicate()
                publish('.git-result', dict(pid=git.pid, exit=git.returncode, stdout=output.hex(), stderr=error.hex()))
                os.write(1, output)
                os.write(2, error)
                status = git.returncode
            pathlib.Path(str(prefix) + '.parent-stat').write_text(stat())
            children = []
            for descriptor, role in ((1, 'stdout'), (2, 'stderr')):
                child = os.fork()
                if child != 0:
                    children.append(child)
                    continue
                null = os.open('/dev/null', os.O_RDWR)
                os.dup2(null, 0)
                os.dup2(null, 3 - descriptor)
                if null > 2:
                    os.close(null)
                marker = '.' + role
                def interrupted(signum, frame):
                    signal.signal(signal.SIGINT, signal.SIG_IGN)
                    publish(marker + '.int', dict(signal=signum, stat=stat(), monotonic_ns=time.monotonic_ns()))
                    raise SystemExit(130)
                signal.signal(signal.SIGINT, interrupted)
                pipe = os.fstat(descriptor)
                publish(marker + '.ready', dict(stat=stat(), descriptor=descriptor, device=pipe.st_dev, inode=pipe.st_ino))
                while not pathlib.Path(str(prefix) + marker + '.release').exists():
                    time.sleep(0.01)
                raise SystemExit(0)
            while not all(pathlib.Path(str(prefix) + '.' + role + '.ready').exists() for role in ('stdout', 'stderr')):
                time.sleep(0.01)
            publish('.children', children)
            raise SystemExit(status)
            """ + "\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Pipes pipes = new(); using CancellationTokenSource cancellation = new();
        Task<GitResult> command = new GitRepository(fixture.Repository.Path, new GitRunner(wrapper)).MarkBisectAsync(GitBisectOption.Good, head, cancellation.Token, pipes.Receive);
        try
        {
            await Until(() => File.Exists(wrapper + ".children")); ProcessIdentity parent = Identity(wrapper + ".parent-stat");
            Check(parent.Parent == Environment.ProcessId && parent.Pid == parent.Group && parent.Pid == parent.Session,
                "The actual directly started parent must execute in its private group and session.");
            foreach (string role in new[] { "stdout", "stderr" })
            {
                string marker = wrapper + "." + role;
                using JsonDocument ready = JsonDocument.Parse(File.ReadAllText(marker + ".ready")); JsonElement value = ready.RootElement;
                // Preserve the actual pre-orphan stat separately; the current parent will be the subreaper, not the exited leader.
                string statPath = marker + ".witness-stat"; File.WriteAllText(statPath, value.GetProperty("stat").GetString());
                PipeHolder holder = new(Identity(statPath), value.GetProperty("descriptor").GetInt32(), value.GetProperty("device").GetUInt64(), value.GetProperty("inode").GetUInt64(), marker);
                holders.Add(holder);
                Check(holder.Identity.Parent == parent.Pid && holder.Identity.Group == parent.Group && holder.Identity.Session == parent.Session
                    && holder.Identity.Started >= parent.Started, "Both independently held pipes must originate from the actual private parent.");
            }
            await Until(() => !LinuxProcessState.IsRunning(parent.Pid));
            Check(!command.IsCompleted, "The completed native Good and exited parent must still leave both genuine Git output pipes pending.");
            foreach (PipeHolder holder in holders)
            {
                ProcessIdentity current = Identity("/proc/" + holder.Identity.Pid + "/stat");
                Check(current.Parent == Environment.ProcessId && current.Started == holder.Identity.Started
                    && current.Group == parent.Group && current.Session == parent.Session,
                    "The exact live holders must be adopted orphans without changing their private group/session.");
                GitResult metadata = await DirectLaunch("/usr/bin/stat", fixture.Root,
                    ["-Lc", "%d:%i", "--", "/proc/" + current.Pid + "/fd/" + holder.Descriptor]);
                Check(metadata.Success && metadata.StandardOutput.Trim() == holder.Device.ToString(CultureInfo.InvariantCulture) + ":" + holder.Inode.ToString(CultureInfo.InvariantCulture),
                    "Cancellation requires a live witness of each exact stdout/stderr pipe device/inode.");
            }
            using JsonDocument native = JsonDocument.Parse(File.ReadAllText(wrapper + ".git-result"));
            Check(native.RootElement.GetProperty("exit").GetInt32() == 0 && (await fixture.Repository.GetBisectStateAsync()).GoodRevisions.Contains(head),
                "The fixture must complete a real successful Good before the independently inherited pipe waits.");
            cancellation.Cancel();
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Orphan cancellation fabricated a Git result."); }
            catch (OperationCanceledException exception) { Check(exception.CancellationToken == cancellation.Token, "The caller's original cancellation token was substituted."); }
            foreach (PipeHolder holder in holders)
            {
                Check(!LinuxProcessState.IsRunning(holder.Identity.Pid), "Cancellation must return only after both witnessed independent pipe holders stop.");
                using JsonDocument interrupted = JsonDocument.Parse(File.ReadAllText(holder.Marker + ".int"));
                Check(interrupted.RootElement.GetProperty("signal").GetInt32() == 2, "Each late discovered live holder needs a genuine SIGINT.");
                string signaledPath = holder.Marker + ".signaled-stat"; File.WriteAllText(signaledPath, interrupted.RootElement.GetProperty("stat").GetString());
                ProcessIdentity signaled = Identity(signaledPath);
                Check(signaled.Pid == holder.Identity.Pid && signaled.Started == holder.Identity.Started
                    && signaled.Group == holder.Identity.Group && signaled.Session == holder.Identity.Session,
                    "The real SIGINT witness must belong to the same independently held pipe actor incarnation.");
                int waited = WaitFixtureChild(holder.Identity.Pid, out int status, 1);
                Check(waited == holder.Identity.Pid, "The exact orphan's actual termination status must be waitable by its fixture subreaper.");
                reaped.Add(waited);
                Check((status & 0x7F) == 0 && ((status >> 8) & 0xFF) == 130,
                    "An exit-on-INT holder must actually exit130 naturally; a forced SIGKILL or a self-reported status is insufficient.");
            }
            Check(pipes.Output.SequenceEqual(Convert.FromHexString(native.RootElement.GetProperty("stdout").GetString()!))
                && pipes.Error.SequenceEqual(Convert.FromHexString(native.RootElement.GetProperty("stderr").GetString()!)),
                "Cancellation must preserve every actual native Git stdout/stderr byte without synthetic progress.");
            GitBisectState state = await fixture.Repository.GetBisectStateAsync();
            Check(state.GoodRevisions.SequenceEqual(new[] { head }) && state.BadRevision is null && await Head(fixture) == head
                && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index) && File.ReadAllBytes(fixture.File("seed.txt")).SequenceEqual(seed)
                && File.ReadAllBytes(fixture.File(".git/config")).SequenceEqual(config),
                "Stopping independent orphan pipes must retain native Good without another mark or changes to raw repository materials.");
        }
        finally
        {
            cancellation.Cancel();
            foreach (PipeHolder holder in holders)
            {
                File.WriteAllText(holder.Marker + ".release", "release\n");
                if (!reaped.Contains(holder.Identity.Pid)) { CleanupOwnedOrphan(holder.Identity); }
            }
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            foreach (PipeHolder holder in holders)
            {
                if (reaped.Contains(holder.Identity.Pid)) { continue; }
                await Until(() => !LinuxProcessState.IsRunning(holder.Identity.Pid));
                _ = WaitFixtureChild(holder.Identity.Pid, out _, 1);
            }
        }
    }

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int GetSubreaper(int option, out int value, IntPtr third, IntPtr fourth, IntPtr fifth);
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int SetSubreaper(int option, nuint value, IntPtr third, IntPtr fourth, IntPtr fifth);
    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitFixtureChild(int process, out int status, int options);
}
