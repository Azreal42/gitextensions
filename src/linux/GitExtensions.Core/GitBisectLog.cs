namespace GitExtensions.Linux;

internal static class GitBisectLog
{
    internal static IEnumerable<string> ReadBadRevisions(string log, bool possible = false)
    {
        string prefix = possible ? "# possible first " : "# first ";
        foreach (string line in log.Split('\n'))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            // Recent Git quotes the term in BISECT_LOG; older versions write it bare.
            string record = line[prefix.Length..];
            string? marker = record.StartsWith("bad commit: [", StringComparison.Ordinal) ? "bad commit: ["
                : record.StartsWith("'bad' commit: [", StringComparison.Ordinal) ? "'bad' commit: [" : null;
            if (marker is null) { continue; }
            int end = record.IndexOf(']', marker.Length);
            if (end < 0 || (end + 1 < record.Length && record[end + 1] is not (' ' or '\r'))) { continue; }
            string hash = record[marker.Length..end];
            if (hash.Length is not (40 or 64) || !hash.All(char.IsAsciiHexDigit)) { continue; }
            yield return hash;
        }
    }
}
