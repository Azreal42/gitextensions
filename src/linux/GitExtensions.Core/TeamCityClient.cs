namespace GitExtensions.Linux;

/// <summary>Borrowed by chooser/runtime consumers; the owning host cancels and drains DisposeAsync.</summary>
public interface ITeamCityClient : IDisposable, IAsyncDisposable
{
    Task<TeamCityPage<TeamCityProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task<TeamCityPage<TeamCityBuildType>> GetProjectBuildTypesAsync(string projectId, CancellationToken cancellationToken = default);
    Task<TeamCityBuildType> GetBuildTypeAsync(string buildTypeId, CancellationToken cancellationToken = default);
    Task<TeamCityBuildSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default);
    Uri? BuildPageUri(TeamCityBuild build);
}
