using System.Globalization;

namespace GitExtensions.Linux.Plugins;

public sealed record DeleteUnusedBranchCandidate(GitReference Reference, DateTimeOffset Date, string Author, string Subject,
    string? Reason, bool Suggested)
{
    public bool Eligible => Reason is null;
    internal PluginRow Row(string? status = null, string? reason = null)
        => new(Reference.Name, [Reference.ShortName, Reference.IsRemote ? "Remote" : "Local", Date.ToString("O", CultureInfo.InvariantCulture),
            Author, Subject, status ?? (Reason is not null ? "Excluded" : Suggested ? "Suggested" : "Eligible"),
            reason ?? Reason ?? (Suggested ? "Older than the configured age." : "Eligible; not preselected by age."), Reference.Hash]);
}

public sealed class DeleteUnusedBranchesPreview
{
    internal DeleteUnusedBranchesPreview(string rule, IEnumerable<DeleteUnusedBranchCandidate> candidates, string remote, bool includeUnmerged)
    { Rule = rule; Candidates = Array.AsReadOnly(candidates.ToArray()); Remote = remote; IncludeUnmerged = includeUnmerged; }
    public string Rule { get; }
    public IReadOnlyList<DeleteUnusedBranchCandidate> Candidates { get; }
    public string Remote { get; }
    public bool IncludeUnmerged { get; }
    public PluginResult ToPluginResult() => new(Rule + "\nPreview only; no branches were changed.", Table(Candidates.Select(item => item.Row()).ToArray()));
    internal static PluginTable Table(IReadOnlyList<PluginRow> rows)
        => new(["Branch", "Scope", "Tip date", "Author", "Subject", "Status", "Reason", "Tip"], rows);
}

public enum DeleteUnusedBranchStatus { Deleted, Failed, Interrupted, NotAttempted }
public sealed record DeleteUnusedBranchOutcome(DeleteUnusedBranchCandidate Branch, DeleteUnusedBranchStatus Status, string Message);

public sealed class DeleteUnusedBranchesDeletionResult
{
    internal DeleteUnusedBranchesDeletionResult(DeleteUnusedBranchesPreview preview, IEnumerable<DeleteUnusedBranchOutcome> outcomes, bool cancelled)
    { Preview = preview; Outcomes = Array.AsReadOnly(outcomes.ToArray()); Cancelled = cancelled; }
    public DeleteUnusedBranchesPreview Preview { get; }
    public IReadOnlyList<DeleteUnusedBranchOutcome> Outcomes { get; }
    public bool Cancelled { get; }
    public int DeletedCount => Outcomes.Count(item => item.Status == DeleteUnusedBranchStatus.Deleted);
    public int FailureCount => Outcomes.Count(item => item.Status == DeleteUnusedBranchStatus.Failed);
    public bool Success => !Cancelled && FailureCount == 0;
    // Preserve the generic plugin's acquired-effects contract, including rejected leases.
    public bool RepositoryChanged => DeletedCount > 0;
    // A failed/interrupted native attempt can have effects without acknowledging success.
    public bool MayHaveRepositoryChanges => Outcomes.Any(item => item.Status != DeleteUnusedBranchStatus.NotAttempted);
    public PluginResult ToPluginResult()
    {
        Dictionary<string, DeleteUnusedBranchOutcome> outcomes = Outcomes.ToDictionary(item => item.Branch.Reference.Name, StringComparer.Ordinal);
        PluginRow[] rows = Preview.Candidates.Select(item => outcomes.TryGetValue(item.Reference.Name, out DeleteUnusedBranchOutcome? outcome)
            ? item.Row(outcome.Status.ToString(), outcome.Message) : item.Row()).ToArray();
        return new(Preview.Rule + $"\n{DeletedCount} branch(es) deleted; {FailureCount} failed." + (Cancelled ? "\nOperation cancelled; review the per-branch outcomes before retrying." : ""),
            DeleteUnusedBranchesPreview.Table(rows), RepositoryChanged: RepositoryChanged, Success: Success) { RepositoryMayHaveChanges = MayHaveRepositoryChanges };
    }
}
