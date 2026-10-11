using System.Globalization;

namespace GitExtensions.Linux;

public static class GitOutputParser
{
    public static GitStatus ParseStatus(string output)
    {
        string? branch = null;
        string? head = null;
        int ahead = 0;
        int behind = 0;
        List<GitChange> changes = [];
        string[] entries = output.Split('\0');
        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = entry[14..];
            }
            else if (entry.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                head = entry[13..] is "(initial)" ? null : entry[13..];
            }
            else if (entry.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                string[] counts = entry[12..].Split(' ');
                ahead = int.Parse(counts[0].AsSpan(1), CultureInfo.InvariantCulture);
                behind = int.Parse(counts[1].AsSpan(1), CultureInfo.InvariantCulture);
            }
            else if (entry[0] is '?' or '!')
            {
                changes.Add(new GitChange(entry[2..], null, entry[0], entry[0], false, false));
            }
            else if (entry[0] is '1' or '2' or 'u')
            {
                int separators = entry[0] switch { '1' => 8, '2' => 9, _ => 10 };
                string[] fields = entry.Split(' ', separators + 1, StringSplitOptions.None);
                if (fields.Length != separators + 1 || fields[1].Length != 2)
                {
                    throw new FormatException("Incomplete Git porcelain status record.");
                }

                string? originalPath = null;
                int? similarityPercent = null;
                if (entry[0] == '2')
                {
                    if (++i >= entries.Length || entries[i].Length == 0)
                    {
                        throw new FormatException("Missing original pathname in Git rename status.");
                    }

                    originalPath = entries[i];
                    ReadOnlySpan<char> score = fields[8].AsSpan();
                    if (score.Length < 2 || score[0] is not ('R' or 'C')
                        || score[1..].IndexOfAnyExceptInRange('0', '9') >= 0
                        || !int.TryParse(score[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int percentage) || percentage > 100)
                    {
                        throw new FormatException("Invalid Git porcelain rename/copy percentage.");
                    }
                    similarityPercent = percentage;
                }

                changes.Add(new GitChange(fields[^1], originalPath, fields[1][0], fields[1][1], entry[0] == 'u', fields[2].StartsWith('S'))
                    { SimilarityPercent = similarityPercent });
            }
        }

        return new GitStatus(branch, head, ahead, behind, changes);
    }

    internal static IReadOnlyList<GitCommit> ParseLog(string output)
    {
        List<GitCommit> commits = [];
        string[] fields = output.Split('\0');
        for (int i = 0; i + 6 < fields.Length; i += 7)
        {
            commits.Add(new GitCommit(fields[i], fields[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries), fields[i + 2], fields[i + 3],
                DateTimeOffset.Parse(fields[i + 4], CultureInfo.InvariantCulture), fields[i + 5], fields[i + 6]));
        }

        return commits;
    }

    internal static IReadOnlyList<GitWorktree> ParseWorktrees(string output)
    {
        List<GitWorktree> worktrees = [];
        foreach (string record in output.Split("\0\0", StringSplitOptions.RemoveEmptyEntries))
        {
            Dictionary<string, string> values = record.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(field => field.Split(' ', 2)).ToDictionary(field => field[0], field => field.Length == 2 ? field[1] : "", StringComparer.Ordinal);
            worktrees.Add(new GitWorktree(values["worktree"], values.GetValueOrDefault("HEAD"), values.GetValueOrDefault("branch"),
                values.ContainsKey("bare"), values.ContainsKey("detached"), values.GetValueOrDefault("locked"), values.GetValueOrDefault("prunable")));
        }

        return worktrees;
    }

    internal static IReadOnlyList<GitBlameLine> ParseBlame(string output)
    {
        List<GitBlameLine> lines = [];
        string hash = "";
        int originalLine = 0;
        int lineNumber = 0;
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);
        foreach (string line in output.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                DateTimeOffset authoredAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(metadata["author-time"], CultureInfo.InvariantCulture));
                string offset = metadata["author-tz"];
                int sign = offset[0] == '-' ? -1 : 1;
                authoredAt = authoredAt.ToOffset(TimeSpan.FromMinutes(sign * (int.Parse(offset.AsSpan(1, 2), CultureInfo.InvariantCulture) * 60 + int.Parse(offset.AsSpan(3, 2), CultureInfo.InvariantCulture))));
                lines.Add(new GitBlameLine(hash, originalLine, lineNumber, metadata["author"], metadata["author-mail"].Trim('<', '>'), authoredAt,
                    metadata.GetValueOrDefault("summary", ""), line[1..]));
                metadata.Clear();
            }
            else if (line.Length > 0)
            {
                string[] parts = line.Split(' ', 4);
                if (parts.Length >= 3 && parts[0].Length is 40 or 64 && int.TryParse(parts[1], out originalLine) && int.TryParse(parts[2], out lineNumber))
                {
                    hash = parts[0];
                }
                else
                {
                    int space = line.IndexOf(' ');
                    metadata[space < 0 ? line : line[..space]] = space < 0 ? "" : line[(space + 1)..];
                }
            }
        }

        return lines;
    }
}
