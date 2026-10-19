namespace GitExtensions.Linux;

/// <summary>The two Windows build-server substitutions come from the branch's fetch URL, then the first remote.</summary>
public sealed record BuildServerRepositoryVariables(string Project, string ShortName)
{
    public string Expand(string value)
    {
        if (!string.IsNullOrWhiteSpace(Project)) { value = value.Replace("{cRepoProject}", Project, StringComparison.Ordinal); }
        if (!string.IsNullOrWhiteSpace(ShortName)) { value = value.Replace("{cRepoShortName}", ShortName, StringComparison.Ordinal); }
        return value;
    }
    public static BuildServerRepositoryVariables FromRemoteUrl(string? url)
    {
        string project;
        try { project = System.IO.Path.GetFileNameWithoutExtension(System.IO.Path.GetDirectoryName(url)) ?? ""; }
        catch (ArgumentException) { project = ""; }
        return new(project, System.IO.Path.GetFileNameWithoutExtension(url) ?? "");
    }
}

public sealed partial class GitRepository
{
    public async Task<BuildServerRepositoryVariables> GetBuildServerRepositoryVariablesAsync(CancellationToken cancellationToken = default)
    {
        GitResult branch = await ExecuteAsync(["symbolic-ref", "-q", "--short", "HEAD"], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
        if (!branch.Success && branch.ExitCode != 1) { throw new GitCommandException(["symbolic-ref"], branch); }
        IReadOnlyList<KeyValuePair<string, string>> configuration = await GetConfigAsync("^(branch\\..*\\.remote|remote\\..*\\.url)$", cancellationToken: cancellationToken).ConfigureAwait(false);
        string name = "branch." + branch.StandardOutput.TrimEnd('\n', '\r') + ".remote";
        string? remote = configuration.LastOrDefault(pair => pair.Key == name).Value;
        if (string.IsNullOrWhiteSpace(remote))
        {
            GitResult remotes = await ExecuteAsync(["remote"], cancellationToken).ConfigureAwait(false);
            if (!remotes.Success) { throw new GitCommandException(["remote"], remotes); }
            remote = remotes.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('\r') ?? "";
        }
        string? url = configuration.LastOrDefault(pair => pair.Key == "remote." + remote + ".url").Value;
        return BuildServerRepositoryVariables.FromRemoteUrl(url);
    }
}
