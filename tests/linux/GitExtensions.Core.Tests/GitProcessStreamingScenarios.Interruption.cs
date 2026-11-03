using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;

internal static partial class GitProcessStreamingScenarios
{
    private sealed record BlockedCheckout(string Before, string Target, string Helper);
    private sealed record LockWitness(ProcessIdentity Git, ProcessIdentity Helper, int Descriptor, ulong Device, ulong Inode, byte[] Blob);

    private static async Task<BlockedCheckout> PrepareBlockedCheckout(RepositoryFixture fixture)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The private smudge fixture requires Linux procfs."); }
        fixture.Write(".gitattributes", "seed.txt filter=private-interrupt\n"); fixture.Write("seed.txt", "before checkout λ\n");
        await Git(fixture, "add", "--", ".gitattributes", "seed.txt"); await Git(fixture, "commit", "-m", "Before filtered checkout");
        string before = await Head(fixture); fixture.Write("seed.txt", "after checkout é\n");
        await Git(fixture, "add", "--", "seed.txt"); await Git(fixture, "commit", "-m", "After filtered checkout"); string target = await Head(fixture);
        await Git(fixture, "checkout", "--detach", before);
        string helper = Path.Combine(fixture.Root, "private smudge λ " + Guid.NewGuid().ToString("N"));
        string script = "#!/usr/bin/python3\n" + $$"""
            import json, os, pathlib, signal, subprocess, sys, time
            prefix = pathlib.Path({{JsonSerializer.Serialize(helper)}})
            lock = pathlib.Path({{JsonSerializer.Serialize(fixture.File(".git/index.lock"))}})
            def identity(pid):
                text = pathlib.Path('/proc/' + str(pid) + '/stat').read_text()
                fields = text[text.rfind(')') + 2:].split()
                return dict(pid=pid, parent=int(fields[1]), group=int(fields[2]), session=int(fields[3]), started=int(fields[19]))
            def publish(suffix, value):
                temp = pathlib.Path(str(prefix) + suffix + '.tmp')
                temp.write_text(json.dumps(value), encoding='utf-8')
                os.replace(temp, str(prefix) + suffix)
            def interrupted(signum, frame):
                signal.signal(signal.SIGINT, signal.SIG_IGN)
                publish('.interrupt', dict(signal=signum, monotonic_ns=time.monotonic_ns(), helper=identity(os.getpid())))
                raise SystemExit(130)
            signal.signal(signal.SIGINT, interrupted)
            blob = sys.stdin.buffer.read()
            pid = os.getppid()
            owner = None
            while pid > 1:
                ancestor = identity(pid)
                if pathlib.Path('/proc/' + str(pid) + '/exe').resolve() == pathlib.Path('/usr/bin/git').resolve():
                    for fd in pathlib.Path('/proc/' + str(pid) + '/fd').iterdir():
                        try:
                            current, expected = fd.stat(), lock.stat()
                            if (current.st_dev, current.st_ino) == (expected.st_dev, expected.st_ino):
                                owner = dict(git=ancestor, helper=identity(os.getpid()), descriptor=int(fd.name),
                                             device=current.st_dev, inode=current.st_ino, blob=blob.hex())
                                break
                        except FileNotFoundError:
                            pass
                if owner is not None:
                    break
                pid = ancestor['parent']
            if owner is None:
                raise RuntimeError('No actual ancestor Git owns the current index.lock FD')
            sys.stdout.buffer.write(blob)
            sys.stdout.buffer.flush()
            publish('.ready', owner)
            emitted = False
            while not pathlib.Path(str(prefix) + '.release').exists():
                if not emitted and pathlib.Path(str(prefix) + '.emit').exists():
                    # Actual Git diagnostic bytes reach the stream observer while checkout still owns its index lock.
                    subprocess.run(['/usr/bin/git', 'rev-parse', '--verify', 'missing-private-interrupt-ref'], check=False)
                    emitted = True
                time.sleep(0.01)
            """ + "\n";
        File.WriteAllText(helper, script); File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Git(fixture, "config", "filter.private-interrupt.clean", "cat");
        await Git(fixture, "config", "filter.private-interrupt.smudge", Quote(helper));
        await Git(fixture, "config", "filter.private-interrupt.required", "true");
        return new(before, target, helper);
    }

    private static ProcessIdentity WitnessIdentity(JsonElement value) => new(value.GetProperty("pid").GetInt32(), value.GetProperty("parent").GetInt32(),
        value.GetProperty("group").GetInt32(), value.GetProperty("session").GetInt32(), value.GetProperty("started").GetInt64());

    private static async Task<LockWitness> WitnessCheckout(RepositoryFixture fixture, BlockedCheckout checkout)
    {
        await Until(() => File.Exists(checkout.Helper + ".ready"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(checkout.Helper + ".ready")); JsonElement value = document.RootElement;
        LockWitness witness = new(WitnessIdentity(value.GetProperty("git")), WitnessIdentity(value.GetProperty("helper")), value.GetProperty("descriptor").GetInt32(),
            value.GetProperty("device").GetUInt64(), value.GetProperty("inode").GetUInt64(), Convert.FromHexString(value.GetProperty("blob").GetString()!));
        Check(witness.Git.Group == witness.Helper.Group && witness.Git.Session == witness.Helper.Session && witness.Helper.Started >= witness.Git.Started,
            "The smudge helper must actually descend from the Git holding the lock in the same process group/session.");
        await CheckHeldLock(fixture, witness);
        GitBinaryResult blob = await new GitRunner().RunBinaryAsync(fixture.Repository.Path, ["cat-file", "blob", checkout.Target + ":seed.txt"]);
        Check(blob.Success && blob.StandardOutput.SequenceEqual(witness.Blob), "The blocked filter must consume actual committed Git blob bytes.");
        return witness;
    }

    private static async Task CheckHeldLock(RepositoryFixture fixture, LockWitness witness)
    {
        ProcessIdentity git = Identity("/proc/" + witness.Git.Pid + "/stat"), helper = Identity("/proc/" + witness.Helper.Pid + "/stat");
        Check(git.Started == witness.Git.Started && git.Group == witness.Git.Group && git.Session == witness.Git.Session
            && helper.Started == witness.Helper.Started && helper.Group == witness.Helper.Group && helper.Session == witness.Helper.Session,
            "The exact witnessed Git/filter actors must still be alive when cancellation is triggered.");
        string descriptor = "/proc/" + witness.Git.Pid + "/fd/" + witness.Descriptor;
        GitResult actual = await DirectLaunch("/usr/bin/stat", fixture.Root, ["-Lc", "%d:%i", "--", descriptor, fixture.File(".git/index.lock")]);
        string expected = witness.Device.ToString(CultureInfo.InvariantCulture) + ":" + witness.Inode.ToString(CultureInfo.InvariantCulture);
        Check(actual.Success && actual.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(new[] { expected, expected }),
            "The actual Git FD and physical index.lock must still identify the same device/inode; mere lock presence is insufficient.");
    }

    private static async Task AssertGracefulCheckout(RepositoryFixture fixture, BlockedCheckout checkout, LockWitness witness, byte[] beforeIndex)
    {
        await Until(() => !LinuxProcessState.IsRunning(witness.Git.Pid) && !LinuxProcessState.IsRunning(witness.Helper.Pid));
        Check(!File.Exists(fixture.File(".git/index.lock")), "SIGINT must let the actual Git lock owner remove its own lock naturally.");
        Check(File.Exists(checkout.Helper + ".interrupt"), "The owned filter must witness the real SIGINT before it exits.");
        using JsonDocument interrupted = JsonDocument.Parse(File.ReadAllText(checkout.Helper + ".interrupt"));
        ProcessIdentity signaled = WitnessIdentity(interrupted.RootElement.GetProperty("helper"));
        Check(interrupted.RootElement.GetProperty("signal").GetInt32() == 2 && signaled.Pid == witness.Helper.Pid && signaled.Started == witness.Helper.Started
            && signaled.Group == witness.Helper.Group && signaled.Session == witness.Helper.Session,
            "The actual SIGINT must be witnessed by the same owned filter incarnation.");
        Check(await Head(fixture) == checkout.Before && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(beforeIndex),
            "Interrupted checkout must retain the real pre-checkout HEAD and committed index; the runner must not roll Git forward.");
    }

    private static async Task GracefulCheckoutWithForeignGitAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The lock ownership fixtures require Linux procfs."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(), foreignFixture = await RepositoryFixture.CreateAsync();
        BlockedCheckout checkout = await PrepareBlockedCheckout(fixture), foreignCheckout = await PrepareBlockedCheckout(foreignFixture);
        byte[] index = File.ReadAllBytes(fixture.File(".git/index"));
        ProcessStartInfo foreignInfo = new("/usr/bin/git") { WorkingDirectory = foreignFixture.Repository.Path, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "checkout", "--detach", foreignCheckout.Target }) { foreignInfo.ArgumentList.Add(argument); }
        using Process foreign = Process.Start(foreignInfo)!; foreign.StandardInput.Close();
        Task<string> foreignOutput = foreign.StandardOutput.ReadToEndAsync(), foreignError = foreign.StandardError.ReadToEndAsync();
        using CancellationTokenSource cancellation = new(); Pipes pipes = new();
        Task<GitResult> command = new GitRunner().RunAsync(fixture.Repository.Path, ["checkout", "--detach", checkout.Target], cancellation.Token, outputReceived: pipes.Receive);
        try
        {
            LockWitness own = await WitnessCheckout(fixture, checkout), other = await WitnessCheckout(foreignFixture, foreignCheckout);
            Check(own.Git.Pid == own.Git.Group && own.Git.Pid == own.Git.Session && other.Git.Pid == foreign.Id
                && other.Git.Session != own.Git.Session, "The concurrent Git must be demonstrably outside this invocation's private session.");
            await CheckHeldLock(fixture, own); await CheckHeldLock(foreignFixture, other);
            cancellation.Cancel();
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Canceled checkout returned a fabricated Git result."); }
            catch (OperationCanceledException) { }
            await AssertGracefulCheckout(fixture, checkout, own, index);
            await CheckHeldLock(foreignFixture, other);
            Check(!File.Exists(foreignCheckout.Helper + ".interrupt") && !foreign.HasExited,
                "Cancellation must preserve the foreign Git, its helper and its exact lock inode.");
            File.WriteAllText(foreignCheckout.Helper + ".release", "release\n");
            await foreign.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await Task.WhenAll(foreignOutput, foreignError);
            Check(foreign.ExitCode == 0 && !File.Exists(foreignFixture.File(".git/index.lock")) && await Head(foreignFixture) == foreignCheckout.Target,
                "The unaffected foreign Git must complete normally and clean up its own lock.");
        }
        finally
        {
            cancellation.Cancel(); File.WriteAllText(checkout.Helper + ".release", "release\n"); File.WriteAllText(foreignCheckout.Helper + ".release", "release\n");
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            await foreign.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await Task.WhenAll(foreignOutput, foreignError);
        }
    }

    private static async Task GracefulOrphanCheckoutAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The orphan lock fixture requires Linux procfs."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); BlockedCheckout checkout = await PrepareBlockedCheckout(fixture);
        byte[] index = File.ReadAllBytes(fixture.File(".git/index")); string wrapper = Path.Combine(fixture.Root, "exited checkout parent λ");
        File.WriteAllText(wrapper, "#!/usr/bin/python3\n" + $$"""
            import os, pathlib, subprocess, sys, time
            subprocess.Popen(['/usr/bin/git'] + sys.argv[1:])
            while not pathlib.Path({{JsonSerializer.Serialize(checkout.Helper + ".ready")}}).exists():
                time.sleep(0.01)
            pathlib.Path({{JsonSerializer.Serialize(wrapper + ".parent-stat")}}).write_text(pathlib.Path('/proc/' + str(os.getpid()) + '/stat').read_text())
            """ + "\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using CancellationTokenSource cancellation = new();
        Task<GitResult> command = new GitRunner(wrapper).RunAsync(fixture.Repository.Path, ["checkout", "--detach", checkout.Target], cancellation.Token);
        try
        {
            LockWitness witness = await WitnessCheckout(fixture, checkout); await Until(() => File.Exists(wrapper + ".parent-stat"));
            ProcessIdentity parent = Identity(wrapper + ".parent-stat"); await Until(() => !LinuxProcessState.IsRunning(parent.Pid));
            ProcessIdentity supervisor = WitnessSupervisor(parent);
            Check(parent.Parent == supervisor.Pid && parent.Pid == parent.Group && parent.Pid == parent.Session
                && witness.Git.Session == parent.Session && witness.Git.Group == parent.Group && witness.Git.Started >= parent.Started
                && !command.IsCompleted, "The original direct leader must have exited while its real Git descendant still owns the lock/output pipes.");
            await CheckHeldLock(fixture, witness); cancellation.Cancel();
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Orphan cancellation returned a fabricated result."); }
            catch (OperationCanceledException) { }
            await AssertGracefulCheckout(fixture, checkout, witness, index);
        }
        finally
        {
            cancellation.Cancel(); File.WriteAllText(checkout.Helper + ".release", "release\n");
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }

    private static async Task GracefulObserverCheckoutAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The observer lock fixture requires Linux procfs."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); BlockedCheckout checkout = await PrepareBlockedCheckout(fixture);
        byte[] index = File.ReadAllBytes(fixture.File(".git/index")); InvalidOperationException sentinel = new("locked checkout observer witness");
        using CancellationTokenSource cleanup = new(); Pipes pipes = new(); int armed = 0;
        Task<GitResult> command = new GitRunner().RunAsync(fixture.Repository.Path, ["checkout", "--detach", checkout.Target], cleanup.Token,
            outputReceived: chunk => { pipes.Receive(chunk); if (Volatile.Read(ref armed) == 1) { throw sentinel; } });
        try
        {
            LockWitness witness = await WitnessCheckout(fixture, checkout); await CheckHeldLock(fixture, witness);
            Volatile.Write(ref armed, 1); File.WriteAllText(checkout.Helper + ".emit", "emit\n");
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("The original observer exception was hidden."); }
            catch (InvalidOperationException exception) when (ReferenceEquals(exception, sentinel)) { }
            await AssertGracefulCheckout(fixture, checkout, witness, index);
            Check(Encoding.UTF8.GetString(pipes.Error).Contains("Needed a single revision", StringComparison.Ordinal),
                "The failed observer must retain the genuine native diagnostic bytes which caused its failure.");
        }
        finally
        {
            cleanup.Cancel(); File.WriteAllText(checkout.Helper + ".release", "release\n");
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (InvalidOperationException exception) when (ReferenceEquals(exception, sentinel)) { }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task IgnoredInterruptFallsBackAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The SIGINT deadline fixture requires Linux procfs."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync(); string wrapper = Path.Combine(fixture.Root, "ignore interrupt λ");
        File.WriteAllText(wrapper, "#!/usr/bin/python3\n" + $$"""
            import json, os, pathlib, signal, subprocess, sys, time
            prefix = pathlib.Path({{JsonSerializer.Serialize(wrapper)}})
            def interrupted(signum, frame):
                pathlib.Path(str(prefix) + '.interrupt').write_text(json.dumps(dict(signal=signum, monotonic_ns=time.monotonic_ns())))
            signal.signal(signal.SIGINT, interrupted)
            child = os.fork()
            if child == 0:
                null = os.open('/dev/null', os.O_RDWR)
                for descriptor in (0, 1, 2):
                    os.dup2(null, descriptor)
                if null > 2:
                    os.close(null)
                prefix = pathlib.Path(str(prefix) + '.child')
                pathlib.Path(str(prefix) + '.stat').write_text(pathlib.Path('/proc/' + str(os.getpid()) + '/stat').read_text())
                while not pathlib.Path(str(prefix) + '.release').exists():
                    time.sleep(0.01)
                raise SystemExit(0)
            result = subprocess.run(['/usr/bin/git'] + sys.argv[1:], check=False)
            if result.returncode != 0:
                raise SystemExit(result.returncode)
            while not pathlib.Path(str(prefix) + '.child.stat').exists():
                time.sleep(0.01)
            pathlib.Path(str(prefix) + '.stat').write_text(pathlib.Path('/proc/' + str(os.getpid()) + '/stat').read_text())
            while not pathlib.Path(str(prefix) + '.release').exists():
                time.sleep(0.01)
            """ + "\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using CancellationTokenSource cancellation = new(); Pipes pipes = new();
        ProcessIdentity? detachedPipes = null;
        Task<GitResult> command = new GitRunner(wrapper).RunAsync(fixture.Repository.Path, ["log", "-1", "--format=%H"], cancellation.Token, outputReceived: pipes.Receive);
        try
        {
            await Until(() => File.Exists(wrapper + ".stat") && pipes.Output.Length > 0); ProcessIdentity actor = Identity(wrapper + ".stat");
            ProcessIdentity child = Identity(wrapper + ".child.stat"); detachedPipes = child;
            ProcessIdentity supervisor = WitnessSupervisor(actor);
            Check(actor.Parent == supervisor.Pid && actor.Pid == actor.Group && actor.Pid == actor.Session && LinuxProcessState.IsRunning(actor.Pid),
                "The SIGINT-ignoring actor must be this invocation's actual live private leader.");
            Check(child.Parent == actor.Pid && child.Group == actor.Group && child.Session == actor.Session && child.Started >= actor.Started
                && Enumerable.Range(0, 3).All(fd => new FileInfo("/proc/" + child.Pid + "/fd/" + fd).LinkTarget == "/dev/null"),
                "The additional owned child must be alive in the private session with all three redirected pipes actually closed.");
            Stopwatch elapsed = Stopwatch.StartNew(); cancellation.Cancel(); await Until(() => File.Exists(wrapper + ".interrupt"));
            await Until(() => File.Exists(wrapper + ".child.interrupt"));
            await Task.Delay(100);
            Check(!command.IsCompleted && LinuxProcessState.IsRunning(actor.Pid) && Identity("/proc/" + actor.Pid + "/stat").Started == actor.Started,
                "An actor which really handled and ignored SIGINT must remain alive during the grace period.");
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Forced fallback returned a fabricated result."); }
            catch (OperationCanceledException) { }
            Check(!LinuxProcessState.IsRunning(child.Pid), "Cancellation must await the known child without pipes, not merely leader exit and EOF.");
            Check(elapsed.Elapsed >= TimeSpan.FromMilliseconds(450) && elapsed.Elapsed < TimeSpan.FromSeconds(3) && !LinuxProcessState.IsRunning(actor.Pid),
                "The fallback must actually wait for the 500ms grace interval and then stop the remaining owned actor.");
            Check(Encoding.UTF8.GetString(pipes.Output).Trim() == await Head(fixture), "Forced cancellation lost or fabricated the earlier real Git output.");
        }
        finally
        {
            cancellation.Cancel(); File.WriteAllText(wrapper + ".release", "release\n"); File.WriteAllText(wrapper + ".child.release", "release\n");
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            CleanupOwnedOrphan(detachedPipes);
        }
    }

    private static async Task BlockedNativeLaunchCancellationAsync()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The native launch boundary fixture requires Linux."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string source = Path.Combine(fixture.Root, "private launch blocker.c"), library = Path.Combine(fixture.Root, "private-launch-blocker.so");
        // Instrument the actual native image, without changing its handshake, PID, exec result or Git output.
        File.WriteAllText(source, """
            #define _GNU_SOURCE
            #include <dlfcn.h>
            #include <errno.h>
            #include <fcntl.h>
            #include <signal.h>
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            #include <unistd.h>
            static void stop_at(const char *phase)
            {
                const char *selected = getenv("GITEXTENSIONS_PRIVATE_LAUNCH_PHASE");
                const char *marker = getenv("GITEXTENSIONS_PRIVATE_LAUNCH_MARKER");
                if (selected == NULL || marker == NULL || strcmp(selected, phase) != 0) { return; }
                int fd = open(marker, O_CREAT | O_EXCL | O_WRONLY | O_CLOEXEC, 0600);
                if (fd < 0) { return; }
                (void)dprintf(fd, "%ld\n", (long)getpid());
                (void)close(fd);
                (void)raise(SIGSTOP);
            }
            __attribute__((constructor)) static void before_main(void) { stop_at("before-hello"); }
            int execv(const char *path, char *const arguments[])
            {
                const char *target = getenv("GITEXTENSIONS_PRIVATE_LAUNCH_TARGET");
                if (target != NULL && strcmp(path, target) == 0) stop_at("before-exec");
                int (*next)(const char *, char *const []) = (int (*)(const char *, char *const []))dlsym(RTLD_NEXT, "execv");
                if (next == NULL) { errno = ENOSYS; return -1; }
                return next(path, arguments);
            }
            """ + "\n");
        GitResult compiled = await DirectLaunch("/usr/bin/cc", fixture.Root, ["-std=c11", "-shared", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror", source, "-ldl", "-o", library]);
        Check(compiled.Success && File.Exists(library), "The private native launch-boundary fixture did not compile: " + compiled.StandardError);
        string head = await Head(fixture); byte[] index = File.ReadAllBytes(fixture.File(".git/index"));
        foreach (string phase in new[] { "before-hello", "before-exec" })
        {
            string marker = Path.Combine(fixture.Root, phase + ".pid"), dispatched = Path.Combine(fixture.Root, phase + ".git-dispatched"), target = Path.Combine(fixture.Root, phase + " target Git λ");
            File.WriteAllText(target, "#!/bin/sh\nprintf 'dispatched\\n' > " + Quote(dispatched) + "\nexec /usr/bin/git \"$@\"\n");
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using CancellationTokenSource cancellation = new(); int callbacks = 0;
            Task<GitResult> command = new GitRunner(target).RunAsync(fixture.Repository.Path, ["log", "-1", "--format=%H"], cancellation.Token,
                environment: new Dictionary<string, string?> { ["LD_PRELOAD"] = library, ["GITEXTENSIONS_PRIVATE_LAUNCH_PHASE"] = phase,
                    ["GITEXTENSIONS_PRIVATE_LAUNCH_MARKER"] = marker, ["GITEXTENSIONS_PRIVATE_LAUNCH_TARGET"] = target }, outputReceived: _ => callbacks++);
            ProcessIdentity? witness = null, supervisorWitness = null;
            try
            {
                await Until(() => File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), out _));
                int pid = int.Parse(File.ReadAllText(marker).Trim(), CultureInfo.InvariantCulture);
                await Until(() => File.ReadAllText("/proc/" + pid + "/stat").Split(") ", StringSplitOptions.None)[^1][0] is 'T' or 't');
                ProcessIdentity actor = Identity("/proc/" + pid + "/stat"); witness = actor;
                string expectedImage = phase == "before-hello" ? "gitextensions-posix" : "gitextensions-exec";
                if (phase == "before-exec") { supervisorWitness = WitnessSupervisor(actor); }
                Check(actor.Parent == (supervisorWitness?.Pid ?? Environment.ProcessId)
                    && new FileInfo("/proc/" + pid + "/exe").LinkTarget == Path.Combine(AppContext.BaseDirectory, expectedImage)
                    && !command.IsCompleted && callbacks == 0 && !File.Exists(dispatched),
                    "The actual native image must be stopped before Git dispatch, beneath its witnessed owner.");
                Check(actor.Pid == actor.Session && actor.Pid == actor.Group,
                    "Native spawn isolates the supervisor before its constructor; the exec child establishes its own session before Git execv.");
                cancellation.Cancel();
                try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Native pre-exec cancellation fabricated a Git result."); }
                catch (OperationCanceledException) { }
                Check(!LinuxProcessState.IsRunning(pid) && (supervisorWitness is null || !LinuxProcessState.IsRunning(supervisorWitness.Value.Pid))
                    && callbacks == 0 && !File.Exists(dispatched) && await Head(fixture) == head
                    && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index),
                    "Cancellation must terminate the actual stopped launcher before returning, with no exec, output or Git effects.");
            }
            finally
            {
                cancellation.Cancel(); CleanupOwnedOrphan(witness);
                try { await command.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            }
        }
    }
}
