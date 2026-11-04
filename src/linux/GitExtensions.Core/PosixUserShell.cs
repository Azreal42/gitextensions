using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitExtensions.Linux;

/// <summary>Transfers a single-purpose terminal helper to a user shell, retaining its PID and native job-control semantics.</summary>
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
public static class PosixUserShell
{
    /// <summary>Replaces the caller process. Use only in the private terminal helper, after its startup channel has closed.</summary>
    [DoesNotReturn]
    public static int ReplaceCurrentProcess(string workingDirectory, GitExecutionEnvironment environment, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { throw new PlatformNotSupportedException("A user shell requires POSIX exec."); }
        ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Path.IsPathFullyQualified(environment.Executable) || environment.Executable.Contains('\0') || workingDirectory.Contains('\0')
            || environment.EnvironmentVariables.Any(pair => pair.Key.Length == 0 || pair.Key.Contains('=') || pair.Key.Contains('\0')))
        { throw new ArgumentException("The terminal shell requires a literal absolute executable and valid native values."); }
        using NativeUtf8Vector argv = new(new[] { environment.Executable }.Concat(arguments));
        using NativeUtf8Vector values = new(environment.EnvironmentVariables.Where(pair => pair.Value is not null).Select(pair => pair.Key + "=" + pair.Value));
        _ = Replace(environment.Executable, Path.GetFullPath(workingDirectory), argv.Pointer, values.Pointer);
        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot start the selected terminal shell.");
    }

    [DllImport("gitextensions-posix", EntryPoint = "ge_exec_user_shell", SetLastError = true)]
    private static extern int Replace([MarshalAs(UnmanagedType.LPUTF8Str)] string executable,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string directory, IntPtr arguments, IntPtr environment);
}
