using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GitExtensions.Linux;

internal static partial class GitProcessStreamingScenarios
{
    // Root connects this separate array in Program.cs; existing registrations remain untouched here.
    internal static readonly (string Name, Func<Task> Run)[] RelayCancellationCases =
    [
        ("cancellation captures relayed native Git descendants before their leader exits on its first SIGINT", Guard(() => RelayCancellationAsync(false, false))),
        ("cancellation retains a witnessed descendant which closes every inherited pipe before SIGINT", Guard(() => RelayCancellationAsync(true, false))),
        ("relay cancellation leaves a separately witnessed foreign native actor and its pipes untouched", Guard(() => RelayCancellationAsync(true, true)))
    ];

    private static string RelayScript(RepositoryFixture fixture, string prefix, bool closedActor)
        => "#!/usr/bin/python3\n" + $$"""
        import json, os, pathlib, signal, subprocess, sys, threading, time
        prefix = pathlib.Path({{JsonSerializer.Serialize(prefix)}})
        repository = {{JsonSerializer.Serialize(fixture.Repository.Path)}}
        include_closed = {{(closedActor ? "True" : "False")}}
        role = pathlib.Path(sys.argv[0]).name
        def identity(pid=None):
            pid = os.getpid() if pid is None else pid
            raw = pathlib.Path('/proc/' + str(pid) + '/stat').read_text()
            fields = raw[raw.rfind(')') + 2:].split()
            return dict(pid=pid, parent=int(fields[1]), group=int(fields[2]), session=int(fields[3]), started=int(fields[19]))
        def publish(suffix, value):
            target = pathlib.Path(str(prefix) + suffix)
            temporary = pathlib.Path(str(target) + '.tmp')
            temporary.write_text(json.dumps(value), encoding='utf-8')
            os.replace(temporary, target)
        def released():
            return pathlib.Path(str(prefix) + '.release').exists()
        if role == 'relay-git':
            command = ['ls-remote', '--heads', '--', 'ssh://private-relay.invalid/repository']
            if sys.argv[1:] != command:
                raise RuntimeError('Unexpected relay Git argv')
            probe = subprocess.run(['/usr/bin/git', 'rev-parse', 'HEAD'], capture_output=True, check=True)
            publish('.probe', dict(exit=probe.returncode, stdout=probe.stdout.hex(), stderr=probe.stderr.hex()))
            os.write(1, probe.stdout)
            def interrupted(number, frame):
                publish('.relay-int', dict(signal=number, actor=identity()))
                raise SystemExit(130)  # Deliberately no child wait: the fixture subreaper observes the orphan's real exit.
            signal.signal(signal.SIGINT, interrupted)
            entered = threading.Event()
            def start_git():
                child = subprocess.Popen(['/usr/bin/git', *command], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                publish('.git-born', identity(child.pid))
                entered.set()
                while not released():
                    time.sleep(.01)
                output, error = child.communicate()
                publish('.relay-child-wait', dict(pid=child.pid, exit=child.returncode, stdout=output.hex(), stderr=error.hex()))
            # The native child belongs to this worker's task/children file, not the main thread's.
            threading.Thread(target=start_git, daemon=True).start()
            entered.wait()
            publish('.relay-ready', dict(actor=identity(), pipes=[dict(device=os.fstat(fd).st_dev, inode=os.fstat(fd).st_ino) for fd in (1, 2)]))
            while not released():
                time.sleep(.01)
            raise SystemExit(0)
        elif role == 'relay-ssh':
            request = ['private-relay.invalid', "git-upload-pack '/repository'"]
            if sys.argv[1:] not in (request, ['-o', 'SendEnv=GIT_PROTOCOL', *request]):
                raise RuntimeError('Only the private SSH fixture host/path is supported; no network')
            stopped = threading.Event()
            def interrupted(number, frame):
                publish('.ssh-int', dict(signal=number, actor=identity()))
                stopped.set()
            signal.signal(signal.SIGINT, interrupted)
            upload = subprocess.Popen(['/usr/bin/git', 'upload-pack', repository], stdin=sys.stdin.buffer,
                                      stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            packet = os.read(upload.stdout.fileno(), 65536)
            if not packet or upload.poll() is not None:
                raise RuntimeError('The real upload-pack must advertise bytes while still alive')
            pathlib.Path(str(prefix) + '.packet.raw').write_bytes(packet)
            closed = None
            if include_closed:
                closed = subprocess.Popen([str(prefix.parent / 'closed-actor')], stdin=subprocess.DEVNULL,
                                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                while not pathlib.Path(str(prefix) + '.closed-ready').exists():
                    time.sleep(.01)
            publish('.ssh-ready', dict(actor=identity(), git=identity(os.getppid()), upload=identity(upload.pid),
                                      closed=None if closed is None else identity(closed.pid),
                                      pipes=[dict(device=os.fstat(fd).st_dev, inode=os.fstat(fd).st_ino) for fd in (1, 2)]))
            while not stopped.is_set() and not released():
                time.sleep(.01)
            if released() and not stopped.is_set():
                # Failure cleanup is a release of the real protocol, never a fabricated cancellation receipt.
                os.write(1, packet)
                while data := os.read(upload.stdout.fileno(), 65536):
                    os.write(1, data)
            status = upload.wait()
            error = upload.stderr.read()
            pathlib.Path(str(prefix) + '.upload-stderr.raw').write_bytes(error)
            closed_status = None if closed is None else closed.wait()
            publish('.ssh-child-waits', dict(upload_pid=upload.pid, upload_exit=status,
                                            closed_pid=None if closed is None else closed.pid, closed_exit=closed_status))
            raise SystemExit(130 if stopped.is_set() else 0)
        elif role == 'closed-actor':
            # These are genuinely different, non-pipe descriptors before the ready witness.
            null = os.open('/dev/null', os.O_RDWR)
            for fd in (0, 1, 2):
                os.dup2(null, fd)
            if null > 2:
                os.close(null)
            def interrupted(number, frame):
                publish('.closed-int', dict(signal=number, actor=identity()))
                raise SystemExit(130)
            signal.signal(signal.SIGINT, interrupted)
            publish('.closed-ready', dict(actor=identity(), descriptors=[os.readlink('/proc/self/fd/' + str(fd)) for fd in (0, 1, 2)]))
            while not released():
                time.sleep(.01)
            raise SystemExit(0)
        elif role == 'foreign-actor':
            result = subprocess.run(['/usr/bin/git', 'rev-parse', 'HEAD'], capture_output=True, check=True)
            def interrupted(number, frame):
                publish('.foreign-int', dict(signal=number, actor=identity()))
                raise SystemExit(130)
            signal.signal(signal.SIGINT, interrupted)
            publish('.foreign-ready', dict(actor=identity(), git_exit=result.returncode, stdout=result.stdout.hex(), stderr=result.stderr.hex()))
            while not released():
                time.sleep(.01)
            raise SystemExit(0)
        else:
            raise RuntimeError('Unknown private relay role')
        """ + "\n";

