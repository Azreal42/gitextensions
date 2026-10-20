namespace GitExtensions.Linux.Desktop;

internal sealed record ConfirmationsSettingsValues
{
    public bool DontConfirmAmend { get; init; }
    public bool DontConfirmUndoLastCommit { get; init; }
    public bool DontConfirmCommitIfNoBranch { get; init; }
    public bool DontConfirmRebase { get; init; }
    public bool DontConfirmFetchAndPruneAll { get; init; }
    public bool DontConfirmPushNewBranch { get; init; }
    public bool DontConfirmAddTrackingRef { get; init; }
    public bool DontConfirmDeleteUnmergedBranch { get; init; }
    public bool ConfirmBranchCheckout { get; init; }
    public bool? AutoPopStashAfterCheckoutBranch { get; init; }
    public bool DontConfirmSecondAbortConfirmation { get; init; }
    public bool? AutoPopStashAfterPull { get; init; }
    public bool DontConfirmStashDrop { get; init; }
    public bool DontConfirmResolveConflicts { get; init; }
    public bool DontConfirmCommitAfterConflictsResolved { get; init; }
    public bool? DontConfirmUpdateSubmodulesOnCheckout { get; init; }
    public bool DontConfirmSwitchWorktree { get; init; }

    internal bool? Get(string name) => name switch
    {
        nameof(DontConfirmAmend) => DontConfirmAmend, nameof(DontConfirmUndoLastCommit) => DontConfirmUndoLastCommit,
        nameof(DontConfirmCommitIfNoBranch) => DontConfirmCommitIfNoBranch, nameof(DontConfirmRebase) => DontConfirmRebase,
        nameof(DontConfirmFetchAndPruneAll) => DontConfirmFetchAndPruneAll, nameof(DontConfirmPushNewBranch) => DontConfirmPushNewBranch,
        nameof(DontConfirmDeleteUnmergedBranch) => DontConfirmDeleteUnmergedBranch, nameof(ConfirmBranchCheckout) => ConfirmBranchCheckout,
        nameof(AutoPopStashAfterCheckoutBranch) => AutoPopStashAfterCheckoutBranch, nameof(DontConfirmSecondAbortConfirmation) => DontConfirmSecondAbortConfirmation,
        nameof(DontConfirmAddTrackingRef) => DontConfirmAddTrackingRef, nameof(AutoPopStashAfterPull) => AutoPopStashAfterPull,
        nameof(DontConfirmStashDrop) => DontConfirmStashDrop, nameof(DontConfirmResolveConflicts) => DontConfirmResolveConflicts,
        nameof(DontConfirmCommitAfterConflictsResolved) => DontConfirmCommitAfterConflictsResolved,
        nameof(DontConfirmUpdateSubmodulesOnCheckout) => DontConfirmUpdateSubmodulesOnCheckout, nameof(DontConfirmSwitchWorktree) => DontConfirmSwitchWorktree,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal ConfirmationsSettingsValues With(string name, bool? value) => name switch
    {
        nameof(DontConfirmAmend) => this with { DontConfirmAmend = value == true },
        nameof(DontConfirmUndoLastCommit) => this with { DontConfirmUndoLastCommit = value == true },
        nameof(DontConfirmCommitIfNoBranch) => this with { DontConfirmCommitIfNoBranch = value == true },
        nameof(DontConfirmRebase) => this with { DontConfirmRebase = value == true },
        nameof(DontConfirmFetchAndPruneAll) => this with { DontConfirmFetchAndPruneAll = value == true },
        nameof(DontConfirmPushNewBranch) => this with { DontConfirmPushNewBranch = value == true },
        nameof(DontConfirmDeleteUnmergedBranch) => this with { DontConfirmDeleteUnmergedBranch = value == true },
        nameof(ConfirmBranchCheckout) => this with { ConfirmBranchCheckout = value == true },
        nameof(AutoPopStashAfterCheckoutBranch) => this with { AutoPopStashAfterCheckoutBranch = value },
        nameof(DontConfirmSecondAbortConfirmation) => this with { DontConfirmSecondAbortConfirmation = value == true },
        nameof(DontConfirmAddTrackingRef) => this with { DontConfirmAddTrackingRef = value == true },
        nameof(AutoPopStashAfterPull) => this with { AutoPopStashAfterPull = value },
        nameof(DontConfirmStashDrop) => this with { DontConfirmStashDrop = value == true },
        nameof(DontConfirmResolveConflicts) => this with { DontConfirmResolveConflicts = value == true },
        nameof(DontConfirmCommitAfterConflictsResolved) => this with { DontConfirmCommitAfterConflictsResolved = value == true },
        nameof(DontConfirmUpdateSubmodulesOnCheckout) => this with { DontConfirmUpdateSubmodulesOnCheckout = value },
        nameof(DontConfirmSwitchWorktree) => this with { DontConfirmSwitchWorktree = value == true },
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}

internal sealed record ConfirmationSetting(string Name, string Profile, string Key, string Control, string Caption,
    string Group, bool Nullable = false, bool ThreeState = false, bool Inverted = true)
{
    internal bool? ToCheckState(bool? value) => value.HasValue ? Inverted ? !value.Value : value : null;
    internal bool? FromCheckState(bool? check) => Nullable ? check.HasValue ? Inverted ? !check.Value : check : null
        : Inverted ? check == false : check != false;
}

internal static class ConfirmationSettingsCatalog
{
    internal static readonly IReadOnlyList<ConfirmationSetting> Fields = Array.AsReadOnly<ConfirmationSetting>(
    [
        new("DontConfirmAmend", "commit-preferences.json", "DontConfirmAmend", "chkAmend", "Amend last commit", "Commits"),
        new("DontConfirmUndoLastCommit", "browse-command-preferences.json", "DontConfirmUndoLastCommit", "chkUndoLastCommitConfirmation", "Undo last commit", "Commits", ThreeState: true),
        new("DontConfirmCommitIfNoBranch", "commit-preferences.json", "DontConfirmCommitIfNoBranch", "chkCommitIfNoBranch", "Commit when no branch is currently checked out (headless state)", "Commits"),
        new("DontConfirmRebase", "rebase-preferences.json", "DontConfirmRebase", "chkRebaseOnTopOfSelectedCommit", "Rebase on top of selected commit", "Commits", ThreeState: true),
        new("DontConfirmFetchAndPruneAll", "pull-preferences.json", "DontConfirmFetchAndPruneAll", "chkFetchAndPruneAllConfirmation", "Fetch and prune branches", "Branches"),
        new("DontConfirmPushNewBranch", "push-preferences.json", "DontConfirmNewBranch", "chkPushNewBranch", "Push a new branch for the remote", "Branches"),
        new("DontConfirmAddTrackingRef", "push-preferences.json", "DontConfirmAddTracking", "chkAddTrackingRef", "Add a tracking reference for newly pushed branch", "Branches"),
        new("DontConfirmDeleteUnmergedBranch", "reference-preferences.json", "DontConfirmDeleteUnmergedBranch", "chkBranchDeleteUnmerged", "Delete unmerged branches", "Branches"),
        new("ConfirmBranchCheckout", "reference-preferences.json", "ConfirmBranchCheckout", "chkBranchCheckoutConfirmation", "Checkout branch using left panel", "Branches", Inverted: false),
        new("AutoPopStashAfterPull", "pull-preferences.json", "AutoPopStashAfterPull", "chkAutoPopStashAfterPull", "Apply stashed changes after successful pull (else stash will be popped automatically)", "Stashes", Nullable: true, ThreeState: true),
        new("AutoPopStashAfterCheckoutBranch", "reference-preferences.json", "AutoPopStashAfterCheckoutBranch", "chkAutoPopStashAfterCheckout", "Apply stashed changes after successful checkout (else stash will be popped automatically)", "Stashes", Nullable: true, ThreeState: true),
        new("DontConfirmStashDrop", "stash-preferences.json", "DontConfirmDrop", "chkConfirmStashDrop", "Drop stash", "Stashes"),
        new("DontConfirmResolveConflicts", "conflict-confirmation-preferences.json", "DontConfirmResolveConflicts", "chkResolveConflicts", "Resolve conflicts", "Conflicts", ThreeState: true),
        new("DontConfirmCommitAfterConflictsResolved", "conflict-confirmation-preferences.json", "DontConfirmCommitAfterConflictsResolved", "chkCommitAfterConflictsResolved", "Commit changes after conflicts have been resolved", "Conflicts", ThreeState: true),
        new("DontConfirmSecondAbortConfirmation", "conflict-confirmation-preferences.json", "DontConfirmSecondAbortConfirmation", "chkSecondAbortConfirmation", "Confirm for the second time to abort a merge", "Conflicts", ThreeState: true),
        new("DontConfirmUpdateSubmodulesOnCheckout", "submodule-checkout-preferences.json", "DontConfirmUpdateSubmodulesOnCheckout", "chkUpdateModules", "Update submodules on checkout", "Submodules", Nullable: true, ThreeState: true),
        new("DontConfirmSwitchWorktree", "browse-command-preferences.json", "DontConfirmSwitchWorktree", "chkSwitchWorktree", "Switch worktree", "Worktrees")
    ]);
}
