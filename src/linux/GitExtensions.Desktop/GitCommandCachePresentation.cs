namespace GitExtensions.Linux.Desktop;

/// <summary>The Windows cache viewer shows real admitted output with visible whitespace.</summary>
internal static class GitCommandCachePresentation
{
    internal static string Detail(GitCommandCacheEntry entry)
        => entry.Key + "\n-------------------------------------\n\n" + PrintableChars(entry.StandardOutput)
            + "\n-------------------------------------\n\n" + PrintableChars(entry.StandardError);

    internal static string? PrintableChars(string? value)
        => value?.Replace("\0", @"\0", StringComparison.Ordinal).Replace("\r", @"\r", StringComparison.Ordinal)
            .Replace("\n", "\\n\n", StringComparison.Ordinal).Replace("\t", "»", StringComparison.Ordinal)
            .Replace(" ", "·", StringComparison.Ordinal).Replace("\u001b", @"\x1b", StringComparison.Ordinal);
}
