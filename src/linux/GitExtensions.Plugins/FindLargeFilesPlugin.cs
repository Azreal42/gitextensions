using System.Globalization;

namespace GitExtensions.Linux.Plugins;

/// <summary>Ports the original HEAD history scan, blob aggregation and verify-pack compressed sizes.</summary>
public sealed class FindLargeFilesPlugin : INativePlugin
{
    public PluginDescriptor Descriptor { get; } = new(new Guid("5AE20AB1-D677-46C5-ABDB-7874FF5A9296"),
        "Find large files", "Find blobs at least the configured size throughout the selected revision's history and review files to remove from history.",
        "IconFindLargeFiles",
        [new("minimumMegabytes", "Minimum size (MiB)", PluginParameterKind.Number, "1", Required: true),
         new("revision", "Revision", PluginParameterKind.Text, "HEAD", Required: true)]);

    public async Task<PluginResult> ExecuteAsync(PluginContext context, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        string entered = PluginValues.Text(Descriptor, values, "minimumMegabytes");
        if (!decimal.TryParse(entered, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal mib) || mib < 0 || mib > long.MaxValue / 1048576m)
            throw new ArgumentException("Minimum size must be a nonnegative MiB value.", "minimumMegabytes");
        long threshold = (long)decimal.Truncate(mib * 1048576m);
        string revision = PluginValues.Text(Descriptor, values, "revision");
        string hash = await context.Repository.ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        string history = (await context.Repository.RunAsync(["log", "--no-show-signature", "--format=%H%x00%cI", hash, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput;
        Dictionary<string, Blob> objects = new(StringComparer.Ordinal);
        foreach (string commit in history.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] fields = commit.Split('\0');
            DateTimeOffset date = DateTimeOffset.Parse(fields[1], CultureInfo.InvariantCulture);
            string tree = (await context.Repository.RunAsync(["ls-tree", "-r", "-l", "-z", "--full-tree", fields[0]], cancellationToken).ConfigureAwait(false)).StandardOutput;
            foreach (string entry in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = entry.IndexOf('\t');
                string[] data = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (data[1] != "blob" || !long.TryParse(data[3], NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size < threshold) continue;
                if (!objects.TryGetValue(data[2], out Blob? blob))
                {
                    blob = new(data[2], entry[(tab + 1)..], size, date);
                    objects.Add(blob.Hash, blob);
                }
                blob.Commits.Add(fields[0]);
                if (date > blob.LastCommit) blob.LastCommit = date;
            }
        }
        string packPath = (await context.Repository.RunAsync(["rev-parse", "--path-format=absolute", "--git-path", "objects/pack"], cancellationToken).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n');
        if (Directory.Exists(packPath))
        {
            foreach (string pack in Directory.EnumerateFiles(packPath, "pack-*.idx"))
            {
                string output = (await context.Repository.RunAsync(["verify-pack", "-v", pack], cancellationToken).ConfigureAwait(false)).StandardOutput;
                foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length >= 4 && fields[1] == "blob" && objects.TryGetValue(fields[0], out Blob? blob)
                        && long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long compressed)) blob.CompressedSize = compressed;
                }
            }
        }
        PluginRow[] rows = objects.Values.OrderByDescending(blob => blob.Size).ThenBy(blob => blob.Path, StringComparer.Ordinal)
            .Select(blob => new PluginRow(blob.Hash, [blob.Hash, blob.Path, blob.Size.ToString(CultureInfo.InvariantCulture),
                blob.CompressedSize?.ToString(CultureInfo.InvariantCulture) ?? "Unknown", blob.Commits.Count.ToString(CultureInfo.InvariantCulture),
                blob.LastCommit.ToString("O", CultureInfo.InvariantCulture)])).ToArray();
        return new($"{rows.Length} distinct blob(s) at least {threshold} bytes in {revision} history. Identical blobs are grouped across commits; each row shows its first path.",
            new(["Object", "Path", "Size (bytes)", "Compressed size (bytes)", "Commit count", "Last commit date"], rows));
    }

    private sealed class Blob(string hash, string path, long size, DateTimeOffset lastCommit)
    {
        internal string Hash { get; } = hash;
        internal string Path { get; } = path;
        internal long Size { get; } = size;
        internal long? CompressedSize { get; set; }
        internal DateTimeOffset LastCommit { get; set; } = lastCommit;
        internal HashSet<string> Commits { get; } = new(StringComparer.Ordinal);
    }
}
