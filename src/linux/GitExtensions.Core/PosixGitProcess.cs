using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux;

/// <summary>Private descriptors drive a supervisor which reserves Git's group identity until drain.</summary>
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
internal sealed class PosixGitProcess : IDisposable
{
    private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _primaryExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _acknowledgement = new(1, 1);
    private Stream _commands = Stream.Null, _events = Stream.Null;
    private Task<int>? _exit;
    private Task? _control;
    private int _pid, _gitPid, _exitCode;
    private bool _acknowledged, _stopping, _rejectedBeforeFork, _drained;
    internal Stream StandardInput { get; private set; } = Stream.Null;
    internal Stream StandardOutput { get; private set; } = Stream.Null;
    internal Stream StandardError { get; private set; } = Stream.Null;
    internal Task Control => _control!;
    internal Task<int> Started => _started.Task;
    internal Task<int> PrimaryExit => _primaryExit.Task;
    internal int ExitCode => _exit?.IsCompletedSuccessfully == true && _exit.Result == _exitCode ? _exitCode
        : throw new IOException("The supervisor exit differs from its terminal report.");

    internal static PosixGitProcess Start(ProcessStartInfo start, CancellationToken token, bool childCreatesSession = false,
        bool terminal = false, bool inheritConsole = false)
    {
        if (inheritConsole && !terminal || terminal && childCreatesSession)
        { throw new ArgumentException("A terminal owns its session and may inherit only its caller's standard I/O."); }
        token.ThrowIfCancellationRequested();
        string helper = Path.Combine(AppContext.BaseDirectory, "gitextensions-posix");
        if (!File.Exists(helper)) { throw new FileNotFoundException("The private POSIX Git supervisor is unavailable.", helper); }
        start.Environment.TryGetValue("PATH", out string? path);
        // The terminal's native exec loop uses its captured PATH and working
        // directory, with no shell fallback or implicit current-directory search.
        string executable = terminal ? start.FileName : GitConfiguredExecutable.Resolve(start.FileName, path);
        PosixGitProcess owner = new();
        try
        {
            using Pipe? input = inheritConsole ? null : new(PipeDirection.Out), output = inheritConsole ? null : new(PipeDirection.In),
                error = inheritConsole ? null : new(PipeDirection.In);
            using Pipe commands = new(PipeDirection.Out), events = new(PipeDirection.In);
            if (!inheritConsole)
            { owner.StandardInput = input!.TakeServer(); owner.StandardOutput = output!.TakeServer(); owner.StandardError = error!.TakeServer(); }
            owner._commands = commands.TakeServer(); owner._events = events.TakeServer();
            // None of the parent's descriptors is temporarily made inheritable.
            // Explicit high duplicates also avoid dup2 clobbering a later source.
            using SafeFileHandle fd0 = inheritConsole ? DuplicateStandard(0) : input!.Duplicate(),
                fd1 = inheritConsole ? DuplicateStandard(1) : output!.Duplicate(), fd2 = inheritConsole ? DuplicateStandard(2) : error!.Duplicate(),
                fd3 = commands.Duplicate(), fd4 = events.Duplicate();
            string[] prefix = terminal ? [helper, "3", "4", "--terminal", "--", executable]
                : childCreatesSession ? [helper, "3", "4", "--child-sets-session", "--", executable]
                : [helper, "3", "4", "--", executable];
            using NativeUtf8Vector arguments = new(prefix.Concat(start.ArgumentList));
            using NativeUtf8Vector environment = new(start.Environment.Where(pair => pair.Value is not null).Select(pair => pair.Key + "=" + pair.Value));
            string directory = string.IsNullOrEmpty(start.WorkingDirectory) ? Directory.GetCurrentDirectory() : start.WorkingDirectory;
            token.ThrowIfCancellationRequested();
            Check(Spawn(helper, directory, arguments.Pointer, environment.Pointer,
                [fd0.DangerousGetHandle().ToInt32(), fd1.DangerousGetHandle().ToInt32(), fd2.DangerousGetHandle().ToInt32(),
                 fd3.DangerousGetHandle().ToInt32(), fd4.DangerousGetHandle().ToInt32()], out int processId),
                "Cannot start the private POSIX supervisor.");
            owner._pid = processId;
            owner._exit = ObserveExitAsync(processId);
            owner._control = owner.ObserveAsync(token);
            return owner;
        }
        catch (Exception failure)
        {
            try { owner.Dispose(); } catch (Exception cleanup) { failure.Data["PosixFactoryCleanupFailure"] = cleanup; }
            throw;
        }
    }

