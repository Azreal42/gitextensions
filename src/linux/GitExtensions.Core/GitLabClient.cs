namespace GitExtensions.Linux;

public interface IGitLabClient : IDisposable, IAsyncDisposable
{
    Task<GitLabSnapshot> ReadAsync(bool force = false, CancellationToken cancellationToken = default);
    Task<GitLabProject> GetProjectAsync(string projectNamespace, string projectName, CancellationToken cancellationToken = default);
    Uri? BuildPageUri(GitLabPipeline pipeline);
}
