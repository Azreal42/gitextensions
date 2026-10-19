namespace GitExtensions.Linux;

/// <summary>Borrowed by Fork/Clone. Its host disposes it only after the dialog has drained its operations.</summary>
public interface IGitHubForkCloneClient : IDisposable
{
    GitHubHostingSettings Settings { get; }
    Task<string> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<GitHubHostedRepository> GetRepositoryAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default);
    Task<GitHubHostingPage<GitHubHostedRepository>> GetRepositoriesAsync(string? owner = null, CancellationToken cancellationToken = default);
    Task<GitHubRepositorySearchResult> SearchRepositoriesAsync(string query, CancellationToken cancellationToken = default);
    /// <summary>A successful response accepts the fork; its Git objects can still be preparing on the server. Never replay this write automatically.</summary>
    Task<GitHubHostedRepository> ForkRepositoryAsync(GitHubRepositoryId repository, string? organization = null, CancellationToken cancellationToken = default);
}