    private static JsonElement ReadRelay(string prefix, string suffix)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(prefix + suffix));
        return document.RootElement.Clone();
    }

    private static void CheckRelayInterrupt(string prefix, string suffix, ProcessIdentity expected)
    {
        JsonElement value = ReadRelay(prefix, suffix);
        ProcessIdentity actual = WitnessIdentity(value.GetProperty("actor"));
        Check(value.GetProperty("signal").GetInt32() == 2 && actual.Pid == expected.Pid && actual.Started == expected.Started
            && actual.Group == expected.Group && actual.Session == expected.Session,
            "The real SIGINT receipt must name the same witnessed relay actor incarnation.");
    }

    private static void CollectRelayActors(string prefix, List<ProcessIdentity> actors)
    {
        void Add(JsonElement value)
        {
            ProcessIdentity actor = WitnessIdentity(value);
            if (!actors.Any(known => known.Pid == actor.Pid && known.Started == actor.Started)) { actors.Add(actor); }
        }
        if (File.Exists(prefix + ".git-born")) { Add(ReadRelay(prefix, ".git-born")); }
        if (File.Exists(prefix + ".ssh-ready"))
        {
            JsonElement value = ReadRelay(prefix, ".ssh-ready"); Add(value.GetProperty("actor")); Add(value.GetProperty("upload"));
            if (value.GetProperty("closed").ValueKind != JsonValueKind.Null) { Add(value.GetProperty("closed")); }
        }
    }

    private static async Task RelayCancellationAsync(bool closedActor, bool foreignActor)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The relay fixture requires Linux procfs and actual child waits."); }
        if (GetSubreaper(37, out int previous, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0
            || SetSubreaper(36, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
        { throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "Cannot establish the fixture's child subreaper."); }
        try { await RunRelayCancellationAsync(closedActor, foreignActor); }
        finally
        {
            if (SetSubreaper(36, (nuint)previous, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
            { throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "Cannot restore the fixture's subreaper setting."); }
        }
    }

    private static async Task RunRelayCancellationAsync(bool closedActor, bool foreignActor)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("The relay fixture requires Linux native helpers."); }
        using RepositoryFixture fixture = await RepositoryFixture.CreateAsync();
        string head = await Head(fixture), prefix = Path.Combine(fixture.Root, "relay evidence λ");
        byte[] index = File.ReadAllBytes(fixture.File(".git/index")), seed = File.ReadAllBytes(fixture.File("seed.txt")), config = File.ReadAllBytes(fixture.File(".git/config"));
        string directory = Path.Combine(fixture.Root, "relay tools"); Directory.CreateDirectory(directory);
        prefix = Path.Combine(directory, "witness");
        foreach (string name in new[] { "relay-git", "relay-ssh", "closed-actor", "foreign-actor" })
        {
            string path = Path.Combine(directory, name); File.WriteAllText(path, RelayScript(fixture, prefix, closedActor));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Process? foreign = null;
        Task<string>? foreignOutput = null, foreignError = null;
        List<ProcessIdentity> actors = [];
        HashSet<int> reaped = [];
        using CancellationTokenSource cancellation = new();
        Pipes pipes = new();
        Task<GitResult>? command = null;
        try
        {
            if (foreignActor)
            {
                ProcessStartInfo start = new(Path.Combine(directory, "foreign-actor")) { WorkingDirectory = fixture.Repository.Path,
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreign = Process.Start(start)!; foreign.StandardInput.Close();
                foreignOutput = foreign.StandardOutput.ReadToEndAsync(); foreignError = foreign.StandardError.ReadToEndAsync();
                await Until(() => File.Exists(prefix + ".foreign-ready"));
                Check(ReadRelay(prefix, ".foreign-ready").GetProperty("git_exit").GetInt32() == 0, "The foreign control must run real native Git first.");
            }
            NativeWaitWitness waitWitness = await CreateNativeWaitWitnessAsync(fixture);
            Dictionary<string, string?> environment = new()
            {
                ["GIT_SSH"] = Path.Combine(directory, "relay-ssh"), ["GIT_SSH_VARIANT"] = "ssh", ["GIT_SSH_COMMAND"] = null,
                ["GIT_ASKPASS"] = "/bin/false", ["SSH_ASKPASS"] = "/bin/false"
            };
            waitWitness.Apply(environment);
            command = new GitRunner(Path.Combine(directory, "relay-git")).RunAsync(fixture.Repository.Path,
                ["ls-remote", "--heads", "--", "ssh://private-relay.invalid/repository"], cancellation.Token,
                environment: environment, outputReceived: pipes.Receive);
            await Until(() => File.Exists(prefix + ".relay-ready") && File.Exists(prefix + ".ssh-ready") && pipes.Output.Length > 0);
            JsonElement relay = ReadRelay(prefix, ".relay-ready"), ready = ReadRelay(prefix, ".ssh-ready");
            ProcessIdentity leader = WitnessIdentity(relay.GetProperty("actor")), git = WitnessIdentity(ready.GetProperty("git")),
                ssh = WitnessIdentity(ready.GetProperty("actor")), upload = WitnessIdentity(ready.GetProperty("upload"));
            actors.AddRange([git, ssh, upload]);
            if (closedActor) { actors.Add(WitnessIdentity(ready.GetProperty("closed"))); }
            ProcessIdentity supervisor = WitnessSupervisor(leader);
            Check(leader.Parent == supervisor.Pid && leader.Pid == leader.Group && leader.Pid == leader.Session,
                "The real Git launch must own its direct relay and private session.");
            Check(git.Parent == leader.Pid && ssh.Parent == git.Pid && upload.Parent == ssh.Pid
                && actors.All(actor => actor.Group == leader.Group && actor.Session == leader.Session && LinuxProcessState.IsRunning(actor.Pid)),
                "The pending native Git, SSH and upload-pack must be actual live descendants in this private invocation.");
            JsonElement originalPipes = relay.GetProperty("pipes"), relayPipes = ready.GetProperty("pipes");
            Check(Enumerable.Range(0, 2).All(number => originalPipes[number].GetProperty("device").GetUInt64() != relayPipes[number].GetProperty("device").GetUInt64()
                || originalPipes[number].GetProperty("inode").GetUInt64() != relayPipes[number].GetProperty("inode").GetUInt64()),
                "The SSH fixture must replace the outer stdout/stderr pipes instead of exercising the inherited-pipe fallback.");
            Check(File.ReadAllBytes(prefix + ".packet.raw").Length > 0 && !command.IsCompleted,
                "A real pending upload-pack packet must precede the cancellation, without a fabricated query result.");
            if (closedActor)
            {
                Check(ReadRelay(prefix, ".closed-ready").GetProperty("descriptors").EnumerateArray().All(value => value.GetString() == "/dev/null"),
                    "The additional owned actor must close every inherited pipe before cancellation.");
            }
            cancellation.Cancel();
            try { await command.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Relay cancellation returned a fabricated Git result."); }
            catch (OperationCanceledException error) { Check(error.CancellationToken == cancellation.Token, "Relay cancellation replaced the caller's original token."); }
            Check(actors.All(actor => !LinuxProcessState.IsRunning(actor.Pid)), "Cancellation returned while a witnessed relayed descendant could still run.");
            CheckRelayInterrupt(prefix, ".relay-int", leader); CheckRelayInterrupt(prefix, ".ssh-int", ssh);
            if (closedActor) { CheckRelayInterrupt(prefix, ".closed-int", actors[^1]); }
            JsonElement waited = ReadRelay(prefix, ".ssh-child-waits");
            Check(waited.GetProperty("upload_pid").GetInt32() == upload.Pid && waited.GetProperty("upload_exit").GetInt32() != -9,
                "The SSH helper must actually wait for its original native upload-pack child without forced SIGKILL.");
            if (closedActor) { Check(waited.GetProperty("closed_pid").GetInt32() == actors[^1].Pid && waited.GetProperty("closed_exit").GetInt32() == 130, "The closed-pipe actor needs a real natural child wait130, not forced SIGKILL."); }
            foreach (ProcessIdentity orphan in new[] { git, ssh })
            {
                int status = waitWitness.StatusFor(supervisor.Pid, orphan.Pid);
                reaped.Add(orphan.Pid);
                Check(orphan == ssh ? (status & 0x7F) == 0 && ((status >> 8) & 0xFF) == 130 : (status & 0x7F) != 9,
                    "The actual orphan wait must retain graceful interruption instead of a forced SIGKILL.");
            }
            JsonElement probe = ReadRelay(prefix, ".probe");
            Check(probe.GetProperty("exit").GetInt32() == 0 && pipes.Output.SequenceEqual(Convert.FromHexString(probe.GetProperty("stdout").GetString()!))
                && pipes.Error.Length == 0 && Encoding.UTF8.GetString(pipes.Output).Trim() == head,
                "Cancellation lost or invented already-observed bytes from the real native Git probe.");
            if (foreign is not null)
            {
                Check(!foreign.HasExited && !File.Exists(prefix + ".foreign-int"), "Cancellation signaled a separately witnessed foreign actor.");
                ProcessIdentity expected = WitnessIdentity(ReadRelay(prefix, ".foreign-ready").GetProperty("actor"));
                Check(Identity("/proc/" + expected.Pid + "/stat") == expected, "The untouched foreign actor changed incarnation during owned cancellation.");
            }
            Check(await Head(fixture) == head && File.ReadAllBytes(fixture.File(".git/index")).SequenceEqual(index)
                && File.ReadAllBytes(fixture.File("seed.txt")).SequenceEqual(seed) && File.ReadAllBytes(fixture.File(".git/config")).SequenceEqual(config),
                "Read-only relay cancellation changed actual HEAD/index/worktree/config materials.");
        }
        finally
        {
            CollectRelayActors(prefix, actors);
            cancellation.Cancel(); File.WriteAllText(prefix + ".release", "release\n");
            foreach (ProcessIdentity actor in actors.AsEnumerable().Reverse()) { CleanupOwnedOrphan(actor); }
            if (command is not null) { try { await command.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
            foreach (ProcessIdentity actor in actors)
            {
                await Until(() => !LinuxProcessState.IsRunning(actor.Pid));
                if (!reaped.Contains(actor.Pid)) { _ = WaitFixtureChild(actor.Pid, out _, 1); }
            }
            if (foreign is not null)
            {
                await foreign.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(foreign.ExitCode == 0, "The foreign fixture's actual normal release did not exit0.");
                await Task.WhenAll(foreignOutput!, foreignError!); foreign.Dispose();
            }
        }
    }
}

internal static class GitProcessRelayScenarios
{
    internal static readonly (string Name, Func<Task> Run)[] All = GitProcessStreamingScenarios.RelayCancellationCases;
}
