using GitCommands.ExternalLinks;
using GitCommands.Remotes;

namespace GitExtensions.Linux;

public enum GitRevisionLinkTemplate { GitHub, AzureDevOps }

/// <summary>Portable form of the Windows cloud definition extractors. No network or remote mutations.</summary>
public static class GitRevisionLinkTemplates
{
    public static ExternalLinkDefinition CreateManual() => new()
    {
        Name = "<new>", Enabled = true, UseRemotesPattern = "upstream|origin", UseOnlyFirstRemote = true,
        SearchInParts = { ExternalLinkDefinition.RevisionPart.Message },
        RemoteSearchInParts = { ExternalLinkDefinition.RemotePart.URL }
    };

    public static IReadOnlyList<ExternalLinkDefinition> Create(GitRevisionLinkTemplate template,
        IReadOnlyList<GitRevisionLinkRemote> remotes, Func<string, string>? translate = null)
    {
        Func<string, string> t = translate ?? (source => source);
        GitHubRemoteParser github = new(); AzureDevOpsRemoteParser azure = new();
        bool Valid(string url) => template == GitRevisionLinkTemplate.GitHub ? github.IsValidRemoteUrl(url) : azure.IsValidRemoteUrl(url);
        GitRevisionLinkRemote[] candidates = remotes.Where(remote => !string.IsNullOrWhiteSpace(remote.Url) && Valid(remote.Url!)).ToArray();
        string? url = candidates.FirstOrDefault(remote => remote.Name == "upstream")?.Url
            ?? candidates.FirstOrDefault(remote => remote.Name == "fork")?.Url
            ?? candidates.FirstOrDefault(remote => remote.Name == "origin")?.Url ?? candidates.FirstOrDefault()?.Url;
        string? owner = null, repository = null;
        if (url is not null)
        {
            if (template == GitRevisionLinkTemplate.GitHub) { github.TryExtractGitHubDataFromRemoteUrl(url, out owner, out repository); }
            else { azure.TryExtractAzureDevopsDataFromRemoteUrl(url, out owner, out _, out repository); }
        }
        bool isGitHub = template == GitRevisionLinkTemplate.GitHub;
        string service = isGitHub ? "GitHub" : "Azure DevOps";
        owner ??= isGitHub ? "ORGANIZATION_NAME" : "ACCOUNT_NAME"; repository ??= "REPO_NAME";
        string root = isGitHub ? $"https://github.com/{owner}/{repository}" : $"https://dev.azure.com/{owner}";
        string Caption(string source) => string.Format(t(source), service);
        ExternalLinkDefinition code = new()
        {
            Name = Caption("{0} - Code"), Enabled = true, SearchPattern = ".*",
            SearchInParts = { ExternalLinkDefinition.RevisionPart.Message },
            LinkFormats =
            {
                new() { Caption = Caption("View commit in {0}"), Format = isGitHub ? root + "/commit/%COMMIT_HASH%" : $"{root}/_git/{repository}/commit/%COMMIT_HASH%" },
                new() { Caption = Caption("View project in {0}"), Format = isGitHub ? root : $"{root}/{repository}" }
            }
        };
        ExternalLinkDefinition issues = new()
        {
            Name = Caption("{0} - Issues"), Enabled = true,
            SearchPattern = isGitHub ? @"(?i)(?<!pull request |pr[ _]?)(#|(((feat(ure)?)|fix)[/_-]))\d+" : @"#(\d+)",
            NestedSearchPattern = isGitHub ? @"\d+" : null,
            SearchInParts = { ExternalLinkDefinition.RevisionPart.Message },
            LinkFormats = { new() { Caption = "#{0}", Format = isGitHub ? root + "/issues/{0}" : $"{root}/{repository}/_workitems/edit/{{0}}" } }
        };
        if (!isGitHub) { return [code, issues]; }
        issues.SearchInParts.Add(ExternalLinkDefinition.RevisionPart.LocalBranches);
        return [code, issues, new()
        {
            Name = Caption("{0} - Pull Requests"), Enabled = true, SearchPattern = @"(?i)(pull request |pr[ _]?)#?\d+", NestedSearchPattern = @"\d+",
            SearchInParts = { ExternalLinkDefinition.RevisionPart.Message, ExternalLinkDefinition.RevisionPart.LocalBranches, ExternalLinkDefinition.RevisionPart.RemoteBranches },
            LinkFormats = { new() { Caption = "PR #{0}", Format = root + "/pull/{0}" } }
        }];
    }
}
