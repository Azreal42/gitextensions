namespace GitExtensions.Linux;

/// <summary>A file and its own endpoint, independent of the comparison form's selectors.</summary>
public sealed record GitFileComparisonSide(GitCompareRevision Revision, string Path);

public sealed partial class GitRepository
{
    public async Task<IReadOnlyList<string>> GetCustomDiffToolsAsync(CancellationToken cancellationToken = default)
        => (await GetConfigAsync("^difftool\\..*\\.cmd$", cancellationToken: cancellationToken).ConfigureAwait(false))
            .Select(entry => entry.Key[9..^4]).Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Compare independent files: existing worktree files stay live/editable; Commit and Index inputs are private copies.</summary>
    public Task<GitResult> OpenFileDiffToolAsync(GitFileComparisonSide first, GitFileComparisonSide second,
        string? customTool = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return WithMutationAsync(async token =>
        {
            string firstPath = ValidatePath(first.Path), secondPath = ValidatePath(second.Path);
            FileDiffToolInput before = await PrepareFileDiffToolInputAsync(first.Revision, firstPath, token).ConfigureAwait(false);
            FileDiffToolInput after = await PrepareFileDiffToolInputAsync(second.Revision, secondPath, token).ConfigureAwait(false);
            return await OpenFileDiffToolInputsAsync(before, after, customTool, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private sealed record FileDiffToolInput(string Path, byte[]? Snapshot, string? LivePath = null);

    private async Task<FileDiffToolInput> PrepareFileDiffToolInputAsync(GitCompareRevision revision, string path, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.Kind != GitCompareRevisionKind.WorkingTree)
        { return new(path, await GetComparisonFileBytesAsync(revision, path, token).ConfigureAwait(false)); }
        await EnsureWorktreeAsync(token).ConfigureAwait(false);
        return new(path, null, ValidateFileDiffToolWorkingPath(path));
    }

    private string ValidateFileDiffToolWorkingPath(string path)
    {
        ValidateWorktreeFileLocation(path);
        string full = System.IO.Path.Combine(Path, path);
        if (new FileInfo(full).LinkTarget is not null || Directory.Exists(full))
        { throw new InvalidOperationException("A live diff-tool input must be a file, not a symbolic link or directory: " + path); }
        if (!File.Exists(full)) { throw new FileNotFoundException("The selected working-tree file does not exist; no replacement is created.", full); }
        return full;
    }

    private Task<GitResult> OpenFileDiffToolCopiesAsync(string firstPath, byte[] before, string secondPath, byte[] after,
        string? customTool, CancellationToken token)
        => OpenFileDiffToolInputsAsync(new(firstPath, before), new(secondPath, after), customTool, token);

    private async Task<GitResult> OpenFileDiffToolInputsAsync(FileDiffToolInput before, FileDiffToolInput after,
        string? customTool, CancellationToken token)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-file-diff-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
        else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        try
        {
            async Task<string> SideAsync(string name, FileDiffToolInput input)
            {
                if (input.LivePath is not null) { return input.LivePath; }
                string side = System.IO.Path.Combine(directory, name);
                Directory.CreateDirectory(side);
                string file = System.IO.Path.Combine(side, System.IO.Path.GetFileName(input.Path));
                await File.WriteAllBytesAsync(file, input.Snapshot!, token).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                return file;
            }
            string left = await SideAsync("A", before).ConfigureAwait(false), right = await SideAsync("B", after).ConfigureAwait(false);
            foreach (FileDiffToolInput input in new[] { before, after })
            { if (input.LivePath is not null) { ValidateFileDiffToolWorkingPath(input.Path); } }
            List<string> arguments = ["difftool", "--no-prompt", "--no-index"];
            AddCustomDiffTool(arguments, customTool);
            arguments.AddRange(["--", left, right]);
            // --no-index uses 1 to report differing files, including with a successful tool.
            return await ExecuteAsync(arguments, token, allowedExitCodes: [1]).ConfigureAwait(false);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AddCustomDiffTool(List<string> arguments, string? customTool)
    {
        if (customTool is null) { arguments.Add("--gui"); return; }
        ArgumentException.ThrowIfNullOrWhiteSpace(customTool);
        if (customTool.Contains('\0')) { throw new ArgumentException("Diff tool names cannot contain NUL.", nameof(customTool)); }
        arguments.Add("--tool=" + customTool);
    }
}
