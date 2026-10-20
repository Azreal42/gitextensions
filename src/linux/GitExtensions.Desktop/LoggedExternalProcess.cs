using System.Diagnostics;

namespace GitExtensions.Linux.Desktop;

/// <summary>Observes a user-launched process without owning its lifetime or blocking application shutdown.</summary>
internal sealed class LoggedExternalProcess
{
    private readonly TaskCompletionSource<int?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly GitCommandLogProcessOperation _operation;
    private readonly Process? _process;
    private int _finished;
    internal Task<int?> Completion => _completion.Task;
    internal int? ProcessId { get; }
    internal bool HasProcess => _process is not null;
    internal Task<int?> WaitAsync(CancellationToken cancellationToken) => Completion.WaitAsync(cancellationToken);

    private LoggedExternalProcess(GitCommandLogProcessOperation operation, Process? process)
    {
        _operation = operation; _process = process;
        if (process is null) { FinishUnobserved(); return; }
        try
        {
            int processId = process.Id; ProcessId = processId;
            operation.SetProcessId(processId);
            process.Exited += OnExited;
            process.EnableRaisingEvents = true;
            if (process.HasExited) { FinishExited(); }
        }
        catch (Exception) { FinishUnobserved(); } // A successful launch must survive an unavailable process handle.
    }

    internal static LoggedExternalProcess Start(ProcessStartInfo startInfo, GitCommandLogProcessKind kind,
        Func<ProcessStartInfo, Process?>? launch = null, GitCommandLogProcessContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(startInfo.FileName);
        if (startInfo.FileName.Contains('\0') || startInfo.WorkingDirectory.Contains('\0') || startInfo.Arguments.Contains('\0'))
        { throw new ArgumentException("Process launch values cannot contain NUL.", nameof(startInfo)); }
        foreach (string argument in startInfo.ArgumentList)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (argument.Contains('\0')) { throw new ArgumentException("Process arguments cannot contain NUL.", nameof(startInfo)); }
        }
        // Capture the caller before any async continuation; retain no executable, URL, arguments or output.
        context ??= GitCommandLog.CaptureProcessContext();
        cancellationToken.ThrowIfCancellationRequested();
        GitCommandLogProcessOperation operation = GitCommandLog.StartProcess(kind, startInfo.WorkingDirectory, context);
        Process? process;
        try { process = (launch ?? Process.Start)(startInfo); }
        catch { operation.Complete(null); throw; }
        return new(operation, process);
    }

    private void OnExited(object? sender, EventArgs args) => FinishExited();
    private void FinishExited()
    {
        int exit;
        try { exit = _process!.ExitCode; }
        catch (Exception) { FinishUnobserved(); return; }
        if (Interlocked.Exchange(ref _finished, 1) != 0) { return; }
        _operation.Complete(exit);
        ReleaseHandle(); _completion.TrySetResult(exit);
    }
    private void FinishUnobserved()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) { return; }
        _operation.CompleteUnobserved();
        ReleaseHandle(); _completion.TrySetResult(null);
    }
    private void ReleaseHandle()
    {
        // Disposing a Process releases the component/handle; it does not terminate the native application.
        if (_process is null) { return; }
        try { _process.Exited -= OnExited; _process.Dispose(); }
        catch (Exception) { /* Observation cleanup must not affect the launched application. */ }
    }
}
