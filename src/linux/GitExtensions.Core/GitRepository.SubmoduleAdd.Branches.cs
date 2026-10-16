namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Lists branch names from the same source that native submodule add would use, without editing the repository.</summary>
    public async Task<IReadOnlyList<string>> GetSubmoduleRemoteBranchesAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0) { return []; }
        ValidateSubmoduleSource(source);
        bool relative = source.StartsWith("./", StringComparison.Ordinal) || source.StartsWith("../", StringComparison.Ordinal);
        if (!relative && !System.IO.Path.IsPathRooted(source) && !source.Contains(':'))
        { throw new ArgumentException("A submodule source must be absolute, contain a transport colon, or begin with ./ or ../, as required by Git submodule add.", nameof(source)); }
        GitRepositoryInfo info = await DiscoverRepositoryAsync(Path, _runner, cancellationToken).ConfigureAwait(false);
        if (info.IsBare) { throw new InvalidOperationException("Adding a submodule requires a working tree."); }
        string resolved = source;
        if (relative)
        {
            string remote = "origin";
            GitResult head = await Read(["symbolic-ref", "--quiet", "HEAD"], [1]).ConfigureAwait(false);
            string? configured = null;
            if (head.Success)
            {
                string name = ReadOutputLine(head.StandardOutput);
                configured = await Scalar("branch." + name["refs/heads/".Length..] + ".remote").ConfigureAwait(false);
            }
            if (configured is not null) { remote = configured; }
            else if (await GetRangeDiffGitVersionAsync(cancellationToken).ConfigureAwait(false) >= new Version(2, 51))
            {
                // Git 2.51 moved the submodule helper to repo_default_remote:
                // it now selects a sole configured remote even when it is not origin.
                string[] remotes = (await Read(["remote"]).ConfigureAwait(false)).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (remotes.Length == 1) { remote = remotes[0]; }
            }
            string? url = await Scalar("remote." + remote + ".url").ConfigureAwait(false);
            resolved = ResolveSubmoduleRelativeUrl(url ?? info.RootPath, source);
        }
        // A clone treats its source as a URL, whereas ls-remote also accepts remote
        // names. A process-only remote preserves literal URL semantics even if a
        // resolved local filename happens to match another configured remote.
        string query = "gitextensions-submodule-query-" + Guid.NewGuid().ToString("N");
        GitResult result = await Read(["-c", "remote." + query + ".url=" + resolved, "ls-remote", "--heads", "--", query]).ConfigureAwait(false);
        List<string> branches = [];
        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = line.IndexOf('\t');
            if (tab >= 0 && line.AsSpan(tab + 1).StartsWith("refs/heads/", StringComparison.Ordinal))
            { branches.Add(line[(tab + 1 + "refs/heads/".Length)..]); }
        }
        return branches.AsReadOnly();

        async Task<GitResult> Read(IReadOnlyList<string> arguments, int[]? allowed = null)
        {
            GitResult value = await _runner.RunAsync(info.RootPath, [.. GlobalOptions, .. arguments], cancellationToken).ConfigureAwait(false);
            if (!value.Success && !(allowed?.Contains(value.ExitCode) ?? false)) { throw new GitCommandException(arguments, value); }
            return value;
        }
        async Task<string?> Scalar(string key)
        {
            GitResult value = await Read(["config", "--null", "--get", key], [1]).ConfigureAwait(false);
            return value.Success ? value.StandardOutput.EndsWith('\0') ? value.StandardOutput[..^1] : value.StandardOutput : null;
        }
    }

    private static string ResolveSubmoduleRelativeUrl(string remote, string source)
    {
        if (remote.Length == 0) { throw new ArgumentException("The default remote has an empty URL. Set its URL before requesting submodule branches.", nameof(source)); }
        if (remote.EndsWith('/')) { remote = remote[..^1]; }
        bool relativeBase = !System.IO.Path.IsPathRooted(remote) && !remote.Contains(':');
        if (relativeBase && !remote.StartsWith("./", StringComparison.Ordinal) && !remote.StartsWith("../", StringComparison.Ordinal)) { remote = "./" + remote; }
        bool colon = false;
        while (source.StartsWith("./", StringComparison.Ordinal) || source.StartsWith("../", StringComparison.Ordinal))
        {
            if (source.StartsWith("./", StringComparison.Ordinal)) { source = source[2..]; continue; }
            source = source[3..];
            int separator = remote.LastIndexOf('/');
            if (separator < 0) { separator = remote.LastIndexOf(':'); colon |= separator >= 0; }
            if (separator >= 0) { remote = remote[..separator]; }
            else if (relativeBase || remote == ".") { throw new ArgumentException("The relative submodule source cannot be resolved against the repository remote.", nameof(source)); }
            else { remote = "."; }
        }
        // Keep the raw base URL until after combining it with the relative source.
        // ls-remote then applies Git's native insteadOf rewriting to the combined URL.
        string combined = remote + (colon ? ":" : "/") + source;
        if (source.EndsWith('/')) { combined = combined[..^1]; }
        return combined.StartsWith("./", StringComparison.Ordinal) ? combined[2..] : combined;
    }
}
