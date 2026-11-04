using System.ComponentModel;

namespace GitExtensions.Linux;

/// <summary>Shared Git-for-Windows shell discovery for interactive Tools and explicit script shell aliases.</summary>
public static class WindowsGitShellResolver
{
    public static string? FindInteractiveShell(GitExecutionEnvironment environment, bool nativeConsole = false)
        => Find(environment, nativeConsole ? ["bash.exe", "sh.exe"] : ["git-bash.exe", "bash.exe", "sh.exe"], launcherFallback: !nativeConsole);

    /// <summary>Only bare bash/sh aliases use Git's tools. Explicit paths and unrelated commands remain literal.</summary>
    public static string ResolveScriptCommand(string executable, GitExecutionEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable); ArgumentNullException.ThrowIfNull(environment);
        string shell = executable.ToLowerInvariant();
        if (shell is not ("bash" or "bash.exe" or "sh" or "sh.exe")) { return executable; }
        string name = shell.EndsWith(".exe", StringComparison.Ordinal) ? shell : shell + ".exe";
        return Find(environment, [name], launcherFallback: false)
            ?? throw new FileNotFoundException("The requested shell is unavailable in the configured Git installation. Choose its tools directory or an explicit script executable.", executable);
    }

    private static string? Find(GitExecutionEnvironment environment, IReadOnlyList<string> names, bool launcherFallback)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.Options.ToolsDirectory is { Length: > 0 } tools && FindIn(tools, names) is { } explicitShell) { return explicitShell; }
        string? directory = GitDirectory(environment);
        if (directory is not null)
        {
            foreach (string root in new[] { directory, Path.Combine(directory, "usr", "bin"), Path.Combine(directory, "bin") })
            { if (FindIn(root, names) is { } installed) { return installed; } }
        }
        if (launcherFallback)
        {
            try { return GitConfiguredExecutable.Resolve("git-bash.exe", environment.GetVariable("PATH"), windows: true, includeProcessDirectories: false); }
            catch (Win32Exception) { }
        }
        return null;
    }

    private static string? GitDirectory(GitExecutionEnvironment environment)
    {
        string executable;
        try { executable = GitConfiguredExecutable.Resolve(environment.Executable, environment.GetVariable("PATH"), windows: true); }
        catch (Win32Exception) { return null; }
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        string leaf = Path.GetFileName(directory);
        if (leaf.Equals("cmd", StringComparison.OrdinalIgnoreCase) || leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            directory = Path.GetDirectoryName(directory) ?? directory;
            if (new[] { "mingw64", "mingw32", "usr" }.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
            { directory = Path.GetDirectoryName(directory) ?? directory; }
        }
        return directory;
    }

    private static string? FindIn(string directory, IReadOnlyList<string> names)
        => names.Select(name => Path.GetFullPath(Path.Combine(directory, name))).FirstOrDefault(File.Exists);
}
