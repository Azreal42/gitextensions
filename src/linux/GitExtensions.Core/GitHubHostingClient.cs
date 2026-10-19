namespace GitExtensions.Linux;

/// <summary>Borrowed by dialogs; its owning host cancels/drains their work before disposing it.</summary>
public interface IGitHubHostingClient : IDisposable
{
    GitHubHostingSettings Settings { get; }
    Task<string> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<GitHubHostedRepository> GetRepositoryAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default);
    Task<GitHubHostingPage<GitHubHostedRepository>> GetRepositoriesAsync(string? owner = null, CancellationToken cancellationToken = default);
    Task<GitHubHostingPage<GitHubHostedRepository>> GetForksAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default);
    Task<GitHubHostingPage<GitHubHostedBranch>> GetBranchesAsync(GitHubRepositoryId repository, CancellationToken cancellationToken = default);
    Task<GitHubHostingPage<GitHubPullRequest>> GetPullRequestsAsync(GitHubRepositoryId repository, string state = "open", CancellationToken cancellationToken = default);
    Task<GitHubPullRequest> GetPullRequestAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default);
    Task<GitHubHostingPage<GitHubPullRequestFile>> GetPullRequestFilesAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default);
    Task<string> GetPullRequestDiffAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default);
    Task<GitHubHostingPage<GitHubPullRequestDiscussionEntry>> GetDiscussionAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default);
    Task<GitHubPullRequest> CreatePullRequestAsync(GitHubRepositoryId repository, GitHubPullRequestCreate draft, CancellationToken cancellationToken = default);
    Task<GitHubPullRequestDiscussionEntry> PostCommentAsync(GitHubRepositoryId repository, int number, string body, CancellationToken cancellationToken = default);
    Task<GitHubPullRequest> ClosePullRequestAsync(GitHubRepositoryId repository, int number, CancellationToken cancellationToken = default);
}
