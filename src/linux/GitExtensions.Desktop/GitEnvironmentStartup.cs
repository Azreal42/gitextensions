using System.ComponentModel;

namespace GitExtensions.Linux.Desktop;

internal sealed record GitEnvironmentStartup(DesktopStartupArguments Arguments, bool CheckAtStartup,
    Exception? PreparationError = null, string? CurrentDirectory = null)
{
    internal static bool IsExecutableUnavailable(Exception? error) => error is Win32Exception
        || error is GitCommandException command && command.Result.ExitCode is 126 or 127;
}
