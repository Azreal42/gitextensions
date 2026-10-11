using System.Globalization;
using System.Text;

namespace GitExtensions.Linux;

public sealed record GitFileHistoryEntry(GitCommit Commit, string Path, char Status, string? PreviousPath = null)
{
    public bool IsDeleted => Status == 'D';
}
public sealed record GitFileHistoryOptions(bool FollowRenames = true, bool ExactRenames = false, bool FullHistory = false,
    bool SimplifyMerges = false, int Count = 300);
public sealed record GitFileBlameOptions(bool IgnoreWhitespace = false, bool DetectMoves = false, bool DetectCopies = false);
public sealed record GitFileBlameLine(GitBlameLine Line, string OriginalPath, string? PreviousHash = null, string? PreviousPath = null, bool IsBoundary = false);
public sealed record GitFileBlame(string Path, string? Revision, IReadOnlyList<GitFileBlameLine> Lines, byte[] Bytes);

public sealed partial class GitRepository
{
    public async Task<IReadOnlyList<GitFileHistoryEntry>> GetFileHistoryAsync(string path, string revision = "HEAD",
        GitFileHistoryOptions? options = null, CancellationToken cancellationToken = default)
    {
        path = ValidatePath(path);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Count);
        string target = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        List<string> arguments = ["--literal-pathspecs", "log", "--topo-order", "--parents", "-z", "--name-status", "--no-ext-diff", "--no-textconv",
            "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%s%x00%b", "--max-count=" + options.Count.ToString(CultureInfo.InvariantCulture)];
        if (options.FollowRenames) { arguments.Add("--follow"); }
        arguments.Add(options.ExactRenames ? "--find-renames=100%" : "--find-renames");
        if (options.ExactRenames) { arguments.AddRange(["--find-copies=100%", "--find-copies-harder"]); }
        if (options.FullHistory) { arguments.Add("--full-history"); }
        if (options.FullHistory && options.SimplifyMerges) { arguments.Add("--simplify-merges"); }
        arguments.AddRange([target, "--", path]);
        string[] fields = (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput.Split('\0');
        List<GitFileHistoryEntry> history = [];
        string olderPath = path;
        int index = 0;
        while (index < fields.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (index < fields.Length && string.IsNullOrEmpty(fields[index])) { index++; }
            if (index == fields.Length) { break; }
            string hash = fields[index++].TrimStart('\n');
            if (!IsObjectId(hash) || index + 5 >= fields.Length) { throw new FormatException("Incomplete file-history commit record."); }
            GitCommit commit = new(hash, fields[index++].Split(' ', StringSplitOptions.RemoveEmptyEntries), fields[index++], fields[index++],
                DateTimeOffset.Parse(fields[index++], CultureInfo.InvariantCulture), fields[index++], fields[index++]);
            List<GitRevisionFile> changes = [];
            while (index < fields.Length)
            {
                string status = fields[index].TrimStart('\n');
                if (status.Length == 0) { index++; continue; }
                if (IsObjectId(status)) { break; }
                index++;
                if (index >= fields.Length) { throw new FormatException("Missing file-history path."); }
                string first = fields[index++];
                if (status[0] is 'R' or 'C')
                {
                    if (index >= fields.Length) { throw new FormatException("Missing file-history rename destination."); }
                    changes.Add(new(fields[index++], status[0], first));
                }
                else { changes.Add(new(first, status[0])); }
            }
            GitRevisionFile? changed = changes.FirstOrDefault(file => file.Path == olderPath);
            history.Add(new(commit, changed?.Path ?? olderPath, changed?.Status ?? 'M', changed?.OriginalPath));
            if (options.FollowRenames && changed?.OriginalPath is not null) { olderPath = changed.OriginalPath; }
        }
        // --follow keeps raw parents on some Git versions. Walk omitted commits
        // to the next displayed ancestor so a path-filtered graph still connects
        // real ancestry; never manufacture a single lane from chronological order.
        HashSet<string> displayed = history.Select(entry => entry.Commit.Hash).ToHashSet(StringComparer.Ordinal);
        if (history.Any(entry => entry.Commit.Parents.Any(parent => !displayed.Contains(parent))))
        {
            string[] excluded = history[^1].Commit.Parents.Select(parent => "^" + parent).ToArray();
            string raw = (await ExecuteAsync(["rev-list", "--parents", "--topo-order", target, .. excluded], cancellationToken).ConfigureAwait(false)).StandardOutput;
            Dictionary<string, string[]> parents = new(StringComparer.Ordinal);
            foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] ids = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                parents[ids[0]] = ids[1..];
            }
            for (int position = 0; position < history.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GitFileHistoryEntry entry = history[position];
                List<string> visible = [];
                Queue<string> pending = new(entry.Commit.Parents);
                HashSet<string> visited = new(StringComparer.Ordinal);
                while (pending.TryDequeue(out string? parent))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!visited.Add(parent)) { continue; }
                    if (displayed.Contains(parent)) { visible.Add(parent); }
                    else if (parents.TryGetValue(parent, out string[]? ancestors)) { foreach (string ancestor in ancestors) { pending.Enqueue(ancestor); } }
                    else if (position == history.Count - 1) { visible.Add(parent); } // Truncated-history boundary.
                }
                history[position] = entry with { Commit = entry.Commit with { Parents = visible.Distinct(StringComparer.Ordinal).ToArray() } };
            }
        }
        return history;
    }

    public async Task<GitFileBlame> GetFileBlameAsync(string path, string? revision = "HEAD", GitFileBlameOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        path = ValidatePath(path);
        options ??= new();
        string? target = revision is null ? null : await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        byte[] bytes;
        if (target is null) { await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false); bytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(Path, path), cancellationToken).ConfigureAwait(false); }
        else { bytes = await GetFileBytesAsync(path, target, cancellationToken).ConfigureAwait(false); }
        List<string> arguments = ["--literal-pathspecs", "blame", "--line-porcelain"];
        if (options.IgnoreWhitespace) { arguments.Add("-w"); }
        if (options.DetectMoves) { arguments.Add("-M"); }
        if (options.DetectCopies) { arguments.Add("-C"); }
        if (target is not null) { arguments.Add(target); }
        arguments.AddRange(["--", path]);
        string output = (await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput;
        IReadOnlyList<GitBlameLine> parsed = GitOutputParser.ParseBlame(output);
        List<GitFileBlameLine> lines = [];
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);
        foreach (string line in output.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.StartsWith('\t'))
            {
                string? previous = metadata.GetValueOrDefault("previous");
                int space = previous?.IndexOf(' ') ?? -1;
                lines.Add(new(parsed[lines.Count], DecodeBlamePath(metadata.GetValueOrDefault("filename", path)),
                    space < 0 ? null : previous![..space], space < 0 ? null : DecodeBlamePath(previous![(space + 1)..]), metadata.ContainsKey("boundary")));
                metadata.Clear();
            }
            else if (line.Length > 0)
            {
                int space = line.IndexOf(' ');
                string key = space < 0 ? line : line[..space];
                if (!IsObjectId(key)) { metadata[key] = space < 0 ? "" : line[(space + 1)..]; }
            }
        }
        return new(path, target, lines, bytes);
    }

    private static bool IsObjectId(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    // Git quotes control characters even with core.quotepath=false. Octal escapes
    // represent UTF-8 bytes; trimming or regex-unescaping would lose literal names.
    private static string DecodeBlamePath(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') { return value; }
        List<byte> bytes = [];
        for (int index = 1; index < value.Length - 1; index++)
        {
            char character = value[index];
            if (character == '\\' && index + 1 < value.Length - 1)
            {
                character = value[++index];
                if (character is >= '0' and <= '7')
                {
                    int octal = character - '0';
                    for (int count = 1; count < 3 && index + 1 < value.Length - 1 && value[index + 1] is >= '0' and <= '7'; count++)
                    { octal = octal * 8 + value[++index] - '0'; }
                    bytes.Add((byte)octal); continue;
                }
                character = character switch { 'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', _ => character };
            }
            int length = char.IsHighSurrogate(character) && index + 1 < value.Length - 1 && char.IsLowSurrogate(value[index + 1]) ? 2 : 1;
            bytes.AddRange(Encoding.UTF8.GetBytes(length == 1 ? character.ToString() : value.Substring(index, length)));
            index += length - 1;
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
