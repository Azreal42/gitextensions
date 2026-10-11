namespace GitExtensions.Linux.Plugins;

public static class GourceExecutable
{
    public static string? Resolve(string executable, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0')) return null;
        IEnumerable<string> candidates = executable.Contains(Path.DirectorySeparatorChar)
            ? [Path.GetFullPath(executable, workingDirectory)]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(directory => Path.Combine(string.IsNullOrEmpty(directory) ? workingDirectory : Path.GetFullPath(directory, workingDirectory), executable));
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                if (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate)
                    & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) return candidate;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
