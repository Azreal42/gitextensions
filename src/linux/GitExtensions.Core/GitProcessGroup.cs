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
        void Signal(OwnedProcess member, int signal)
        {
            int result;
            do { result = (int)SendSignal(SendSignalSyscall, member.Handle, signal, IntPtr.Zero, 0); }
            while (result != 0 && Marshal.GetLastPInvokeError() == Interrupted);
            if (result != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != NoSuchProcess) { failure ??= new Win32Exception(error, "Cannot signal an owned Git process."); }
            }
        }
        try
        {
            Stopwatch discovery = Stopwatch.StartNew(); Discover(discovery, TimeSpan.FromMilliseconds(10));
            // Readers continue to drain in GitRunner throughout this grace period. Cancellation remains the original outcome.
            Stopwatch grace = Stopwatch.StartNew();
            HashSet<OwnedProcess> interrupted = [];
            while (true)
            {
                foreach (OwnedProcess member in _members)
                {
                    if (grace.Elapsed >= TimeSpan.FromMilliseconds(500)) { break; }
                    if (interrupted.Add(member)) { Signal(member, SigInt); }
                }
                if (grace.Elapsed >= TimeSpan.FromMilliseconds(500) || failure is not null) { break; }
                bool live = false;
                foreach (OwnedProcess member in _members)
                {
                    if (grace.Elapsed >= TimeSpan.FromMilliseconds(500) || member.IsAlive()) { live = true; break; }
                }
                if (!live)
                {
                    int before = _members.Count; Discover(grace, TimeSpan.FromMilliseconds(500));
                    if (before == _members.Count) { break; }
                }
                TimeSpan remaining = TimeSpan.FromMilliseconds(500) - grace.Elapsed;
                if (remaining <= TimeSpan.Zero || failure is not null) { break; }
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(10) ? remaining : TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
                Discover(grace, TimeSpan.FromMilliseconds(500));
            }
            // Freeze witnessed parents before the final descendant scan, so they cannot keep forking during forced cleanup.
            HashSet<OwnedProcess> frozen = [];
            Stopwatch forcedDiscovery = Stopwatch.StartNew();
            int previous = _members.Count;
            for (int pass = 0; pass < 4; pass++)
            {
                foreach (OwnedProcess member in _members) { if (frozen.Add(member)) { Signal(member, SigStop); } }
                previous = _members.Count; Discover(forcedDiscovery, TimeSpan.FromMilliseconds(100));
                if (previous == _members.Count || failure is not null || forcedDiscovery.Elapsed >= TimeSpan.FromMilliseconds(100)) { break; }
            }
            foreach (OwnedProcess member in _members.AsEnumerable().Reverse()) { Signal(member, SigKill); }
            // A parent can have exited before its final pipe holder was first observed; retain the pinned-pipe proof here too.
            discovery.Restart(); previous = _members.Count; Discover(discovery, TimeSpan.FromMilliseconds(20));
            foreach (OwnedProcess member in _members.Skip(previous)) { Signal(member, SigKill); }
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

    private void Discover(Stopwatch clock, TimeSpan deadline)
    {
        if (_leader == 0) { return; }
        // No numeric kill(-SID): a reused group ID must never make a new invocation our target.
        // New members need a live stable parent/session anchor across capture, or an actual FD on our still-pinned pipes.
        // An unobserved child which closes all pipes and becomes orphaned after the last anchor exits cannot be proven here,
        // even if it retained the old SID. Comprehensive ownership of such children remains a separate lifecycle requirement.
        // One bounded pass; Stop signals captured members between passes even if another actor keeps forking.
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (clock.Elapsed >= deadline) { return; }
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                || pid <= 1 || pid == Environment.ProcessId || _members.Any(member => member.Pid == pid && member.IsAlive())) { continue; }
            OwnedProcess? candidate = OwnedProcess.Open(pid);
            if (candidate is null) { continue; }
            bool owned = false;
            try
            {
                if (candidate.State() is not ProcessState initial || !initial.IsAlive) { continue; }
                foreach (OwnedProcess anchor in _members)
                {
                    if (clock.Elapsed >= deadline) { return; }
                    if (anchor.State() is not ProcessState before || !before.IsAlive) { continue; }
                    bool parent = initial.Parent == before.Pid;
                    bool session = before.Session == _leader && initial.Session == _leader;
                    if (!parent && !session) { continue; }
                    if (candidate.State() is not ProcessState current || !current.IsAlive
                        || parent && current.Parent != before.Pid || session && current.Session != _leader) { continue; }
                    if (anchor.State() is ProcessState after && after.IsAlive && after.Started == before.Started
                        && (!session || after.Session == _leader)) { owned = true; break; }
                }
                if (!owned && initial.Session == _leader && candidate.HasPipe(_pipes.Select(pipe => pipe.Identity), () => clock.Elapsed >= deadline)
                    && candidate.State() is ProcessState holder && holder.IsAlive && holder.Session == _leader)
                { owned = true; }
                if (owned) { _members.Add(candidate); candidate = null; }
            }
            finally { candidate?.Dispose(); }
        }
    }

    public void Dispose()
    {
        foreach (OwnedProcess member in _members) { member.Dispose(); }
        foreach ((SafeFileHandle handle, _) in _pipes) { handle.Dispose(); }
    }

    private readonly record struct ProcessState(int Pid, int Parent, int Session, long Started, char Status)
    {
        internal bool IsAlive => Status is not ('Z' or 'X' or 'x');
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
        internal ProcessState? State()
        {
            try { return ProcessState.Parse(File.ReadAllText(DirectoryPath + "/stat")); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
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
        internal bool HasPipe(IEnumerable<FileIdentity> pipes, Func<bool> expired)
        {
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(DirectoryPath + "/fd"))
                {
                    if (expired()) { return false; }
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
