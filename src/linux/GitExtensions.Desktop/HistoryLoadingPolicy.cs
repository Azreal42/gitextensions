namespace GitExtensions.Linux.Desktop;

/// <summary>The saved Windows graph ceiling is independent of the positive Git read/page size.</summary>
internal readonly record struct HistoryLoadingPolicy
{
    internal int MaximumCommits { get; }
    // One additional result is reserved for the sentinel. This is an integer
    // representation bound, not a user-visible history ceiling in unlimited mode.
    private int RepresentableWindow => MaximumCommits == 0 ? int.MaxValue - 1 : Math.Min(MaximumCommits, int.MaxValue - 1);

    internal HistoryLoadingPolicy(int maximumCommits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCommits);
        MaximumCommits = maximumCommits;
    }

    internal int ClampWindow(int requested) => Math.Clamp(requested, 1, RepresentableWindow);
    internal int InitialWindow(int batchSize) => ClampWindow(batchSize);
    internal bool CanExpand(int current) => current < RepresentableWindow;
    internal int NextWindow(int current, int batchSize)
        => (int)Math.Min(RepresentableWindow, Math.Max(1L, (long)current + Math.Max(1, batchSize)));
    internal int ReadCount(int window) => checked(ClampWindow(window) + 1);
    internal bool HasMore(int returnedCount, int window) => returnedCount > window && CanExpand(window);
}
