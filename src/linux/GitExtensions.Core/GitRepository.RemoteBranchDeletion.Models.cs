namespace GitExtensions.Linux;

/// <summary>A fetched branch and the exact server-side branch/hash the user is confirming.</summary>
public sealed record GitRemoteBranchDeletionPreview(GitReference Reference, string Remote, string BranchName,
    bool IsMerged, IReadOnlyList<string> TrackingLocalBranches)
{
    public string ExpectedHash => Reference.Hash;
    public string DestinationRef => "refs/heads/" + BranchName;
    public IReadOnlyList<string> ExpectedPushUrls { get; init; } = [];
}

public sealed record GitDeleteRemoteBranchesOptions(IReadOnlyList<GitRemoteBranchDeletionPreview> Branches, bool AllowUnmerged = false);
public enum GitRemoteBranchDeletionBatchStatus { NotAttempted, Succeeded, Failed, Cancelled }

/// <summary>One atomic server transaction. Cancellation/transport errors can leave its remote outcome unknown.</summary>
public sealed record GitRemoteBranchDeletionBatchResult(string Remote, IReadOnlyList<GitRemoteBranchDeletionPreview> Branches,
    GitRemoteBranchDeletionBatchStatus Status, GitResult? Result = null, string? ErrorMessage = null)
{
    public bool Success => Status == GitRemoteBranchDeletionBatchStatus.Succeeded;
    public string PartialStandardOutput { get; init; } = "";
    public string PartialStandardError { get; init; } = "";
}

/// <summary>Only successful batches may be used to offer deletion of their local tracking branches.</summary>
public sealed record GitRemoteBranchDeletionResult(IReadOnlyList<GitRemoteBranchDeletionBatchResult> Batches, bool Cancelled)
{
    public bool Success => !Cancelled && Batches.Count > 0 && Batches.All(batch => batch.Success);
}

/// <summary>Immutable validated selection, retained while the Desktop executes Before/AfterPush outside Core's mutation lock.</summary>
public sealed class GitRemoteBranchDeletionPlan
{
    internal GitRemoteBranchDeletionPlan(GitRepository repository, bool allowUnmerged, IEnumerable<GitRemoteBranchDeletionPreview> branches)
    {
        Repository = repository; AllowUnmerged = allowUnmerged;
        Branches = Array.AsReadOnly(branches.Select(branch => branch with
        { TrackingLocalBranches = Array.AsReadOnly(branch.TrackingLocalBranches.ToArray()), ExpectedPushUrls = Array.AsReadOnly(branch.ExpectedPushUrls.ToArray()) }).ToArray());
    }
    internal GitRepository Repository { get; }
    internal bool AllowUnmerged { get; }
    public IReadOnlyList<GitRemoteBranchDeletionPreview> Branches { get; }
}
