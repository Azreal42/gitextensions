namespace GitExtensions.Linux.Plugins;

public sealed record AutoCompileSubmodulesOptions(bool Enabled = false, string Executable = "", string Arguments = "/p:Configuration=Debug")
{
    public static AutoCompileSubmodulesOptions FromValues(IReadOnlyDictionary<string, string?> values) => new(
        PluginValues.Boolean(AutoCompileSubmodulesService.Descriptor, values, "enabled"),
        PluginValues.Text(AutoCompileSubmodulesService.Descriptor, values, "executable"),
        PluginValues.Text(AutoCompileSubmodulesService.Descriptor, values, "arguments"));
}

public enum AutoCompileDecision { Build, Skip, Cancel }
public sealed record AutoCompileBuildRequest(string SolutionPath, IReadOnlyList<string> AllSolutions);
public sealed record AutoCompileBuildResult(string SolutionPath, AutoCompileDecision Decision, GitResult? Result = null, string? Error = null);
public sealed record AutoCompileSubmodulesResult(IReadOnlyList<AutoCompileBuildResult> Builds, bool Cancelled = false);

public sealed class AutoCompileSubmodulesService
{
    public static PluginDescriptor Descriptor { get; } = new(new Guid("D4D1ACB7-0B6B-4A3C-B0DB-A25056A277D9"),
        "Auto compile submodules", "Offer to build each solution after a successful submodule update.", "IconAutoCompileSubmodules",
        [new("enabled", "Enabled", PluginParameterKind.Boolean, "false"),
         new("executable", "Path to MSBuild or Linux build executable", PluginParameterKind.Text),
         new("arguments", "Build arguments", PluginParameterKind.Text, "/p:Configuration=Debug",
             Hint: "The solution is the first argument. Use $(SOLUTION) to place it explicitly, e.g. dotnet arguments: build \"$(SOLUTION)\" /p:Configuration=Debug")]);

    private readonly SemaphoreSlim _operation = new(1, 1);

    // This is a post-update hook, not a substitute manual command. The UI host
    // provides its Yes/No/Cancel prompt and displays the returned process output.
    public async Task<AutoCompileSubmodulesResult> AfterSubmoduleUpdateAsync(GitRepository repository, bool actionSucceeded,
        AutoCompileSubmodulesOptions options, Func<AutoCompileBuildRequest, CancellationToken, Task<AutoCompileDecision>> confirmAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(confirmAsync);
        if (!actionSucceeded || !options.Enabled || !Directory.Exists(repository.Path)) { return new([]); }
        cancellationToken.ThrowIfCancellationRequested();
        GitCommandLogProcessContext journalContext = GitCommandLog.CaptureProcessContext();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] arguments = GourceProcess.ParseArguments(options.Arguments);
            EnumerationOptions enumeration = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            string[] solutions = Directory.EnumerateFiles(repository.Path, "*", enumeration)
                .Where(path => string.Equals(Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase))
                .OrderDescending(StringComparer.Ordinal).ToArray();
            List<AutoCompileBuildResult> builds = [];
            foreach (string solution in solutions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AutoCompileDecision decision = await confirmAsync(new(solution, solutions), cancellationToken).ConfigureAwait(false);
                if (!Enum.IsDefined(decision)) { throw new ArgumentOutOfRangeException(nameof(confirmAsync), "Unknown build decision."); }
                if (decision == AutoCompileDecision.Cancel) { builds.Add(new(solution, decision)); return new(builds, Cancelled: true); }
                if (decision == AutoCompileDecision.Skip) { builds.Add(new(solution, decision)); continue; }
                try
                {
                    if (string.IsNullOrWhiteSpace(options.Executable))
                    { throw new ArgumentException("Please enter a build executable in the plugin settings dialog and try again."); }
                    string[] buildArguments = arguments.Any(argument => argument.Contains("$(SOLUTION)", StringComparison.Ordinal))
                        ? arguments.Select(argument => argument.Replace("$(SOLUTION)", solution, StringComparison.Ordinal)).ToArray()
                        : [solution, .. arguments];
                    GitResult result = await GourceProcess.RunAsync(options.Executable, repository.Path, buildArguments, cancellationToken,
                        GitCommandLogProcessKind.Build, journalContext).ConfigureAwait(false);
                    builds.Add(new(solution, decision, result));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { builds.Add(new(solution, decision, Error: exception.Message)); }
            }
            return new(builds);
        }
        finally { _operation.Release(); }
    }
}
