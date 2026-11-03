using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux;

/// <summary>Private descriptors drive a supervisor which reserves Git's group identity until drain.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacGitProcess : IDisposable
{
    private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Stream _commands = Stream.Null, _events = Stream.Null;
    private Task<int>? _exit;
    private Task? _control;
    private int _pid, _gitPid, _exitCode;
    private bool _acknowledged, _rejectedBeforeFork, _drained;
    internal Stream StandardInput { get; private set; } = Stream.Null;
    internal Stream StandardOutput { get; private set; } = Stream.Null;
    internal Stream StandardError { get; private set; } = Stream.Null;
    internal Task Control => _control!;
    internal Task<int> Started => _started.Task;
    internal int ExitCode => _exit?.IsCompletedSuccessfully == true && _exit.Result == _exitCode ? _exitCode
        : throw new IOException("The supervisor exit differs from its terminal report.");

    internal static MacGitProcess Start(ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string helper = Path.Combine(AppContext.BaseDirectory, "gitextensions-posix");
        if (!File.Exists(helper)) { throw new FileNotFoundException("The private POSIX Git supervisor is unavailable.", helper); }
        start.Environment.TryGetValue("PATH", out string? path);
        string executable = GitConfiguredExecutable.Resolve(start.FileName, path);
        MacGitProcess owner = new();
        try
        {
            using Pipe input = new(PipeDirection.Out), output = new(PipeDirection.In), error = new(PipeDirection.In),
                commands = new(PipeDirection.Out), events = new(PipeDirection.In);
            owner.StandardInput = input.TakeServer(); owner.StandardOutput = output.TakeServer(); owner.StandardError = error.TakeServer();
            owner._commands = commands.TakeServer(); owner._events = events.TakeServer();
            // None of the parent's descriptors is temporarily made inheritable.
            // Explicit high duplicates also avoid dup2 clobbering a later source.
            using SafeFileHandle fd0 = input.Duplicate(), fd1 = output.Duplicate(), fd2 = error.Duplicate(),
                fd3 = commands.Duplicate(), fd4 = events.Duplicate();
            using SpawnAttributes spawn = new();
            SafeFileHandle[] descriptors = [fd0, fd1, fd2, fd3, fd4];
            for (int index = 0; index < descriptors.Length; index++)
            { Check(AddDuplicate(ref spawn.Actions, descriptors[index].DangerousGetHandle().ToInt32(), index), "Cannot map a private Git descriptor."); }
            Check(AddDirectory(ref spawn.Actions, string.IsNullOrEmpty(start.WorkingDirectory) ? Directory.GetCurrentDirectory() : start.WorkingDirectory),
                "Cannot set Git's private working directory.");
            using Utf8Vector arguments = new(new[] { helper, "3", "4", "--", executable }.Concat(start.ArgumentList));
            using Utf8Vector environment = new(start.Environment.Where(pair => pair.Value is not null).Select(pair => pair.Key + "=" + pair.Value));
            token.ThrowIfCancellationRequested();
            Check(Spawn(out int processId, helper, ref spawn.Actions, ref spawn.Attributes, arguments.Pointer, environment.Pointer),
                "Cannot start the private POSIX supervisor.");
            owner._pid = processId;
            owner._exit = owner.ReapAsync();
            owner._control = owner.ObserveAsync(token);
            return owner;
        }
        catch { owner.Dispose(); throw; }
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
            token.ThrowIfCancellationRequested();
            await _commands.WriteAsync("A"u8.ToArray()).ConfigureAwait(false); _acknowledged = true;
            while (true)
            {
                Record record = await ReadAsync().ConfigureAwait(false);
                if (record.Kind == 2 && _gitPid == 0 && record.Actor > 1 && record.Value == 0)
                { _gitPid = record.Actor; _started.TrySetResult(_gitPid); }
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
    }

    internal Task WaitForExitAsync() => _exit!;
    internal async Task<Exception?> StopAsync()
    {
        try
        {
            if (_exit is null) { return null; }
            try { if (!_exit.IsCompleted) { await _commands.WriteAsync("C"u8.ToArray()).ConfigureAwait(false); } }
            catch (IOException) { /* The terminal report and actual child wait decide whether drain was proved. */ }
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

    private async Task<int> ReapAsync()
    {
        while (true)
        {
            int result = WaitPid(_pid, out int status, 1);
            if (result == _pid) { return (status & 0x7f) == 0 ? status >> 8 & 0xff : 128 + (status & 0x7f); }
            if (result < 0 && Marshal.GetLastPInvokeError() != 4) { throw NativeError(Marshal.GetLastPInvokeError(), "Cannot reap the private supervisor."); }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private async Task<Record> ReadAsync()
    {
        byte[] bytes = new byte[16]; await _events.ReadExactlyAsync(bytes).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes) != 0x47455031) { throw new InvalidDataException("Invalid private supervisor record."); }
        return new(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)), checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8))),
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)));
    }

    public void Dispose()
    {
        _commands.Dispose(); _events.Dispose(); StandardInput.Dispose(); StandardOutput.Dispose(); StandardError.Dispose();
        // Factory failure before ACK also closes the private pipe: the supervisor
        // must exit without starting Git. Only this class reaps its unmanaged child.
        if (_pid > 0)
        {
            if (_exit is not null) { _exit.GetAwaiter().GetResult(); }
            else { int result; do { result = WaitPid(_pid, out _, 0); } while (result < 0 && Marshal.GetLastPInvokeError() == 4); }
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

    private sealed class SpawnAttributes : IDisposable
    {
        internal IntPtr Actions, Attributes;
        private bool _actionsInitialized, _attributesInitialized;
        internal SpawnAttributes()
        {
            Check(InitializeActions(out Actions), "Cannot initialize private spawn actions.");
            _actionsInitialized = true;
            try
            {
                Check(InitializeAttributes(out Attributes), "Cannot initialize private spawn attributes.");
                _attributesInitialized = true;
                Check(SetFlags(ref Attributes, 0x4400), "Cannot isolate the supervisor's session and descriptors.");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_attributesInitialized) { DestroyAttributes(ref Attributes); _attributesInitialized = false; }
            if (_actionsInitialized) { DestroyActions(ref Actions); _actionsInitialized = false; }
        }
    }

    private sealed class Utf8Vector : IDisposable
    {
        private readonly List<IntPtr> _strings = [];
        internal IntPtr Pointer { get; private set; }
        internal Utf8Vector(IEnumerable<string> values)
        {
            try
            {
                foreach (string value in values)
                {
                    if (value.Contains('\0')) { throw new ArgumentException("Native process values cannot contain NUL.", nameof(values)); }
                    _strings.Add(Marshal.StringToCoTaskMemUTF8(value));
                }
                IntPtr[] vector = [.. _strings, IntPtr.Zero];
                Pointer = Marshal.AllocHGlobal(vector.Length * IntPtr.Size); Marshal.Copy(vector, 0, Pointer, vector.Length);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (IntPtr value in _strings) { Marshal.FreeCoTaskMem(value); } Marshal.FreeHGlobal(Pointer); }
    }

    // Darwin arm64 passes fcntl's variadic argument on the stack. Keep that ABI
    // inside a native bridge with a fixed signature on both Mac architectures.
    [DllImport("libgitextensions-posix.dylib", EntryPoint = "ge_duplicate_cloexec", SetLastError = true)]
    private static extern int DuplicateDescriptor(SafePipeHandle descriptor, int minimum);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")] private static extern int InitializeActions(out IntPtr actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")] private static extern int DestroyActions(ref IntPtr actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")] private static extern int AddDuplicate(ref IntPtr actions, int descriptor, int target);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np")] private static extern int AddDirectory(ref IntPtr actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("libc", EntryPoint = "posix_spawnattr_init")] private static extern int InitializeAttributes(out IntPtr attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")] private static extern int DestroyAttributes(ref IntPtr attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")] private static extern int SetFlags(ref IntPtr attributes, short flags);
    [DllImport("libc", EntryPoint = "posix_spawn")] private static extern int Spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref IntPtr actions, ref IntPtr attributes, IntPtr arguments, IntPtr environment);
    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)] private static extern int WaitPid(int pid, out int status, int options);
}
