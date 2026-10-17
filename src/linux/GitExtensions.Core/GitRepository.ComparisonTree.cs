using System.Globalization;

namespace GitExtensions.Linux;

public sealed record GitComparisonTreeStage(int Stage, string Mode, string Hash);
public sealed record GitComparisonTreeEntry(string Path, string? Mode, string? Hash, IReadOnlyList<GitComparisonTreeStage> IndexStages)
{
    public bool IsSubmodule => Mode == "160000" || Mode is null && IndexStages.Any(entry => entry.Mode == "160000");
    public bool IsConflict => IndexStages.Any(entry => entry.Stage != 0);
    public string Type => Mode switch { "160000" => "commit", "040000" => "tree", null => "conflict", _ => "blob" };
}

public sealed partial class GitRepository
{
    /// <summary>Reads committed/index/live entries without inventing a commit or a content hash for live/conflicted files.</summary>
    public async Task<IReadOnlyList<GitComparisonTreeEntry>> GetComparisonTreeAsync(GitCompareRevision source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(source.Kind)) { throw new ArgumentOutOfRangeException(nameof(source)); }
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Kind == GitCompareRevisionKind.EmptyTree) { return []; }
        if (source.Kind == GitCompareRevisionKind.Commit)
        {
            string commit = ReadOutputLine((await ExecuteAsync(["--no-optional-locks", "rev-parse", "--verify", "--end-of-options", ValidateValue(source.Revision) + "^{commit}"],
                cancellationToken).ConfigureAwait(false)).StandardOutput);
            string tree = (await ExecuteAsync(["--no-optional-locks", "--literal-pathspecs", "ls-tree", "-r", "-z", "--full-tree", commit, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput;
            List<GitComparisonTreeEntry> committed = [];
            foreach (string record in ReadTreeRecords(tree))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int tab = record.IndexOf('\t');
                string[] header = (tab < 0 ? "" : record[..tab]).Split(' ');
                if (header is not [string mode, _, string hash] || !IsObjectId(hash) || tab == record.Length - 1)
                { throw new FormatException("Invalid committed tree entry."); }
                committed.Add(new(record[(tab + 1)..], mode, hash, []));
            }
            return committed;
        }
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        string output = (await ExecuteAsync(["--no-optional-locks", "--literal-pathspecs", "ls-files", "--stage", "-z", "--"], cancellationToken).ConfigureAwait(false)).StandardOutput;
        Dictionary<string, List<GitComparisonTreeStage>> index = new(StringComparer.Ordinal);
        foreach (string record in ReadTreeRecords(output))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tab = record.IndexOf('\t');
            string[] header = (tab < 0 ? "" : record[..tab]).Split(' ');
            if (header is not [string mode, string hash, string stageText] || !IsObjectId(hash)
                || !int.TryParse(stageText, NumberStyles.None, CultureInfo.InvariantCulture, out int stage) || stage is < 0 or > 3 || tab == record.Length - 1)
            { throw new FormatException("Invalid index tree entry."); }
            string path = record[(tab + 1)..];
            if (!index.TryGetValue(path, out List<GitComparisonTreeStage>? stages)) { index[path] = stages = []; }
            stages.Add(new(stage, mode, hash));
        }
        if (source.Kind == GitCompareRevisionKind.Index)
        {
            return index.Select(pair =>
            {
                GitComparisonTreeStage? stage0 = pair.Value.SingleOrDefault(stage => stage.Stage == 0);
                return new GitComparisonTreeEntry(pair.Key, stage0?.Mode, stage0?.Hash, pair.Value.AsReadOnly());
            }).ToArray();
        }
        string untracked = (await ExecuteAsync(["--no-optional-locks", "--literal-pathspecs", "ls-files", "--others", "--exclude-standard", "-z", "--"], cancellationToken).ConfigureAwait(false)).StandardOutput;
        List<GitComparisonTreeEntry> live = [];
        foreach (string path in index.Keys.Concat(ReadTreeRecords(untracked)).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = ValidatePath(path), full = System.IO.Path.Combine(Path, normalized);
            FileInfo file = new(full);
            IReadOnlyList<GitComparisonTreeStage> stages = index.TryGetValue(path, out List<GitComparisonTreeStage>? tracked) ? tracked.AsReadOnly() : [];
            string mode;
            if (file.LinkTarget is not null) { mode = "120000"; }
            else if (Directory.Exists(full))
            { mode = stages.Any(stage => stage.Mode == "160000") ? "160000" : "040000"; }
            else if (File.Exists(full))
            {
                UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                mode = !OperatingSystem.IsWindows() && (File.GetUnixFileMode(full) & executable) != 0 ? "100755" : "100644";
            }
            else { continue; } // Deleted tracked paths have no working-tree entry.
            live.Add(new(normalized, mode, Hash: null, stages));
        }
        return live;
    }

    private static IEnumerable<string> ReadTreeRecords(string output)
    {
        if (output.Length > 0 && output[^1] != '\0') { throw new FormatException("Incomplete NUL-terminated tree entries."); }
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
