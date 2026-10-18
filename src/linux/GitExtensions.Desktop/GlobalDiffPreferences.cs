namespace GitExtensions.Linux.Desktop;

// General and the Blame menu share one saved algorithm. Reader-local context,
// whitespace and text choices remain intact when another window changes it.
internal static class GlobalDiffPreferences
{
    internal static bool UseHistogram(string? configurationRoot = null)
        => new BlamePreferencesStore(configurationRoot).Load().UseHistogramDiffAlgorithm;

    internal static GitComparisonDiffOptions Apply(GitComparisonDiffOptions options, string? configurationRoot = null)
        => options with { UseHistogram = UseHistogram(configurationRoot) };
}
