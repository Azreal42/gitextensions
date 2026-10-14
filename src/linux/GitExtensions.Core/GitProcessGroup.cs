using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux;

/// <summary>Owns stable process references, including writers which outlive the directly started Git process.</summary>
[SupportedOSPlatform("linux")]
internal sealed class GitProcessGroup : IDisposable
{
    private const int SigInt = 2, SigKill = 9, SigStop = 19, NoSuchProcess = 3, Interrupted = 4;
    private const int CloseOnExec = 0x80000, DirectoryOnly = 0x10000, PathOnly = 0x200000, DuplicateCloseOnExec = 1030;
    // Both supported Linux ABIs use this syscall number. Using syscall also supports Ubuntu 22.04's glibc.
    private const long SendSignalSyscall = 424;
    private readonly List<OwnedProcess> _members = [];
    private readonly List<(SafeFileHandle Handle, FileIdentity Identity)> _pipes = [];
    private int _leader;
    private OwnedProcess? _directChild;

    internal void PinOutputPipes(Stream output, Stream error)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        { throw new PlatformNotSupportedException("The private Git process references require linux-x64 or linux-arm64."); }
        foreach (Stream stream in new[] { output, error })
        {
            if (stream is not PipeStream pipe) { throw new InvalidDataException("Git output must use owned redirected pipes."); }
            int descriptor = Duplicate(pipe.SafePipeHandle, DuplicateCloseOnExec, 3);
            if (descriptor < 0) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot pin a Git output pipe."); }
            SafeFileHandle handle = new((IntPtr)descriptor, ownsHandle: true);
            try { _pipes.Add((handle, FileIdentity.Read(handle))); }
            catch { handle.Dispose(); throw; }
        }
    }

    // Capture even a loader blocked before HELLO. .NET's original child wait state serializes reaping and HasExited.
    // Bracketing open with that original instance's state rejects a numeric PID recycled for another child.
    internal void CaptureStartedChild(Process process)
    {
        if (process.HasExited) { return; }
        int processId = process.Id;
        OwnedProcess? leader = OwnedProcess.Open(processId);
        try
        {
            if (process.HasExited) { return; }
            if (leader is null || leader.State() is not ProcessState state || state.Pid != processId || state.Parent != Environment.ProcessId
                || processId <= 1 || processId == Environment.ProcessId)
            { throw new InvalidDataException("The started Git launcher has no owned direct-child reference."); }
            // The state read above must also be bracketed: the runtime may reap the original child during that read.
            if (process.HasExited) { return; }
            _leader = processId; _directChild = leader; _members.Add(leader); leader = null;
        }
        finally { leader?.Dispose(); }
    }

    internal void AuthenticateLeader(int processId)
    {
        if (_leader != processId || _directChild?.State() is not ProcessState state || state.Pid != processId || state.Parent != Environment.ProcessId)
        { throw new InvalidDataException("The authenticated Git launcher differs from the owned direct child."); }
    }

    internal async Task<Exception?> StopAsync()
    {
        Exception? failure = null;
        bool Signal(OwnedProcess member, int signal)
        {
            int result;
            do { result = (int)SendSignal(SendSignalSyscall, member.Handle, signal, IntPtr.Zero, 0); }
            while (result != 0 && Marshal.GetLastPInvokeError() == Interrupted);
            if (result != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != NoSuchProcess) { failure ??= new Win32Exception(error, "Cannot signal an owned Git process."); }
            }
            return result == 0;
        }
        Dictionary<OwnedProcess, long> interrupted = [];
        HashSet<OwnedProcess> terminated = [];
        HashSet<OwnedProcess> forced = [];
        bool AdvanceStops()
        {
            bool live = false;
            foreach (OwnedProcess member in _members)
            {
                if (terminated.Contains(member)) { continue; }
                if (!member.IsAlive()) { terminated.Add(member); continue; }
                live = true;
                if (!interrupted.TryGetValue(member, out long started))
                {
                    if (!Signal(member, SigInt)) { continue; }
                    interrupted[member] = started = Stopwatch.GetTimestamp();
                }
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(500) && forced.Add(member))
                {
                    // Freeze the witnessed actor before the final scan; any later scan failure still forces its cleanup.
                    Signal(member, SigStop); Signal(member, SigKill);
                }
            }
            return live;
        }
        try
        {
            // A finite cursor survives every quantum. Time spent on earlier host processes cannot discard later candidates.
            // The second snapshot is taken after all first-pass actors stop, to observe their final inherited pipe holders.
            // Actors born after the final snapshot still need broader lifecycle ownership; a fork storm cannot prolong a snapshot.
            for (int pass = 0; pass < 2 && failure is null; pass++)
            {
                DiscoveryCursor discovery = new();
                while (!discovery.Complete && failure is null)
                {
                    AdvanceStops();
                    Stopwatch quantum = Stopwatch.StartNew();
                    do
                    {
                        // Finish this candidate's stable proof before advancing; a deadline cannot skip a half-read holder.
                        Inspect(discovery.Next());
                        AdvanceStops();
                    } while (!discovery.Complete && failure is null && quantum.Elapsed < TimeSpan.FromMilliseconds(10));
                    await Task.Yield();
                }
                // Each late live actor gets its own grace from the actual SIGINT, not from a dead leader or a slow scan.
                // Output readers in GitRunner keep draining, and known actors without pipes are also awaited here.
                while (failure is null && AdvanceStops()) { await Task.Delay(1).ConfigureAwait(false); }
            }
        }
        catch (Exception exception) { failure ??= exception; }
        finally
        {
            // A failed final scan must not strand a process after SIGSTOP. Stable descriptors remain safe even after exit/reuse.
            foreach (OwnedProcess member in _members.AsEnumerable().Reverse())
            {
                try { Signal(member, SigKill); }
                catch (Exception exception) { failure ??= exception; }
            }
            // EOF/leader exit alone do not cover an owned child which closed every redirected pipe.
            // Keep the operation pending until all known actors are actually dead, including stopped or uninterruptible tasks.
            try
            {
                while (failure is null && _members.Any(member => member.IsAlive()))
                { await Task.Delay(1).ConfigureAwait(false); }
            }
            catch (Exception exception) { failure ??= exception; }
        }
        return failure;
    }

    private sealed class DiscoveryCursor
    {
        // Snapshot names, not numeric ownership: every candidate still requires a fresh stable reference and proof.
        private readonly string[] _directories = Directory.GetDirectories("/proc");
        private int _next;
        internal bool Complete => _next == _directories.Length;
        internal string Next() => _directories[_next++];
    }

    private void Inspect(string directory)
    {
        if (_leader == 0 || !int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || pid <= 1 || pid == Environment.ProcessId || _members.Any(member => member.Pid == pid && member.IsAlive())) { return; }
        // No numeric kill(-SID): a reused group ID must never make a new invocation our target.
        // New members need a live stable parent/session anchor across capture, or an actual FD on our still-pinned pipes.
        // An unobserved child which closes all pipes and becomes orphaned after the last anchor exits cannot be proven here,
        // even if it retained the old SID. Comprehensive ownership of such children remains a separate lifecycle requirement.
        // This unlocked prefilter only avoids needless descriptor captures for unrelated host actors. It grants no ownership.
        ProcessState? hint = ProcessState.Read(directory + "/stat");
        if (hint is not ProcessState suggested || !suggested.IsAlive
            || suggested.Session != _leader && !_members.Any(member => member.Pid == suggested.Parent)) { return; }
        OwnedProcess? candidate = OwnedProcess.Open(pid);
        if (candidate is null) { return; }
        bool owned = false;
        try
        {
            if (candidate.State() is not ProcessState initial || !initial.IsAlive) { return; }
            foreach (OwnedProcess anchor in _members)
            {
                if (anchor.Pid != initial.Parent && initial.Session != _leader) { continue; }
                if (anchor.State() is not ProcessState before || !before.IsAlive) { continue; }
                bool parent = initial.Parent == before.Pid;
                bool session = before.Session == _leader && initial.Session == _leader;
                if (!parent && !session) { continue; }
                if (candidate.State() is not ProcessState current || !current.IsAlive
                    || parent && current.Parent != before.Pid || session && current.Session != _leader) { continue; }
                if (anchor.State() is ProcessState after && after.IsAlive && after.Started == before.Started
                    && (!session || after.Session == _leader)) { owned = true; break; }
            }
            if (!owned && initial.Session == _leader && candidate.HasPipe(_pipes.Select(pipe => pipe.Identity))
                && candidate.State() is ProcessState holder && holder.IsAlive && holder.Session == _leader)
            { owned = true; }
            if (owned) { _members.Add(candidate); candidate = null; }
        }
        finally { candidate?.Dispose(); }
    }

    public void Dispose()
    {
        foreach (OwnedProcess member in _members) { member.Dispose(); }
        foreach ((SafeFileHandle handle, _) in _pipes) { handle.Dispose(); }
    }

    private readonly record struct ProcessState(int Pid, int Parent, int Session, long Started, char Status)
    {
        internal bool IsAlive => Status is not ('Z' or 'X' or 'x');
        internal static ProcessState? Read(string path)
        {
            try { return Parse(File.ReadAllText(path)); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        internal static ProcessState Parse(string stat)
        {
            int name = stat.IndexOf('('), end = stat.LastIndexOf(')');
            string[] fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(int.Parse(stat[..name].Trim(), CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture),
                int.Parse(fields[3], CultureInfo.InvariantCulture), long.Parse(fields[19], CultureInfo.InvariantCulture), fields[0][0]);
        }
    }

    private readonly record struct FileIdentity(ulong Device, ulong Inode)
    {
        internal static FileIdentity Read(SafeFileHandle handle)
        {
            // dev_t/ino_t are the first two 64-bit fields on both supported Linux ABIs; reserve more than struct stat needs.
            byte[] stat = new byte[256];
            if (Stat(handle, stat) != 0) { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot identify an owned Git pipe."); }
            return new(BinaryPrimitives.ReadUInt64LittleEndian(stat), BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(8)));
        }
    }

    private sealed class OwnedProcess(int pid, SafeFileHandle handle) : IDisposable
    {
        internal int Pid { get; } = pid;
        internal SafeFileHandle Handle { get; } = handle;
        private string DirectoryPath => "/proc/self/fd/" + Handle.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture);
        internal static OwnedProcess? Open(int pid)
        {
            int descriptor = OpenDirectory("/proc/" + pid.ToString(CultureInfo.InvariantCulture), DirectoryOnly | CloseOnExec);
            if (descriptor < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is 2 or 3 or 13) { return null; }
                throw new Win32Exception(error, "Cannot capture a Git process reference.");
            }
            return new(pid, new SafeFileHandle((IntPtr)descriptor, ownsHandle: true));
        }
        internal ProcessState? State() => ProcessState.Read(DirectoryPath + "/stat");
        internal bool IsAlive()
        {
            if (State() is ProcessState state && !state.IsAlive) { return false; }
            long result;
            do { result = SendSignal(SendSignalSyscall, Handle, 0, IntPtr.Zero, 0); }
            while (result != 0 && Marshal.GetLastPInvokeError() == Interrupted);
            if (result == 0) { return true; }
            int error = Marshal.GetLastPInvokeError();
            if (error == NoSuchProcess) { return false; }
            throw new Win32Exception(error, "Cannot determine whether an owned Git process has stopped.");
        }
        internal bool HasPipe(IEnumerable<FileIdentity> pipes)
        {
            try
            {
                foreach (string path in Directory.GetFileSystemEntries(DirectoryPath + "/fd"))
                {
                    // Inspecting ordinary file descriptors could stat a remote filesystem. Only anonymous pipes can match.
                    if (new FileInfo(path).LinkTarget is not string link || !link.StartsWith("pipe:[", StringComparison.Ordinal)) { continue; }
                    int descriptor = OpenDirectory(path, PathOnly | CloseOnExec);
                    if (descriptor < 0) { continue; }
                    using SafeFileHandle target = new((IntPtr)descriptor, ownsHandle: true);
                    if (pipes.Contains(FileIdentity.Read(target))) { return true; }
                }
            }
            catch (IOException) { /* The candidate can exit while its descriptors are being inspected. */ }
            catch (UnauthorizedAccessException) { }
            return false;
        }
        public void Dispose() => Handle.Dispose();
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Duplicate(SafePipeHandle descriptor, int command, int minimum);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Stat(SafeFileHandle descriptor, [Out] byte[] value);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SendSignal(long number, SafeFileHandle descriptor, int signal, IntPtr info, uint flags);
}
