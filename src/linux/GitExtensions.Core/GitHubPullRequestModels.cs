namespace GitExtensions.Linux;

public sealed record GitHubRepositoryId(string Owner, string Name)
{
    public override string ToString() => Owner + "/" + Name;
}

public sealed record GitHubHostedRepository(long Id, string Owner, string Name, string Description, bool Private,
    bool Fork, string DefaultBranch, string CloneUrl, string SshUrl, string WebUrl, GitHubRepositoryId? Parent = null)
{
    public GitHubRepositoryId Identity => new(Owner, Name);
    public long? ForksCount { get; init; }
    public string? Homepage { get; init; }
    public bool? CanPush { get; init; }
    public override string ToString() => Identity.ToString();
}

public sealed record GitHubHostedBranch(string Name, string Sha)
{
    public override string ToString() => Name;
}

public sealed record GitHubPullRequestRef(GitHubHostedRepository? Repository, string Ref, string Sha);

public sealed record GitHubPullRequest(int Number, string Title, string Body, string Author, DateTimeOffset CreatedAt,
    string State, string WebUrl, GitHubPullRequestRef Base, GitHubPullRequestRef Head, bool Merged = false, int? CommitCount = null)
{
    public bool IsOpen => State == "open";
    public string FetchBranch => $"pr/n{Number}_{Head.Ref}";
    public override string ToString() => $"#{Number}: {Title}";
}

public sealed record GitHubPullRequestCreate(string Head, string Base, string Title, string Body, bool Draft = false, bool MaintainerCanModify = true)
{
    public override string ToString() => "GitHub pull request draft";
}

public sealed record GitHubPullRequestFile(string Filename, string? PreviousFilename, string Status,
    int Additions, int Deletions, int Changes, string? Patch);

public enum GitHubDiscussionKind { Description, Commit, Comment }

public sealed record GitHubPullRequestDiscussionEntry(GitHubDiscussionKind Kind, string Author, DateTimeOffset CreatedAt,
    string Body, string? WebUrl = null, string? Sha = null);

public sealed class GitHubHostingPage<T>
{
    public GitHubHostingPage(IEnumerable<T> items, bool truncated = false)
    { Items = Array.AsReadOnly(items.ToArray()); Truncated = truncated; }
    public IReadOnlyList<T> Items { get; }
    public bool Truncated { get; }
    public override string ToString() => $"GitHub page ({Items.Count} items, truncated={Truncated})";
}
