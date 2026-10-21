namespace GitExtensions.Linux;

public sealed record GitHubAssignedIssue(GitHubRepositoryId Repository, int Number, string Title, string Body, DateTimeOffset UpdatedAt);
public sealed record GitHubIssueCommitTemplate(string Name, string Text);

public static class GitHubIssueCommitTemplates
{
    public static IReadOnlyList<GitHubIssueCommitTemplate> Create(IEnumerable<GitHubAssignedIssue> issues,
        IReadOnlyCollection<GitHubRepositoryId> remotes, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(issues); ArgumentNullException.ThrowIfNull(remotes);
        bool multipleRemotes = remotes.Count > 1;
        return Array.AsReadOnly(issues.Where(issue => issue.Number > 0 && remotes.Any(remote =>
                remote.Owner.Equals(issue.Repository.Owner, StringComparison.OrdinalIgnoreCase)
                && remote.Name.Equals(issue.Repository.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(issue => issue.UpdatedAt).Take(Math.Max(0, maxCount))
            .Select(issue => new GitHubIssueCommitTemplate($"{issue.Number}: {issue.Title}"
                + (multipleRemotes ? $" ({issue.Repository.Owner}/{issue.Repository.Name})" : ""),
                $"\nFixes #{issue.Number} : {issue.Title}\n\n{issue.Body}\n")).ToArray());
    }
}
