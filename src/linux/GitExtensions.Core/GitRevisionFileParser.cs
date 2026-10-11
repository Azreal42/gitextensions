using System.Globalization;

namespace GitExtensions.Linux;

/// <summary>Parses Git's literal --name-status -z output, including rename/copy similarity scores.</summary>
public static class GitRevisionFileParser
{
    public static IReadOnlyList<GitRevisionFile> ParseNameStatus(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length == 0) { return []; }
        if (output[^1] != '\0') { throw new FormatException("Incomplete NUL-terminated Git file-change output."); }
        string[] fields = output.Split('\0');
        List<GitRevisionFile> files = [];
        int index = 0;
        while (index < fields.Length - 1)
        {
            string statusField = ReadField();
            char status = statusField[0];
            if (status is not ('A' or 'C' or 'D' or 'M' or 'R' or 'T' or 'U' or 'X' or 'B'))
            { throw new FormatException("Unknown Git file-change status."); }
            int? similarity = null;
            if (status is 'R' or 'C') { similarity = ReadPercentage(statusField.AsSpan(1)); }
            else if (status == 'M' && statusField.Length > 1) { _ = ReadPercentage(statusField.AsSpan(1)); }
            else if (statusField.Length != 1) { throw new FormatException("Invalid Git file-change status suffix."); }
            string firstPath = ReadField();
            files.Add(status is 'R' or 'C'
                ? new GitRevisionFile(ReadField(), status, firstPath) { SimilarityPercent = similarity }
                : new GitRevisionFile(firstPath, status));
        }
        return files;

        string ReadField()
        {
            if (index >= fields.Length - 1 || fields[index].Length == 0)
            { throw new FormatException("Incomplete Git file-change record."); }
            return fields[index++];
        }
    }

    private static int ReadPercentage(ReadOnlySpan<char> score)
    {
        if (score.IsEmpty || score.IndexOfAnyExceptInRange('0', '9') >= 0
            || !int.TryParse(score, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value > 100)
        { throw new FormatException("Invalid Git rename/copy percentage."); }
        return value;
    }
}
