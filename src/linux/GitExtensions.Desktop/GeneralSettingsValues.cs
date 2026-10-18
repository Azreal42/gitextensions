using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux.Desktop;

/// <summary>The General page's draft; the host owns the existing preference stores.</summary>
internal sealed record GeneralSettingsValues
{
    public bool CheckForUncommittedChangesInCheckoutBranch { get; init; } = true;
    public bool StartWithRecentWorkingDir { get; init; }
    public bool UseHistogramDiffAlgorithm { get; init; }
    public int RevisionGridQuickSearchTimeout { get; init; } = 4000;
    public bool FollowRenamesInFileHistory { get; init; } = true;
    public bool IncludeUntrackedFilesInAutoStash { get; init; }
    public bool? UpdateSubmodulesOnCheckout { get; init; }
    public bool ShowStashCount { get; init; }
    public bool ShowAheadBehindData { get; init; } = true;
    public bool ShowGitStatusInBrowseToolbar { get; init; } = true;
    public bool ShowGitStatusForArtificialCommits { get; init; } = true;
    public bool ShowSubmoduleStatus { get; init; }
    public int MaxRevisionGraphCommits { get; init; } = 100000;
    public bool CloseProcessDialog { get; init; }
    public string DefaultCloneDestinationPath { get; init; } = "";
    public GitPullAction DefaultPullAction { get; init; } = GitPullAction.Merge;
    public bool FollowRenamesInFileHistoryExact { get; init; }
    public bool UseBrowseForFileHistory { get; init; } = true;
}
