using System.Text.RegularExpressions;

namespace GitExtensions.Linux;

public sealed record GitExecutableDiagnostic(string Executable, GitResult? Result, string? Error)
{
    public bool Success => Error is null && Result?.Success == true;
}

public enum GitSshCommandSource { Path, GitSsh, CoreSshCommand, GitSshCommand, Unavailable }

public sealed record GitSshConfiguration(GitSshCommandSource Source, string? Error = null)
{
    public bool HasCommandOverride => Source is GitSshCommandSource.CoreSshCommand or GitSshCommandSource.GitSshCommand;
}

public sealed record GitEnvironmentDiagnostic(GitExecutableDiagnostic Git, GitExecutableDiagnostic Shell,
    GitExecutableDiagnostic Ssh, Version? Version, string? HomeDirectory, string? GlobalConfigOverride,
    IReadOnlyList<string> GlobalConfigCandidates)
{
    public GitSshConfiguration? SshConfiguration { get; init; }
}

/// <summary>Local, native probes. A discovered pathname is never reported as a successful execution.</summary>
public static class GitEnvironmentDiagnostics
{
    public static Version MinimumVersion { get; } = new(2, 43, 0);
    public static Version RecommendedVersion { get; } = new(2, 53, 0);

    public static async Task<GitEnvironmentDiagnostic> ProbeAsync(GitExecutionEnvironment environment,
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        string directory = Path.GetFullPath(workingDirectory);
        GitExecutableDiagnostic git = await Probe(environment.Executable, ["--version"], gitCommand: true).ConfigureAwait(false);
        // Closing stdin lets sh parse EOF and exit without executing command text; ssh -V never connects.
        GitExecutableDiagnostic shell = await Probe("sh", [], gitCommand: false).ConfigureAwait(false);
        // Never interpret a command string as a local diagnostic. A chosen literal
        // executable may be tested, but a higher-priority command remains unverified.
        string sshExecutable = environment.GetVariable("GIT_SSH") ?? "ssh";
        GitExecutableDiagnostic ssh = await Probe(sshExecutable, ["-V"], gitCommand: false).ConfigureAwait(false);
        Match match = Regex.Match(git.Result?.StandardOutput ?? "", @"\Agit version (\d+)\.(\d+)(?:\.(\d+))?");
        Version? version = null;
        if (git.Success)
        {
            string text = match.Groups[1].Value + "." + match.Groups[2].Value + "." + (match.Groups[3].Success ? match.Groups[3].Value : "0");
            if (!match.Success || !System.Version.TryParse(text, out version))
            { git = git with { Error = "The configured executable did not report a valid Git version." }; }
        }
        return new(git, shell, ssh, version, environment.HomeDirectory, environment.GlobalConfigOverride,
            environment.GetGlobalConfigCandidates(directory)) { SshConfiguration = await ObserveSshAsync().ConfigureAwait(false) };

        async Task<GitSshConfiguration> ObserveSshAsync()
        {
            if (environment.EnvironmentVariables.ContainsKey("GIT_SSH_COMMAND")) { return new(GitSshCommandSource.GitSshCommand); }
            try
            {
                GitResult configured = await environment.CreateRunner().RunAsync(directory, ["config", "--get", "core.sshCommand"], cancellationToken).ConfigureAwait(false);
                if (configured.Success) { return new(GitSshCommandSource.CoreSshCommand); }
                if (configured.ExitCode != 1) { return new(GitSshCommandSource.Unavailable, configured.StandardError); }
                return new(environment.EnvironmentVariables.ContainsKey("GIT_SSH") ? GitSshCommandSource.GitSsh : GitSshCommandSource.Path);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new(GitSshCommandSource.Unavailable, error.Message); }
        }

        async Task<GitExecutableDiagnostic> Probe(string executable, IReadOnlyList<string> arguments, bool gitCommand)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                GitRunner runner = new(environment.WithExecutable(executable));
                GitResult result;
                if (gitCommand) { result = await runner.RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false); }
                else
                {
                    GitBinaryResult native = await runner.RunExternalBinaryAsync(directory, arguments, cancellationToken, null, null, null).ConfigureAwait(false);
                    GitOutputDecoding decoded = GitOutputDecoding.Utf8(native);
                    result = new(native.ExitCode, decoded.StandardOutput, decoded.StandardError)
                    { StandardOutputBytes = native.StandardOutput, StandardErrorBytes = native.StandardErrorBytes, Warnings = decoded.Warnings };
                }
                return new(executable, result, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { return new(executable, null, exception.Message); }
        }
    }
}
