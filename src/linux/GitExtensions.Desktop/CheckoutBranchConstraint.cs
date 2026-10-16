namespace GitExtensions.Linux.Desktop;

/// <summary>A conflict workflow can constrain the shared checkout picker and revalidate its native execution.</summary>
public sealed record CheckoutBranchConstraint(
    Func<CancellationToken, Task<IReadOnlyList<GitReference>>> ReadReferencesAsync,
    Func<GitReference, GitCheckoutOptions, CancellationToken, Task<GitCheckoutResult>> CheckoutAsync);
