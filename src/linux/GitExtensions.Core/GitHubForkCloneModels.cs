namespace GitExtensions.Linux;

public sealed class GitHubRepositorySearchResult
{
    public GitHubRepositorySearchResult(IEnumerable<GitHubHostedRepository> items, long totalCount, bool incompleteResults = false, bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);
        Items = Array.AsReadOnly(items.ToArray()); TotalCount = totalCount;
        IncompleteResults = incompleteResults; Truncated = truncated || incompleteResults || totalCount > Items.Count;
    }
    public IReadOnlyList<GitHubHostedRepository> Items { get; }
    public long TotalCount { get; }
    public bool IncompleteResults { get; }
    public bool Truncated { get; }
    public override string ToString() => $"GitHub repository search ({Items.Count} items, truncated={Truncated})";
}
