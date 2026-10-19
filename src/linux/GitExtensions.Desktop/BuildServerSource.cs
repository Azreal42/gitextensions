namespace GitExtensions.Linux.Desktop;

/// <summary>One polling/report pipeline, with each provider retaining its own transport and status rules.</summary>
internal interface IBuildServerSource : IAsyncDisposable
{
    Task<BuildServerSnapshot> ReadAsync(bool force, CancellationToken token);
    TimeSpan? PreferredPollInterval => null;
}

internal sealed class GitHubActionsBuildSource(GitHubActionsApiClient client) : IBuildServerSource
{
    internal GitHubActionsRunSnapshot? LastSnapshot { get; private set; }
    public async Task<BuildServerSnapshot> ReadAsync(bool force, CancellationToken token)
    {
        GitHubActionsRunSnapshot snapshot = await client.ReadAsync(force, token);
        token.ThrowIfCancellationRequested(); LastSnapshot = snapshot;
        return BuildServerSnapshot.FromGitHubActions(snapshot, client.BuildPageUri);
    }
    public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class TeamCityBuildSource(ITeamCityClient client) : IBuildServerSource
{
    public async Task<BuildServerSnapshot> ReadAsync(bool force, CancellationToken token)
        => BuildServerSnapshot.FromTeamCity(await client.ReadAsync(force, token), client.BuildPageUri);
    public ValueTask DisposeAsync() => client.DisposeAsync();
}

internal sealed class GitLabBuildSource(IGitLabClient client) : IBuildServerSource
{
    public async Task<BuildServerSnapshot> ReadAsync(bool force, CancellationToken token)
        => BuildServerSnapshot.FromGitLab(await client.ReadAsync(force, token), client.BuildPageUri);
    public ValueTask DisposeAsync() => client.DisposeAsync();
}

internal sealed class AppVeyorBuildSource(IAppVeyorClient client) : IBuildServerSource
{
    public TimeSpan? PreferredPollInterval => TimeSpan.FromSeconds(10);
    public async Task<BuildServerSnapshot> ReadAsync(bool force, CancellationToken token)
        => BuildServerSnapshot.FromAppVeyor(await client.ReadAsync(force, token), client.BuildPageUri);
    public ValueTask DisposeAsync() => client.DisposeAsync();
}
