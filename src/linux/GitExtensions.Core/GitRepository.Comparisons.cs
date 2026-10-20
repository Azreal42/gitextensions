using System.Globalization;
using System.Text;
using GitCommands.Settings;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>The actual parent pair for Browse's artificial rows, including a genuinely unborn index.</summary>
    public async Task<GitComparison> GetArtificialRevisionComparisonAsync(GitCompareRevision source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Kind is not (GitCompareRevisionKind.Index or GitCompareRevisionKind.WorkingTree))
        { throw new ArgumentException("Choose the index or working-tree endpoint.", nameof(source)); }
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        GitCompareRevision parent;
        if (source.Kind == GitCompareRevisionKind.WorkingTree) { parent = new("", GitCompareRevisionKind.Index); }
        else
        {
            string? head = await ReadFileHistoryHeadAsync(cancellationToken).ConfigureAwait(false);
            parent = head is null ? new("", GitCompareRevisionKind.EmptyTree) : new(head);
        }
        return await GetComparisonAsync(parent, source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitComparison> GetComparisonAsync(GitCompareRevision from, GitCompareRevision to, CancellationToken cancellationToken = default)
    {
        GitCompareRevision first = await ResolveComparisonRevisionAsync(from, cancellationToken).ConfigureAwait(false);
        GitCompareRevision second = await ResolveComparisonRevisionAsync(to, cancellationToken).ConfigureAwait(false);
        if (first == second) { return new(first, second, [], []); }
        if (first.Kind == GitCompareRevisionKind.Commit && second.Kind == GitCompareRevisionKind.Commit)
        {
            GitRevisionChanges changes = await GetRevisionChangesAsync(second.Revision, first.Revision, cancellationToken).ConfigureAwait(false);
            return new(first, second, changes.Files, []);
        }
        List<string> arguments = ["--no-optional-locks", "--literal-pathspecs", "diff", "--name-status", "-z", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv"];
        AddComparisonRevisions(arguments, first, second);
        arguments.Add("--");
        List<GitRevisionFile> files = [.. GitRevisionFileParser.ParseNameStatus((await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false)).StandardOutput)];
        List<string> untracked = [];
        if (first.Kind == GitCompareRevisionKind.WorkingTree || second.Kind == GitCompareRevisionKind.WorkingTree)
        {
            string[] paths = (await ExecuteAsync(["--no-optional-locks", "ls-files", "--others", "--exclude-standard", "-z"], cancellationToken).ConfigureAwait(false)).StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (string path in paths.Where(path => !files.Any(file => file.Path == path || file.OriginalPath == path)))
            {
                files.Add(new(path, second.Kind == GitCompareRevisionKind.WorkingTree ? 'A' : 'D'));
                untracked.Add(path);
            }
        }
        return new(first, second, files, untracked);
    }

    public async Task<string?> GetComparisonMergeBaseAsync(GitCompareRevision from, GitCompareRevision to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (!Enum.IsDefined(from.Kind) || !Enum.IsDefined(to.Kind)) { throw new ArgumentOutOfRangeException(nameof(from)); }
        if (from.Kind == GitCompareRevisionKind.EmptyTree || to.Kind == GitCompareRevisionKind.EmptyTree) { return null; }
        GitStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        string? first = from.Kind == GitCompareRevisionKind.Commit ? await ResolveCachedReadRevisionAsync(from.Revision, cancellationToken).ConfigureAwait(false) : status.Head;
        string? second = to.Kind == GitCompareRevisionKind.Commit ? await ResolveCachedReadRevisionAsync(to.Revision, cancellationToken).ConfigureAwait(false) : status.Head;
        if (first is null || second is null || first == second) { return null; }
        GitResult result = await RunCachedAsync(GitCachedReadKind.Graph, ["merge-base", first, second], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
        return result.Success ? ReadOutputLine(result.StandardOutput) : null;
    }

    public async Task<GitComparisonCommitCounts?> GetComparisonCommitCountsAsync(GitCompareRevision from, GitCompareRevision to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (!Enum.IsDefined(from.Kind) || !Enum.IsDefined(to.Kind)) { throw new ArgumentOutOfRangeException(nameof(from)); }
        if (from.Kind == GitCompareRevisionKind.EmptyTree || to.Kind == GitCompareRevisionKind.EmptyTree) { return null; }
        GitStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Head is null && (from.Kind != GitCompareRevisionKind.Commit || to.Kind != GitCompareRevisionKind.Commit)) { return null; }
        string first = await ResolveCachedReadRevisionAsync(from.Kind == GitCompareRevisionKind.Commit ? from.Revision : "HEAD", cancellationToken).ConfigureAwait(false);
        string second = await ResolveCachedReadRevisionAsync(to.Kind == GitCompareRevisionKind.Commit ? to.Revision : "HEAD", cancellationToken).ConfigureAwait(false);
        string[] fields = (await RunCachedAsync(GitCachedReadKind.Graph, ["rev-list", "--left-right", "--count", first + "..." + second, "--"], cancellationToken).ConfigureAwait(false)).StandardOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return new(int.Parse(fields[0], CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture));
    }

    public async Task<string> GetComparisonFileDiffAsync(GitComparison comparison, GitRevisionFile file,
        GitComparisonDiffOptions? options = null, CancellationToken cancellationToken = default)
        => await ExecuteDiffReadAsync(await BuildComparisonFileDiffArgumentsAsync(comparison, file, options, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<GitDiffReadCommand> BuildComparisonFileDiffArgumentsAsync(GitComparison comparison, GitRevisionFile file,
        GitComparisonDiffOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(file);
        options ??= new();
        ValidateComparisonOptions(options);
        string path = ValidatePath(file.Path);
        if (comparison.UntrackedPaths.Contains(path, StringComparer.Ordinal))
        {
            await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
            List<string> untracked = ["diff", "--no-color", "--no-index", "--no-ext-diff", "--no-textconv"];
            AddComparisonOptions(untracked, options);
            string fullPath = System.IO.Path.Combine(Path, path);
            untracked.AddRange(comparison.Compare.Kind == GitCompareRevisionKind.WorkingTree ? ["--", "/dev/null", fullPath] : ["--", fullPath, "/dev/null"]);
            return new(["--literal-pathspecs", .. untracked], [1]);
        }
        if (comparison.Base.Kind == GitCompareRevisionKind.Commit && comparison.Compare.Kind == GitCompareRevisionKind.Commit && options == new GitComparisonDiffOptions())
        {
            return await BuildRevisionFileDiffArgumentsAsync(new(comparison.Compare.Revision, comparison.Base.Revision, comparison.Files), file, options, cancellationToken).ConfigureAwait(false);
        }
        GitCompareRevision first = await ResolveComparisonRevisionAsync(comparison.Base, cancellationToken).ConfigureAwait(false);
        GitCompareRevision second = await ResolveComparisonRevisionAsync(comparison.Compare, cancellationToken).ConfigureAwait(false);
        if (first == second) { return new([]); }
        List<string> arguments = ["--no-optional-locks", "--literal-pathspecs", "diff", "--no-color", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv"];
        AddComparisonOptions(arguments, options);
        AddComparisonRevisions(arguments, first, second);
        arguments.Add("--");
        if (file.OriginalPath is not null) { arguments.Add(ValidatePath(file.OriginalPath)); }
        arguments.Add(path);
        return new(arguments, CacheKind: (first.Kind is GitCompareRevisionKind.Commit or GitCompareRevisionKind.EmptyTree)
            && (second.Kind is GitCompareRevisionKind.Commit or GitCompareRevisionKind.EmptyTree) ? GitCachedReadKind.Diff : null);
    }

    public async Task<byte[]> GetComparisonFileBytesAsync(GitCompareRevision revision, string path, CancellationToken cancellationToken = default)
    {
        GitCompareRevision selected = await ResolveComparisonRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        path = ValidatePath(path);
        if (selected.Kind == GitCompareRevisionKind.EmptyTree) { return []; }
        if (selected.Kind == GitCompareRevisionKind.Commit)
        {
            GitTreeEntry? entry = (await GetTreeAsync(selected.Revision, path, cancellationToken).ConfigureAwait(false)).FirstOrDefault(entry => entry.Path == path);
            return entry?.Type == "commit" ? Encoding.UTF8.GetBytes("Submodule commit " + entry.Hash + "\n")
                : await GetFileBytesAsync(path, selected.Revision, cancellationToken).ConfigureAwait(false);
        }
        if (selected.Kind == GitCompareRevisionKind.Index)
        {
            string output = (await ExecuteAsync(["--literal-pathspecs", "ls-files", "--stage", "-z", "--", path], cancellationToken).ConfigureAwait(false)).StandardOutput;
            foreach (string item in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = item.IndexOf('\t');
                if (tab < 0 || item[(tab + 1)..] != path) { continue; }
                string[] fields = item[..tab].Split(' ');
                if (fields is ["160000", string hash, "0"]) { return Encoding.UTF8.GetBytes("Submodule commit " + hash + "\n"); }
            }
            return await GetIndexFileBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        string fullPath = System.IO.Path.Combine(Path, path);
        FileInfo info = new(fullPath);
        if (info.LinkTarget is string link) { return Encoding.UTF8.GetBytes(link); }
        if (Directory.Exists(fullPath))
        {
            GitResult submodule = await _runner.RunAsync(fullPath, ["rev-parse", "--verify", "HEAD"], cancellationToken).ConfigureAwait(false);
            if (!submodule.Success) { throw new GitCommandException(["rev-parse", "--verify", "HEAD"], submodule); }
            return Encoding.UTF8.GetBytes("Submodule commit " + ReadOutputLine(submodule.StandardOutput) + "\n");
        }
        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitResult> OpenComparisonDiffToolAsync(GitComparison comparison, GitRevisionFile? file = null,
        bool directory = false, CancellationToken cancellationToken = default)
        => await OpenComparisonDiffToolAsync(comparison, file, directory, null, cancellationToken).ConfigureAwait(false);

    public async Task<GitResult> OpenComparisonDiffToolAsync(GitComparison comparison, GitRevisionFile? file,
        bool directory, string? customTool, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (directory && comparison.Base.Kind == GitCompareRevisionKind.WorkingTree)
        { throw new InvalidOperationException("Directory diff is unavailable when BASE is the working directory."); }
        if (!directory && file is null) { throw new ArgumentException("Select a file for the diff tool.", nameof(file)); }
        return await WithMutationAsync(async token =>
        {
            if (directory || comparison.Base.Kind != GitCompareRevisionKind.Commit || comparison.Compare.Kind != GitCompareRevisionKind.Commit)
            { await EnsureWorktreeAsync(token).ConfigureAwait(false); }
            GitCompareRevision first = await ResolveComparisonRevisionAsync(comparison.Base, token).ConfigureAwait(false);
            GitCompareRevision second = await ResolveComparisonRevisionAsync(comparison.Compare, token).ConfigureAwait(false);
            if (!directory && (await GetInfoAsync(token).ConfigureAwait(false)).IsBare)
            {
                string firstPath = ValidatePath(file!.OriginalPath ?? file.Path), secondPath = ValidatePath(file.Path);
                byte[] before = file.Status == 'A' ? [] : await GetComparisonFileBytesAsync(first, firstPath, token).ConfigureAwait(false);
                byte[] after = file.Status == 'D' ? [] : await GetComparisonFileBytesAsync(second, secondPath, token).ConfigureAwait(false);
                return await OpenFileDiffToolCopiesAsync(firstPath, before, secondPath, after, customTool, token).ConfigureAwait(false);
            }
            List<string> arguments = ["--literal-pathspecs", "difftool", "--no-prompt", "--find-renames"];
            AddCustomDiffTool(arguments, customTool);
            if (directory) { arguments.Add("--dir-diff"); }
            AddComparisonRevisions(arguments, first, second);
            arguments.Add("--");
            if (file is not null)
            {
                if (file.OriginalPath is not null) { arguments.Add(ValidatePath(file.OriginalPath)); }
                arguments.Add(ValidatePath(file.Path));
            }
            return await ExecuteAsync(arguments, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitCompareRevision> ResolveComparisonRevisionAsync(GitCompareRevision revision, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (!Enum.IsDefined(revision.Kind)) { throw new ArgumentOutOfRangeException(nameof(revision)); }
        token.ThrowIfCancellationRequested();
        if (revision.Kind == GitCompareRevisionKind.EmptyTree)
        {
            // No -w: Git computes the repository-format tree identity without writing an object.
            string empty = ReadOutputLine((await ExecuteAsync(["--no-optional-locks", "hash-object", "-t", "tree", "--stdin"],
                token, standardInput: "").ConfigureAwait(false)).StandardOutput);
            return revision with { Revision = empty };
        }
        if (revision.Kind == GitCompareRevisionKind.Commit) { return revision with { Revision = await ResolveCachedReadRevisionAsync(revision.Revision, token).ConfigureAwait(false) }; }
        await EnsureWorktreeAsync(token).ConfigureAwait(false);
        return revision with { Revision = "" };
    }

    private static void AddComparisonRevisions(List<string> arguments, GitCompareRevision from, GitCompareRevision to)
    {
        bool firstTree = from.Kind is GitCompareRevisionKind.Commit or GitCompareRevisionKind.EmptyTree;
        bool secondTree = to.Kind is GitCompareRevisionKind.Commit or GitCompareRevisionKind.EmptyTree;
        if (firstTree && secondTree) { arguments.AddRange([from.Revision, to.Revision]); }
        else if (firstTree) { if (to.Kind == GitCompareRevisionKind.Index) { arguments.Add("--cached"); } arguments.Add(from.Revision); }
        else if (secondTree) { arguments.Add("-R"); if (from.Kind == GitCompareRevisionKind.Index) { arguments.Add("--cached"); } arguments.Add(to.Revision); }
        else if (from.Kind == GitCompareRevisionKind.WorkingTree) { arguments.Add("-R"); }
    }

    private static void ValidateComparisonOptions(GitComparisonDiffOptions options)
    {
        if (!Enum.IsDefined(options.IgnoreWhitespace)) { throw new ArgumentOutOfRangeException(nameof(options)); }
        if (options.ContextLines is < 0 or > 100000) { throw new ArgumentOutOfRangeException(nameof(options)); }
    }

    private static void AddComparisonOptions(List<string> arguments, GitComparisonDiffOptions options)
    {
        ValidateComparisonOptions(options);
        if (options.UseHistogram) { arguments.Add("--histogram"); }
        switch (options.IgnoreWhitespace)
        {
            case IgnoreWhitespaceKind.Eol: arguments.Add("--ignore-space-at-eol"); break;
            case IgnoreWhitespaceKind.Change: arguments.Add("--ignore-space-change"); break;
            case IgnoreWhitespaceKind.AllSpace: arguments.Add("--ignore-all-space"); break;
        }
        arguments.Add("--unified=" + (options.ShowEntireFile ? 9000 : options.ContextLines).ToString(CultureInfo.InvariantCulture));
        if (options.ShowEntireFile) { arguments.Add("--inter-hunk-context=9000"); }
        if (options.TreatAllFilesAsText) { arguments.Add("--text"); }
    }

}