    private static SafeFileHandle DuplicateStandard(int descriptor)
    {
        int copy = DuplicateRawDescriptor(descriptor, 64);
        if (copy < 0) { throw NativeError(Marshal.GetLastPInvokeError(), "Cannot inherit the interactive terminal stream."); }
        return new((IntPtr)copy, ownsHandle: true);
    }

    private async Task ObserveAsync(CancellationToken token)
    {
        Exception? nativeFailure = null;
        try
        {
            Record hello = await ReadAsync().ConfigureAwait(false);
            if (hello.Kind == 4 && hello.Actor == _pid)
            {
                _rejectedBeforeFork = true; throw NativeError(checked((int)hello.Value), "The private supervisor rejected startup.");
            }
            if (hello.Kind != 1 || hello.Actor != _pid || hello.Value != 0) { throw new InvalidDataException("Invalid private supervisor identity."); }
            await _acknowledgement.WaitAsync().ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (_stopping) { throw new OperationCanceledException(token); }
                // Commit the possible fork before sending ACK. Stop never kills
                // the supervisor directly once this admission has been made.
                _acknowledged = true;
                await _commands.WriteAsync("A"u8.ToArray()).ConfigureAwait(false);
            }
            finally { _acknowledgement.Release(); }
            while (true)
            {
                Record record = await ReadAsync().ConfigureAwait(false);
                if (record.Kind == 2 && _gitPid == 0 && record.Actor > 1 && record.Value == 0)
                { _gitPid = record.Actor; _started.TrySetResult(_gitPid); }
                else if (record.Kind == 5 && record.Actor > 1 && (_gitPid == 0 || record.Actor == _gitPid))
                { _primaryExit.TrySetResult(record.Actor); }
                else if (record.Kind == 4)
                {
                    nativeFailure ??= NativeError(checked((int)record.Value), "The supervised Git process failed.");
                    _started.TrySetException(nativeFailure);
                }
                else if (record.Kind == 3 && record.Actor > 1 && (_gitPid == 0 || record.Actor == _gitPid))
                {
                    _exitCode = checked((int)record.Value); _drained = true;
                    if (nativeFailure is not null) { throw nativeFailure; }
                    if (_gitPid == 0) { throw new InvalidDataException("Git completed without an execution acknowledgement."); }
                    return;
                }
                else { throw new InvalidDataException("Invalid private supervisor state transition."); }
            }
        }
        catch (Exception error) { _started.TrySetException(error); throw; }
        finally { _primaryExit.TrySetResult(_gitPid); }
    }

    internal Task WaitForExitAsync() => _exit!;
    internal async Task<Exception?> StopAsync()
    {
        try
        {
            if (_exit is null) { return null; }
            await _acknowledgement.WaitAsync().ConfigureAwait(false);
            try
            {
                _stopping = true;
                if (!_acknowledged)
                {
                    // This unmanaged direct child has not been reaped, so its
                    // PID cannot be reused. No Git fork is allowed before ACK,
                    // including when a dynamic loader blocks before HELLO.
                    if (KillReservedChild(_pid) != 0) { throw NativeError(Marshal.GetLastPInvokeError(), "Cannot stop the reserved supervisor."); }
                }
                else
                {
                    try { if (!_exit.IsCompleted) { await _commands.WriteAsync("C"u8.ToArray()).ConfigureAwait(false); } }
                    catch (IOException) { /* The terminal report and native exit decide whether drain was proved. */ }
                }
            }
            finally { _acknowledgement.Release(); }
            await _exit.ConfigureAwait(false);
            try { if (_control is not null) { await _control.ConfigureAwait(false); } }
            catch when (_drained || !_acknowledged || _rejectedBeforeFork) { }
            return _drained || !_acknowledged || _rejectedBeforeFork ? null
                : new IOException("The POSIX supervisor exited without proving the owned process drain.");
        }
        catch (Exception error) { return error; }
    }

    internal void CloseAfterStopFailure()
    { _commands.Dispose(); _events.Dispose(); StandardInput.Dispose(); StandardOutput.Dispose(); StandardError.Dispose(); }

    internal void AuthenticatePrimary(int processId)
    {
        if (processId != _gitPid || _drained || _exit?.IsCompleted == true)
        { throw new InvalidDataException("The authenticated launcher differs from the reserved primary child."); }
    }

    private static Task<int> ObserveExitAsync(int processId)
        // This wait must leave our direct child's PID reserved. A dedicated
        // blocking waiter avoids timer latency and ThreadPool starvation while
        // the admitted pipe/control operations await their own actual I/O.
        => Task.Factory.StartNew(() =>
        {
            int result = WaitChild(processId, out int code);
            if (result > 0) { return code; }
            throw NativeError(Marshal.GetLastPInvokeError(), "Cannot observe the private supervisor.");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private async Task<Record> ReadAsync()
    {
        byte[] bytes = new byte[16]; await _events.ReadExactlyAsync(bytes).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes) != 0x47455031) { throw new InvalidDataException("Invalid private supervisor record."); }
        return new(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)), checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8))),
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)));
    }

    public void Dispose()
    {
        _acknowledgement.Wait();
        try
        {
            if (_pid > 0 && !_acknowledged)
            {
                _stopping = true;
                if (KillReservedChild(_pid) != 0) { throw NativeError(Marshal.GetLastPInvokeError(), "Cannot stop the reserved supervisor."); }
            }
        }
        finally { _acknowledgement.Release(); }
        _commands.Dispose(); _events.Dispose(); StandardInput.Dispose(); StandardOutput.Dispose(); StandardError.Dispose();
        if (_pid > 0)
        {
            try { _exit?.GetAwaiter().GetResult(); }
            finally
            {
                // Only this owner reaps the native child, and only after all
                // possible stop operations and admitted I/O have finished.
                int processId = _pid; _pid = 0;
                if (ReapChild(processId) != 0) { throw NativeError(Marshal.GetLastPInvokeError(), "Cannot reap the private supervisor."); }
            }
        }
    }

    private static void Check(int error, string message) { if (error != 0) { throw NativeError(error, message); } }
    private static Win32Exception NativeError(int error, string message) => new(error, message + " " + new Win32Exception(error).Message);
    private readonly record struct Record(uint Kind, int Actor, uint Value);

    private sealed class Pipe : IDisposable
    {
        private readonly AnonymousPipeServerStream _server;
        private bool _transferred;
        internal Pipe(PipeDirection direction) => _server = new(direction, HandleInheritability.None);
        internal Stream TakeServer() { _transferred = true; return _server; }
        internal SafeFileHandle Duplicate()
        {
            int descriptor = DuplicateDescriptor(_server.ClientSafePipeHandle, 64);
            if (descriptor < 0) { throw NativeError(Marshal.GetLastPInvokeError(), "Cannot duplicate a private Git descriptor."); }
            return new((IntPtr)descriptor, ownsHandle: true);
        }
        public void Dispose() { _server.DisposeLocalCopyOfClientHandle(); if (!_transferred) { _server.Dispose(); } }
    }

    // Native code owns platform structs and variadic calls on each ABI.
    [DllImport("gitextensions-posix", EntryPoint = "ge_duplicate_cloexec", SetLastError = true)]
    private static extern int DuplicateDescriptor(SafePipeHandle descriptor, int minimum);
    [DllImport("gitextensions-posix", EntryPoint = "ge_duplicate_cloexec", SetLastError = true)]
    private static extern int DuplicateRawDescriptor(int descriptor, int minimum);
    [DllImport("gitextensions-posix", EntryPoint = "ge_spawn_supervisor")]
    private static extern int Spawn([MarshalAs(UnmanagedType.LPUTF8Str)] string executable,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string directory, IntPtr arguments, IntPtr environment,
        [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 5)] int[] descriptors, out int processId);
    [DllImport("gitextensions-posix", EntryPoint = "ge_wait_child", SetLastError = true)]
    private static extern int WaitChild(int processId, out int code);
    [DllImport("gitextensions-posix", EntryPoint = "ge_reap_child", SetLastError = true)]
    private static extern int ReapChild(int processId);
    [DllImport("gitextensions-posix", EntryPoint = "ge_kill_reserved_child", SetLastError = true)]
    private static extern int KillReservedChild(int processId);
}
