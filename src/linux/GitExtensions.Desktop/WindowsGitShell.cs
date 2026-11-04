using System.Diagnostics;

namespace GitExtensions.Linux.Desktop;

/// <summary>Resolve the shell belonging to the captured Git installation, rather than a WSL bash on ambient PATH.</summary>
internal static class WindowsGitShell
{
    internal static ProcessStartInfo CreateStartInfo(string repositoryPath, GitExecutionEnvironment environment, bool nativeConsole = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath); ArgumentNullException.ThrowIfNull(environment);
        string? shell = FindShell(environment, nativeConsole);
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

    private static string? FindShell(GitExecutionEnvironment environment, bool nativeConsole)
    {
        if (environment.Options.ToolsDirectory is { Length: > 0 } tools && FindIn(tools, nativeConsole) is { } explicitShell) { return explicitShell; }
        string git = environment.Executable;
        if (!Path.IsPathFullyQualified(git) && !git.Contains(Path.DirectorySeparatorChar) && !git.Contains(Path.AltDirectorySeparatorChar))
        { git = ConsoleStyleTerminal.Find(Path.HasExtension(git) ? git : git + ".exe", environment.GetVariable("PATH")) ?? ""; }
        if (git.Length > 0)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(git))!;
            string leaf = Path.GetFileName(directory);
            if (leaf.Equals("cmd", StringComparison.OrdinalIgnoreCase) || leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                directory = Path.GetDirectoryName(directory) ?? directory;
                if (Path.GetFileName(directory).Equals("mingw64", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(directory).Equals("mingw32", StringComparison.OrdinalIgnoreCase))
                { directory = Path.GetDirectoryName(directory) ?? directory; }
            }
            foreach (string root in new[] { directory, Path.Combine(directory, "usr", "bin"), Path.Combine(directory, "bin") })
            { if (FindIn(root, nativeConsole) is { } installed) { return installed; } }
        }
        return nativeConsole ? null : ConsoleStyleTerminal.Find("git-bash.exe", environment.GetVariable("PATH"));
    }

    private static string? FindIn(string directory, bool nativeConsole)
        => (nativeConsole ? new[] { "bash.exe", "sh.exe" } : ["git-bash.exe", "bash.exe", "sh.exe"])
            .Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
}
