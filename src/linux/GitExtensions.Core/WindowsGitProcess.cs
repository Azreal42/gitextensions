using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux;

/// <summary>Owns the kernel job before any Git code can create descendants.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsGitProcess : IDisposable
{
    private const uint KillOnJobClose = 0x2000, Suspended = 4, UnicodeEnvironment = 0x400;
    private const uint ExtendedStartup = 0x80000, NoWindow = 0x08000000, UseStandardHandles = 0x100;
    private readonly KernelHandle _job;
    private SafeProcessHandle? _process;
    private Task? _exit;
    internal int Id { get; private set; }
    internal Stream StandardInput { get; private set; } = Stream.Null;
    internal Stream StandardOutput { get; private set; } = Stream.Null;
    internal Stream StandardError { get; private set; } = Stream.Null;

    private WindowsGitProcess()
    {
        _job = new(CreateJobObject(IntPtr.Zero, null));
        if (_job.IsInvalid) { throw Error("Cannot create the private Git job."); }
        ExtendedLimits limits = new() { Basic = new() { Flags = KillOnJobClose } };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { Win32Exception error = Error("Cannot configure the private Git job."); _job.Dispose(); throw error; }
    }

    internal static WindowsGitProcess Start(ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        WindowsGitProcess owner = new();
        bool assigned = false;
        try
        {
            using ChildPipe input = new(PipeDirection.Out), output = new(PipeDirection.In), error = new(PipeDirection.In);
            owner.StandardInput = input.TakeServer(); owner.StandardOutput = output.TakeServer(); owner.StandardError = error.TakeServer();
            using StartupAttributes attributes = new([input.ChildHandle, output.ChildHandle, error.ChildHandle]);
            StartupInfoEx startup = new()
            {
                Startup = new() { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = UseStandardHandles,
                    Input = input.ChildHandle, Output = output.ChildHandle, Error = error.ChildHandle },
                Attributes = attributes.Pointer
            };
            start.Environment.TryGetValue("PATH", out string? path);
            string executable = GitConfiguredExecutable.Resolve(start.FileName, path);
            StringBuilder command = new(QuoteArgument(executable));
            if (start.ArgumentList.Count != 0)
            { foreach (string argument in start.ArgumentList) { command.Append(' ').Append(QuoteArgument(argument)); } }
            else if (!string.IsNullOrEmpty(start.Arguments)) { command.Append(' ').Append(start.Arguments); }
            string environment = string.Join('\0', start.Environment.Where(pair => pair.Value is not null)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
            IntPtr environmentBlock = Marshal.StringToHGlobalUni(environment);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    Suspended | UnicodeEnvironment | ExtendedStartup | NoWindow, environmentBlock,
                    string.IsNullOrEmpty(start.WorkingDirectory) ? Directory.GetCurrentDirectory() : start.WorkingDirectory,
                    ref startup, out ProcessInformation process))
                { throw Error("Cannot start the private Git process."); }
                owner._process = new(process.Process, ownsHandle: true); owner.Id = checked((int)process.ProcessId);
                using KernelHandle thread = new(process.Thread);
                if (!AssignProcessToJobObject(owner._job, owner._process)) { throw Error("Cannot own the private Git process before execution."); }
                assigned = true;
                token.ThrowIfCancellationRequested();
                if (ResumeThread(thread) == uint.MaxValue) { throw Error("Cannot resume the owned Git process."); }
            }
            finally { Marshal.FreeHGlobal(environmentBlock); }
            return owner;
        }
        catch (Exception failure)
        {
            // A child rejected before assignment is still suspended. Never leave it behind or resume it unowned.
            if (!assigned && owner._process is not null)
            {
                if (TerminateProcess(owner._process, 1)) { WaitForSingleObject(owner._process, uint.MaxValue); }
                else { failure.Data["GitUnassignedProcessStopFailure"] = Error("Cannot stop the suspended Git process."); }
            }
            owner.Dispose(); throw;
        }
    }

    internal int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(_process!, out uint code)) { throw Error("Cannot read the Git exit code."); }
            return unchecked((int)code);
        }
    }

    internal Task WaitForExitAsync() => _exit ??= WaitCoreAsync();
    private async Task WaitCoreAsync()
    {
        using ProcessWait wait = new(_process!);
        TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(wait,
            (_, _) => exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        try { await exited.Task.ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }

    internal async Task WaitForEmptyAsync()
    {
        while (true)
        {
            if (!QueryInformationJobObject(_job, 1, out Accounting accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
            { throw Error("Cannot observe the private Git job."); }
            if (accounting.ActiveProcesses == 0) { return; }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    internal async Task<Exception?> StopAsync()
    {
        try
        {
            if (!TerminateJobObject(_job, 0xC000013A)) { throw Error("Cannot stop the private Git job."); }
            await WaitForEmptyAsync().ConfigureAwait(false); return null;
        }
        catch (Exception error) { return error; }
    }

    internal void CloseAfterStopFailure()
    { _job.Dispose(); StandardInput.Dispose(); StandardOutput.Dispose(); StandardError.Dispose(); }

    public void Dispose()
    { _job.Dispose(); StandardInput.Dispose(); StandardOutput.Dispose(); StandardError.Dispose(); _process?.Dispose(); }

    // The CRT argv rules are also used by Git for Windows. No shell interprets these values.
    internal static string QuoteArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) { throw new ArgumentException("Process arguments cannot contain NUL.", nameof(value)); }
        if (value.Length != 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) { return value; }
        StringBuilder result = new("\""); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); }
            else { result.Append('\\', slashes).Append(character); }
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static Win32Exception Error(string message)
    {
        int error = Marshal.GetLastPInvokeError();
        return new(error, message + " " + new Win32Exception(error).Message);
    }

    private sealed class ChildPipe : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly NamedPipeClientStream _child;
        private bool _transferred;
        internal IntPtr ChildHandle => _child.SafePipeHandle.DangerousGetHandle();
        internal ChildPipe(PipeDirection parentDirection)
        {
            string name = "gitextensions-stdio-" + Guid.NewGuid().ToString("N");
            _server = new(name, parentDirection, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                _child = new(".", name, parentDirection == PipeDirection.In ? PipeDirection.Out : PipeDirection.In,
                    PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.Inheritable);
                try
                {
                    Task connected = _server.WaitForConnectionAsync();
                    _child.Connect(10_000); connected.GetAwaiter().GetResult();
                }
                catch { _child.Dispose(); throw; }
            }
            catch { _server.Dispose(); throw; }
        }
        internal Stream TakeServer() { _transferred = true; return _server; }
        public void Dispose() { _child.Dispose(); if (!_transferred) { _server.Dispose(); } }
    }

    private sealed class StartupAttributes : IDisposable
    {
        internal IntPtr Pointer { get; }
        private readonly IntPtr _handles;
        private bool _initialized;
        internal StartupAttributes(IntPtr[] handles)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0) { throw Error("Cannot size Git startup attributes."); }
            Pointer = Marshal.AllocHGlobal(checked((nint)size));
            try
            {
                if (!InitializeProcThreadAttributeList(Pointer, 1, 0, ref size)) { throw Error("Cannot initialize Git startup attributes."); }
                _initialized = true; _handles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                Marshal.Copy(handles, 0, _handles, handles.Length);
                if (!UpdateProcThreadAttribute(Pointer, 0, (nuint)0x20002, _handles, (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                { throw Error("Cannot restrict Git's inherited handles."); }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_initialized) { DeleteProcThreadAttributeList(Pointer); }
            Marshal.FreeHGlobal(_handles); Marshal.FreeHGlobal(Pointer);
        }
    }

    private sealed class ProcessWait : WaitHandle
    {
        private readonly SafeProcessHandle _owner;
        private bool _reference;
        internal ProcessWait(SafeProcessHandle owner)
        {
            _owner = owner;
            try { owner.DangerousAddRef(ref _reference); SafeWaitHandle = new(owner.DangerousGetHandle(), ownsHandle: false); }
            catch { if (_reference) { owner.DangerousRelease(); _reference = false; } throw; }
        }
        protected override void Dispose(bool explicitDisposing)
        { base.Dispose(explicitDisposing); if (_reference) { _owner.DangerousRelease(); _reference = false; } }
    }

    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal KernelHandle(IntPtr value) : base(ownsHandle: true) { SetHandle(value); }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size; internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, CountX, CountY, Fill, Flags;
        internal ushort ShowWindow, ReservedSize; internal IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime; internal uint Flags; internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit; internal nuint Affinity; internal uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        internal BasicLimits Basic; internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        internal long User, Kernel, PeriodUser, PeriodKernel;
        internal uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(KernelHandle job, int information, ref ExtendedLimits value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(KernelHandle job, int information, out Accounting value, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(KernelHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(KernelHandle job, uint code);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
