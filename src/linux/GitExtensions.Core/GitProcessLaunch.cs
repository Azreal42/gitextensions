using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitExtensions.Linux;

/// <summary>Private CLOEXEC control connection and exec-error witness, separate from Git's real output pipes.</summary>
[SupportedOSPlatform("linux")]
internal sealed class GitProcessLaunch : IDisposable
{
    private readonly LinuxUnixSocketDirectory _directory;
    private readonly string _witness, _controlPath, _executable, _workingDirectory;
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _controlCancellation = new();
    private int _acknowledged;
    private GitProcessLaunch(LinuxUnixSocketDirectory directory, string executable, string workingDirectory)
    {
        _directory = directory; _witness = Path.Combine(directory.DirectoryPath, "exec-error"); _controlPath = directory.SocketPath;
        _executable = executable; _workingDirectory = workingDirectory;
    }

    internal static GitProcessLaunch Configure(ProcessStartInfo startInfo)
        => Configure(startInfo, Environment.GetEnvironmentVariable("PATH") ?? "");

    internal static GitProcessLaunch Configure(ProcessStartInfo startInfo, string executableSearchPath)
    {
        string original = startInfo.FileName, target = ResolveDirectExecutable(original);
        string parentDirectory = Directory.GetCurrentDirectory(), parentPath = executableSearchPath;
        string[] arguments = startInfo.ArgumentList.ToArray();
        LinuxUnixSocketDirectory directory = LinuxUnixSocketDirectory.Create("gitextensions-exec-");
        GitProcessLaunch? launch = null;
        try
        {
            launch = new(directory, original, startInfo.WorkingDirectory);
            launch._listener.Bind(new UnixDomainSocketEndPoint(launch._controlPath));
            File.SetUnixFileMode(launch._controlPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            launch._listener.Listen(1);
            startInfo.FileName = Path.Combine(AppContext.BaseDirectory, "gitextensions-exec");
            startInfo.ArgumentList.Clear();
            foreach (string argument in new[] { launch._controlPath, launch._witness, target, parentDirectory, parentPath, original }.Concat(arguments)) { startInfo.ArgumentList.Add(argument); }
            return launch;
        }
        catch (Exception failure)
        {
            try { if (launch is null) { directory.Dispose(); } else { launch.Dispose(); } }
            catch (Exception cleanupFailure) { failure.Data["GitProcessLaunchCleanupFailure"] = cleanupFailure; }
            throw;
        }
    }

    internal async Task ObserveExecAsync(int processId, GitProcessGroup processes, CancellationToken cancellationToken)
        => await ObserveExecAsync(processId, processes.AuthenticateLeader, cancellationToken).ConfigureAwait(false);

    internal async Task ObserveExecAsync(int processId, Action<int> authenticate, CancellationToken cancellationToken)
    {
        try
        {
            using Socket connection = await _listener.AcceptAsync(_controlCancellation.Token).ConfigureAwait(false);
            uint size = 12; PeerCredentials peer = new() { Pid = 0, User = 0, Group = 0 };
            if (GetPeerCredentials(connection.SafeHandle.DangerousGetHandle().ToInt32(), 1, 17, ref peer, ref size) != 0)
            { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot verify the private Git launcher peer."); }
            if (size != 12 || peer.Pid != processId || peer.User != GetEffectiveUser())
            { throw new InvalidDataException("The private Git launcher peer does not match this invocation."); }
            byte[]? hello = await ReceiveRecordAsync(connection).ConfigureAwait(false);
            if (hello is null || !ValidRecord(hello, processId, 0, 0))
            { throw new InvalidDataException("The private Git launcher did not provide a valid handshake."); }
            authenticate(processId);
            cancellationToken.ThrowIfCancellationRequested();
            // The child cannot exec or exit as Git until this acknowledgement is sent. This removes the exit/accept race.
            Volatile.Write(ref _acknowledged, 1);
            using (CancellationTokenSource acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _controlCancellation.Token))
            {
                if (await connection.SendAsync(new byte[] { 0xA5 }, SocketFlags.None, acknowledgement.Token).ConfigureAwait(false) != 1)
                { throw new IOException("The private Git launcher acknowledgement was not sent."); }
            }
            byte[]? errorRecord = await ReceiveRecordAsync(connection).ConfigureAwait(false);
            if (errorRecord is null)
            {
                if (File.Exists(_witness)) { throw new InvalidDataException("The Git launcher control connection ended with an unexplained exec-error witness."); }
                return; // execv closed the native CLOEXEC descriptor; Git's exit code remains entirely its own.
            }
            uint error = BinaryPrimitives.ReadUInt32BigEndian(errorRecord.AsSpan(8)), stage = BinaryPrimitives.ReadUInt32BigEndian(errorRecord.AsSpan(12));
            if (error == 0 || error > int.MaxValue || stage is not (1 or 2) || !ValidRecord(errorRecord, processId, error, stage)
                || await ReceiveRecordAsync(connection).ConfigureAwait(false) is not null)
            { throw new InvalidDataException("The private Git launcher returned an invalid exec-error record."); }
            Win32Exception nativeError = new((int)error, "An error occurred trying to start process '" + _executable + "' with working directory '" + _workingDirectory + "': " + new Win32Exception((int)error).Message);
            // The authenticated control channel still reports the original errno if the filesystem cannot create a witness.
            try { VerifyWitness(errorRecord); }
            catch (Exception witnessFailure) { nativeError.Data["GitProcessExecWitnessFailure"] = witnessFailure; }
            throw nativeError;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { cancellationToken.ThrowIfCancellationRequested(); throw; }
        catch (OperationCanceledException exception) when (_controlCancellation.IsCancellationRequested)
        { throw new IOException("The private Git launcher exited without a valid acknowledged handshake.", exception); }
    }

    internal void ParentExited()
    {
        if (Volatile.Read(ref _acknowledged) == 0) { _controlCancellation.Cancel(); }
    }

    internal void CancelUnacknowledgedHandshake()
    {
        if (Volatile.Read(ref _acknowledged) != 0) { return; }
        _controlCancellation.Cancel();
        // Also reject a connected child still in the listener backlog; it must never wait forever for an ACK.
        _listener.Dispose();
    }

    private async Task<byte[]?> ReceiveRecordAsync(Socket connection)
    {
        byte[] record = new byte[16]; int position = 0;
        while (position < record.Length)
        {
            int count = await connection.ReceiveAsync(record.AsMemory(position), SocketFlags.None, _controlCancellation.Token).ConfigureAwait(false);
            if (count == 0)
            {
                if (position == 0) { return null; }
                throw new InvalidDataException("The private Git launcher control record is incomplete.");
            }
            position += count;
        }
        return record;
    }

    private static bool ValidRecord(byte[] record, int processId, uint error, uint stage) => record.AsSpan(0, 4).SequenceEqual("GEX1"u8)
        && BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4)) == processId
        && BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(8)) == error && BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(12)) == stage;

    private void VerifyWitness(byte[] errorRecord)
    {
        using FileStream file = new(_witness, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (new FileInfo(_witness).LinkTarget is not null || file.Length != 16
            || File.GetUnixFileMode(_witness) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        { throw new InvalidDataException("The private Git exec-error witness is invalid."); }
        Span<byte> bytes = stackalloc byte[16]; file.ReadExactly(bytes);
        if (!bytes.SequenceEqual(errorRecord)) { throw new InvalidDataException("The exec-error witness differs from the authenticated control record."); }
    }

    internal static string ResolveDirectExecutable(string executable)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        if (executable.Contains('\0')) { throw new ArgumentException("Git executable paths cannot contain NUL.", nameof(executable)); }
        if (Path.IsPathRooted(executable)) { return executable; }
        // Process.Unix.ResolvePath searches the executable directory, caller cwd, then the parent's PATH.
        if (Environment.ProcessPath is string processPath)
        {
            string candidate = Path.Combine(Path.GetDirectoryName(processPath)!, executable);
            if (File.Exists(candidate)) { return candidate; }
        }
        string current = Path.Combine(Directory.GetCurrentDirectory(), executable);
        if (File.Exists(current)) { return current; }
        // The C launcher performs PATH selection using the same stat/mode/euid/group predicate as .NET.
        // An effective-access/ACL probe here could incorrectly skip the chosen program in favor of a different Git.
        return "";
    }

    public void Dispose()
    {
        try { _listener.Dispose(); }
        finally { try { _controlCancellation.Dispose(); } finally { _directory.Dispose(); } }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials { internal int Pid; internal uint User, Group; }
    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetPeerCredentials(int socket, int level, int option, ref PeerCredentials value, ref uint length);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUser();
}
