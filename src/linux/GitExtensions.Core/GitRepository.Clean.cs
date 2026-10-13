using System.ComponentModel;
using System.Globalization;
using GitCommands;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<GitCleanResult> PreviewCleanAsync(GitCleanOptions? options = null, CancellationToken cancellationToken = default)
        => CleanWorkingDirectoryCoreAsync(SnapshotCleanOptions(options ?? new()), preview: true, cancellationToken);

    /// <summary>The host confirms destructive cleanup. No preview is required and no prior preview list is used as a deletion authority.</summary>
    public Task<GitCleanResult> CleanWorkingDirectoryAsync(GitCleanOptions? options = null, CancellationToken cancellationToken = default)
        => CleanWorkingDirectoryCoreAsync(SnapshotCleanOptions(options ?? new()), preview: false, cancellationToken);

    private Task<GitCleanResult> CleanWorkingDirectoryCoreAsync(GitCleanOptions options, bool preview, CancellationToken cancellationToken)
        => WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            List<GitCleanCommandResult> commands = [];
            List<string> parent = CleanArguments(options, preview);
            parent.AddRange(options.ExcludePaths.Select(pattern => "--exclude=" + pattern.Replace(' ', '?').Replace(System.IO.Path.DirectorySeparatorChar, '/')));
            parent.Add("--");
            parent.AddRange(options.IncludePaths);
            // Conservative: a mutation-capable invocation has been handed to the runner, even if launch/output later fails.
            bool mutationStarted = !preview;
            GitCleanCommandResult first = await RunCleanCommandAsync(GitCleanCommandScope.Worktree, parent, token).ConfigureAwait(false);
            commands.Add(first);
            bool cancelled = first.Cancelled || token.IsCancellationRequested;
            if (options.CleanSubmodules && !cancelled)
            {
                // FormCleanupRepository invokes this second command even when the parent Git command failed.
                // Native foreach retains its own recursion, visitation order, cwd and stop-on-child-error behavior.
                List<string> child = CleanArguments(options, preview);
                child.Add("--");
                string script = "git --no-pager -c color.ui=false -c core.quotepath=false " + string.Join(' ', child);
                Dictionary<string, string?> environment = new(StringComparer.Ordinal);
                for (int index = 0; index < options.IncludePaths.Count; index++)
                {
                    string variable = "GITEXTENSIONS_CLEAN_INCLUDE_" + index.ToString(CultureInfo.InvariantCulture);
                    environment.Add(variable, options.IncludePaths[index]);
                    // Values never enter this script. Quoted expansion is one argv item, with no eval or shell globbing.
                    script += " \"${" + variable + "}\"";
                }
                GitCleanCommandResult recursive = await RunCleanCommandAsync(GitCleanCommandScope.Submodules,
                    ["submodule", "foreach", "--recursive", script], token, environment).ConfigureAwait(false);
                commands.Add(recursive);
                cancelled = recursive.Cancelled || token.IsCancellationRequested;
            }
            return new GitCleanResult(preview, commands.ToArray(), mutationStarted, cancelled);
        }, cancellationToken);

    private async Task<GitCleanCommandResult> RunCleanCommandAsync(GitCleanCommandScope scope, IReadOnlyList<string> arguments,
        CancellationToken token, IReadOnlyDictionary<string, string?>? environment = null)
    {
        string[] capturedArguments = arguments.ToArray();
        try
        {
            GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, .. capturedArguments], token, environment: environment).ConfigureAwait(false);
            return new(scope, capturedArguments, result, false, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(scope, capturedArguments, null, true, null); }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        { return new(scope, capturedArguments, null, false, error.Message); }
    }

    private static GitCleanOptions SnapshotCleanOptions(GitCleanOptions options)
    {
        if (!Enum.IsDefined(options.Mode)) { throw new ArgumentOutOfRangeException(nameof(options)); }
        return options with { IncludePaths = Lines(options.IncludePaths), ExcludePaths = Lines(options.ExcludePaths) };

        static string[] Lines(IReadOnlyList<string> source)
        {
            ArgumentNullException.ThrowIfNull(source);
            List<string> lines = [];
            foreach (string line in source)
            {
                ArgumentNullException.ThrowIfNull(line);
                if (line.Contains('\0')) { throw new ArgumentException("Cleanup paths cannot contain NUL.", nameof(source)); }
                if (line.Length != 0) { lines.Add(line); }
            }
            return lines.ToArray();
        }
    }

    private static List<string> CleanArguments(GitCleanOptions options, bool preview)
    {
        List<string> arguments = ["clean"];
        switch (options.Mode)
        {
            case CleanMode.All: arguments.Add("-x"); break;
            case CleanMode.OnlyIgnored: arguments.Add("-X"); break;
            case CleanMode.OnlyNonIgnored: break;
            default: throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.RemoveDirectories) { arguments.Add("-d"); }
        arguments.Add(preview ? "--dry-run" : "-f");
        return arguments;
    }
}
