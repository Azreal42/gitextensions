namespace GitExtensions.Linux.Desktop;

/// <summary>A selected repository keeps its Git context; a global-only editor never discovers an unrelated cwd repository.</summary>
internal sealed class GitConfigurationContext : IDisposable
{
    private readonly string? _temporary;
    internal GitRepository Repository { get; }
    internal GitExecutionEnvironment Environment { get; }
    internal bool GlobalOnly { get; }

    private GitConfigurationContext(GitRepository repository, GitExecutionEnvironment environment, string? temporary)
    { Repository = repository; Environment = environment; _temporary = temporary; GlobalOnly = temporary is not null; }

    internal static GitConfigurationContext Create(GitRepository? selected, GitExecutionEnvironment environment,
        string? invokingDirectory = null)
    {
        if (selected is not null) { return new(new(selected.Path, environment.CreateRunner()), environment, null); }
        string origin = Path.GetFullPath(invokingDirectory ?? System.Environment.CurrentDirectory);
        string temporary = Directory.CreateTempSubdirectory("gitextensions-global-config-").FullName;
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        try
        {
            Dictionary<string, string?> values = new(environment.EnvironmentVariables, StringComparer.Ordinal);
            foreach (string key in new[] { "GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_PREFIX" })
            { values.Remove(key); }
            // Preserve the interpretation of inherited relative locations before entering the private no-repository cwd.
            foreach (string key in new[] { "HOME", "XDG_CONFIG_HOME", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM" })
            {
                if (values.GetValueOrDefault(key) is { Length: > 0 } value && !Path.IsPathFullyQualified(value))
                { values[key] = Path.GetFullPath(value, origin); }
            }
            if (values.GetValueOrDefault("PATH") is string path)
            {
                values["PATH"] = string.Join(Path.PathSeparator, path.Split(Path.PathSeparator).Select(directory =>
                    Path.IsPathFullyQualified(directory) ? directory : Path.GetFullPath(directory.Length == 0 ? "." : directory, origin)));
            }
            string executable = environment.Executable;
            if (!Path.IsPathFullyQualified(executable) && executable.Contains(Path.DirectorySeparatorChar))
            { executable = Path.GetFullPath(executable, origin); }
            values["GIT_CEILING_DIRECTORIES"] = temporary;
            GitExecutionEnvironment isolated = GitExecutionEnvironment.Capture(environment.Options with { Executable = executable, HomeDirectory = null, ToolsDirectory = null }, values);
            return new(new(temporary, isolated.CreateRunner()), isolated, temporary);
        }
        catch { Directory.Delete(temporary); throw; }
    }

    public void Dispose()
    {
        if (_temporary is not null && Directory.Exists(_temporary)) { Directory.Delete(_temporary, recursive: true); }
    }
}
