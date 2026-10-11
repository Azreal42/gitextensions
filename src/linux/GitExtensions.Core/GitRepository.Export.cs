using System.Globalization;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Exports committed content using Git's archive attributes. An existing destination is never overwritten.</summary>
    public Task<GitArchiveResult> ExportArchiveAsync(GitArchiveOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Format)) { throw new ArgumentOutOfRangeException(nameof(options), "Unsupported archive format."); }
        if (options.Paths is not null && options.DiffRevision is not null) { throw new ArgumentException("Choose literal paths or a comparison revision, not both.", nameof(options)); }
        if (options.CompressionLevel is int compression && (options.Format != GitArchiveFormat.Zip || compression is < 0 or > 9))
        {
            throw new ArgumentException("ZIP compression must be between 0 and 9; TAR does not accept a compression level.", nameof(options));
        }

        string output = ExportOutputPath(options.OutputPath);
        string prefix = ArchivePrefix(options.Prefix);
        string[]? selected = options.Paths is null ? null : ValidatePaths(options.Paths);
        return WithMutationAsync(async token =>
        {
            await GetInfoAsync(token).ConfigureAwait(false);
            string revision = await ResolveRevisionAsync(options.Revision, token).ConfigureAwait(false);
            if (options.DiffRevision is not null)
            {
                string previous = await ResolveRevisionAsync(options.DiffRevision, token).ConfigureAwait(false);
                GitResult changed = await ExecuteAsync(["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "--diff-filter=ACMRT", "-z", previous, revision, "--"], token).ConfigureAwait(false);
                selected = changed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (selected.Length == 0) { throw new InvalidOperationException("There are no changed files present in the target revision to archive."); }
            }

            EnsureExportAbsent(output);
            string parent = System.IO.Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(parent);
            string temporary = System.IO.Path.Combine(parent, ".gitextensions-archive-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Reserve our private file; Git may overwrite this file, never the user's destination.
                await using (FileStream reserve = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                List<string> arguments = ["archive", "--format=" + options.Format.ToString().ToLowerInvariant(), "--output=" + temporary];
                if (prefix.Length != 0) { arguments.Add("--prefix=" + prefix); }
                if (options.CompressionLevel is int level) { arguments.Add("-" + level.ToString(CultureInfo.InvariantCulture)); }
                arguments.AddRange([revision, "--", .. selected ?? []]);
                await RunExportAsync(arguments, token, literalPaths: true).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, output, overwrite: false);
                return new GitArchiveResult(output, revision, new FileInfo(output).Length, selected ?? []);
            }
            finally { File.Delete(temporary); }
        }, cancellationToken);
    }

    /// <summary>Creates mailbox patches with the original dialog's selection semantics, without overwriting existing patch files.</summary>
    public Task<GitFormatPatchResult> FormatPatchAsync(GitFormatPatchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.StartNumber);
        string output = ExportOutputPath(options.OutputDirectory);
        if (options.SubjectPrefix?.Contains('\0') == true) { throw new ArgumentException("A subject prefix cannot contain NUL.", nameof(options)); }
        if (options.SelectedRevisions is not null && (options.SelectedRevisions.Count == 0 || options.FromRevision is not null || options.ToRevision != "HEAD"))
        {
            throw new ArgumentException("Choose selected revisions or explicit range bounds, not both.", nameof(options));
        }
        if (options.SelectedRevisions?.Count > 2 && options.CoverLetter) { throw new ArgumentException("A cover letter requires a single commit or a contiguous range.", nameof(options)); }

        return WithMutationAsync(async token =>
        {
            await GetInfoAsync(token).ConfigureAwait(false);
            List<(string? From, string To)> ranges = [];
            if (options.SelectedRevisions is null)
            {
                ranges.Add((options.FromRevision is null ? null : await ResolveRevisionAsync(options.FromRevision, token).ConfigureAwait(false),
                    await ResolveRevisionAsync(options.ToRevision, token).ConfigureAwait(false)));
            }
            else
            {
                List<GitCommit> selected = [];
                foreach (string revision in options.SelectedRevisions)
                {
                    string hash = await ResolveRevisionAsync(revision, token).ConfigureAwait(false);
                    if (selected.Any(commit => commit.Hash == hash)) { throw new ArgumentException("Select every revision at most once.", nameof(options)); }
                    selected.Add((await GetLogAsync(1, hash, token).ConfigureAwait(false)).Single());
                }

                if (selected.Count <= 2) { ranges.Add((selected[0].Parents.FirstOrDefault(), selected[^1].Hash)); }
                else { ranges.AddRange(selected.Select(commit => (commit.Parents.FirstOrDefault(), commit.Hash))); }
            }

            Directory.CreateDirectory(output);
            string temporary = System.IO.Path.Combine(output, ".gitextensions-patches-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                int number = options.StartNumber;
                foreach ((string? from, string to) in ranges)
                {
                    List<string> arguments = ["format-patch", "--output-directory=" + temporary, "--start-number=" + number.ToString(CultureInfo.InvariantCulture)];
                    if (!options.FindRenames && !options.FindCopies) { arguments.Add("--no-renames"); }
                    else { arguments.Add("--find-renames"); }
                    if (options.FindCopies) { arguments.Add("--find-copies"); }
                    if (options.BreakRewrites) { arguments.Add("--break-rewrites"); }
                    arguments.Add(options.CoverLetter ? "--cover-letter" : "--no-cover-letter");
                    arguments.Add(options.SignOff ? "--signoff" : "--no-signoff");
                    if (options.SubjectPrefix is not null) { arguments.Add("--subject-prefix=" + options.SubjectPrefix); }
                    if (options.Numbered is bool numbered) { arguments.Add(numbered ? "--numbered" : "--no-numbered"); }
                    arguments.Add(options.IncludeBinary ? "--binary" : "--no-binary");
                    if (from is null) { arguments.AddRange(["--root", to]); }
                    else { arguments.Add(from + ".." + to); }
                    arguments.Add("--");
                    int before = Directory.GetFiles(temporary).Length;
                    await RunExportAsync(arguments, token).ConfigureAwait(false);
                    number = checked(number + Directory.GetFiles(temporary).Length - before);
                }

                string[] generated = Directory.GetFiles(temporary).OrderBy(file => System.IO.Path.GetFileName(file), StringComparer.Ordinal).ToArray();
                if (generated.Length == 0) { throw new InvalidOperationException("The selected revisions produced no mailbox patches."); }
                string[] destinations = generated.Select(file => System.IO.Path.Combine(output, System.IO.Path.GetFileName(file))).ToArray();
                foreach (string destination in destinations) { EnsureExportAbsent(destination); }
                token.ThrowIfCancellationRequested();
                for (int index = 0; index < generated.Length; index++) { File.Move(generated[index], destinations[index], overwrite: false); }
                return new GitFormatPatchResult(output, destinations);
            }
            finally { Directory.Delete(temporary, recursive: true); }
        }, cancellationToken);
    }

    private async Task<GitResult> RunExportAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool literalPaths = false)
    {
        GitResult result = await _runner.RunAsync(Path, [.. GlobalOptions, .. literalPaths ? new[] { "--literal-pathspecs" } : [], .. arguments], cancellationToken).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(arguments, result); }
        return result;
    }

    private static string ExportOutputPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0')) { throw new ArgumentException("An output path cannot contain NUL.", nameof(path)); }
        return System.IO.Path.GetFullPath(path);
    }

    private static void EnsureExportAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) { throw new IOException("The export destination already exists and will not be overwritten: " + path); }
    }

    private static string ArchivePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Contains('\0') || prefix.StartsWith('/') || prefix.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException("An archive prefix must be a relative directory without parent traversal or NUL.", nameof(prefix));
        }
        return prefix.Length == 0 ? "" : prefix.TrimEnd('/') + "/";
    }
}
