using System.Diagnostics;

namespace GitExtensions.Linux.Desktop;

/// <summary>Resolve the shell belonging to the captured Git installation, rather than a WSL bash on ambient PATH.</summary>
internal static class WindowsGitShell
{
    internal static ProcessStartInfo CreateStartInfo(string repositoryPath, GitExecutionEnvironment environment, bool nativeConsole = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath); ArgumentNullException.ThrowIfNull(environment);
        string? shell = WindowsGitShellResolver.FindInteractiveShell(environment, nativeConsole);
        if (shell is null) { throw new FileNotFoundException("Git Bash is unavailable. Install Git for Windows or choose its tools directory in Git settings."); }
        ProcessStartInfo start = new(Path.GetFullPath(shell)) { UseShellExecute = false, WorkingDirectory = Path.GetFullPath(repositoryPath) };
        environment.ApplyTo(start);
        if (Path.GetFileName(shell).Equals("git-bash.exe", StringComparison.OrdinalIgnoreCase))
        { start.ArgumentList.Add("--cd=" + start.WorkingDirectory); }
        else
        {
            start.Environment["CHERE_INVOKING"] = "1";
            start.ArgumentList.Add("--login"); start.ArgumentList.Add("-i");
        }
        return start;
    }

}
