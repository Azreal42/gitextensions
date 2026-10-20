using System.Collections.ObjectModel;
using System.Diagnostics;

namespace GitExtensions.Linux;

/// <summary>Application choices, applied only to child processes. Empty paths retain the native environment.</summary>
public sealed record GitEnvironmentOptions
{
    public string Executable { get; init; } = "git";
    public string? ToolsDirectory { get; init; }
    public string? HomeDirectory { get; init; }
    public string SshExecutable { get; init; } = "";
}

/// <summary>An immutable execution context shared by a command, its probes, and an entire owned operation.</summary>
public sealed class GitExecutionEnvironment
{
    private GitExecutionEnvironment(GitEnvironmentOptions options, Dictionary<string, string?> values)
    { Options = options; EnvironmentVariables = new ReadOnlyDictionary<string, string?>(values); }

    public GitEnvironmentOptions Options { get; }
    public string Executable => Options.Executable;
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }
    public string? GetVariable(string name) => EnvironmentVariables.GetValueOrDefault(name);
    public string? HomeDirectory => GetVariable("HOME");
    public string? GlobalConfigOverride => GetVariable("GIT_CONFIG_GLOBAL");

    public static GitExecutionEnvironment Capture(GitEnvironmentOptions options,
        IReadOnlyDictionary<string, string?>? inheritedEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        string executable = string.IsNullOrEmpty(options.Executable) ? "git" : options.Executable;
        foreach (string? value in new[] { executable, options.ToolsDirectory, options.HomeDirectory, options.SshExecutable })
        { if (value?.Contains('\0') == true) { throw new ArgumentException("Git environment paths cannot contain NUL.", nameof(options)); } }
        Dictionary<string, string?> values = inheritedEnvironment is null
            ? Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal)
            : new(inheritedEnvironment, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(options.ToolsDirectory))
        {
            string path = values.GetValueOrDefault("PATH") ?? "";
            if (!path.Split(Path.PathSeparator).Contains(options.ToolsDirectory, StringComparer.Ordinal))
            { values["PATH"] = path.Length == 0 ? options.ToolsDirectory : path + Path.PathSeparator + options.ToolsDirectory; }
        }
        foreach (string name in values.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray()) { values.Remove(name); }
        if (options.HomeDirectory is not null) { values["HOME"] = options.HomeDirectory; }
        // GIT_SSH is a literal executable. Git retains authority over GIT_SSH_COMMAND,
        // core.sshCommand, ssh.variant, the inherited agent and per-call overrides.
        if (!string.IsNullOrEmpty(options.SshExecutable)) { values["GIT_SSH"] = options.SshExecutable; }
        return new(options with { Executable = executable }, values);
    }

    public void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment.Clear();
        foreach ((string name, string? value) in EnvironmentVariables)
        { if (value is not null) { startInfo.Environment[name] = value; } }
    }
    public GitRunner CreateRunner() => new(this);
    internal GitExecutionEnvironment WithExecutable(string executable)
        => Capture(new GitEnvironmentOptions { Executable = executable }, EnvironmentVariables);

    public IReadOnlyList<string> GetGlobalConfigCandidates(string workingDirectory)
    {
        if (EnvironmentVariables.ContainsKey("GIT_CONFIG_GLOBAL"))
        { return string.IsNullOrEmpty(GlobalConfigOverride) ? [] : [Path.GetFullPath(GlobalConfigOverride, workingDirectory)]; }
        string? home = HomeDirectory;
        if (string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home)) { return []; }
        string xdg = GetVariable("XDG_CONFIG_HOME") is { Length: > 0 } configured ? Path.GetFullPath(configured, workingDirectory) : Path.Combine(home, ".config");
        return [Path.Combine(home, ".gitconfig"), Path.Combine(xdg, "git", "config")];
    }
}

/// <summary>Publishes immutable options; the ambient process environment is captured at each operation boundary.</summary>
public static class GitEnvironment
{
    internal const string InheritedExecutableVariable = "GITEXTENSIONS_GIT_EXECUTABLE";
    private static GitEnvironmentOptions _options = new();
    private static readonly AsyncLocal<GitExecutionEnvironment?> Operation = new();
    public static GitEnvironmentOptions Options => Volatile.Read(ref _options);
    public static GitExecutionEnvironment Current => Operation.Value ?? GitExecutionEnvironment.Capture(Options);
    public static void Configure(GitEnvironmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = GitExecutionEnvironment.Capture(options);
        Volatile.Write(ref _options, options with { });
    }
    public static IDisposable BeginOperation()
    {
        GitExecutionEnvironment? previous = Operation.Value;
        Operation.Value ??= GitExecutionEnvironment.Capture(Options);
        return new GitEnvironmentOperation(() => Operation.Value = previous);
    }
}

internal sealed class GitEnvironmentOperation(Action restore) : IDisposable
{
    private Action? _restore = restore;
    public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
}
