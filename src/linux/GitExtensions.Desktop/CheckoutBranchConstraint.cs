namespace GitExtensions.Linux.Desktop;

/// <summary>A conflict workflow can constrain the shared checkout picker and revalidate its native execution.</summary>
public sealed record CheckoutBranchConstraint(
    Func<CancellationToken, Task<IReadOnlyList<GitReference>>> ReadReferencesAsync,
    Func<GitReference, GitCheckoutOptions, CancellationToken, Task<GitCheckoutResult>> CheckoutAsync)
{
    /// <summary>Only an explicitly opted-in host may use the shared default action before showing this picker.</summary>
    public bool AllowDefaultAction { get; init; }
    public Func<GitReference, GitCheckoutOptions, CancellationToken, Task<GitCheckoutPreparation>>? PrepareAsync { get; init; }
    public Func<GitReference, GitCheckoutPreparation, CancellationToken, Task<GitCheckoutResult>>? CompleteAsync { get; init; }
}
