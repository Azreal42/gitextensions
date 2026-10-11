using System.Globalization;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public static async Task<GitPatchFileKind> GetPatchFileKindAsync(string patchFile, CancellationToken cancellationToken = default)
    {
        string file = ExistingPatchFile(patchFile);
        using StreamReader reader = new(file);
        string? firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return firstLine is not null && (firstLine.StartsWith("diff ", StringComparison.Ordinal) || firstLine.StartsWith("Index: ", StringComparison.Ordinal))
            ? GitPatchFileKind.Diff : GitPatchFileKind.Mailbox;
    }

    public Task<GitResult> ApplyDiffPatchAsync(string patchFile, GitApplyPatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        string file = ExistingPatchFile(patchFile);
        options ??= new();
        if (!Enum.IsDefined(options.Target) || !Enum.IsDefined(options.Whitespace)) { throw new ArgumentOutOfRangeException(nameof(options)); }
        if (options.StripCount is < 0 or > 255) { throw new ArgumentOutOfRangeException(nameof(options), "StripCount must be between 0 and 255."); }
        string whitespace = options.Whitespace == GitPatchWhitespace.ErrorAll ? "error-all" : options.Whitespace.ToString().ToLowerInvariant();
        List<string> arguments = ["apply", "--whitespace=" + whitespace, "-p" + options.StripCount.ToString(CultureInfo.InvariantCulture)];
        if (options.IgnoreWhitespace) { arguments.Add("--ignore-whitespace"); }
        if (options.Reverse) { arguments.Add("--reverse"); }
        if (options.CheckOnly) { arguments.Add("--check"); }
        if (options.ThreeWay) { arguments.Add("--3way"); }
        if (options.Target == GitPatchTarget.Index) { arguments.Add("--cached"); }
        if (options.Target == GitPatchTarget.IndexAndWorkingTree) { arguments.Add("--index"); }
        if (options.Directory is not null) { arguments.Add("--directory=" + ValidatePath(options.Directory)); }
        arguments.AddRange(["--", file]);
        return MutateAsync(arguments, cancellationToken);
    }

    public Task<GitResult> ApplyMailboxPatchesAsync(IEnumerable<string> patchFiles, GitAmOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patchFiles);
        string[] files = patchFiles.Select(ExistingPatchFile).ToArray();
        if (files.Length == 0) { throw new ArgumentException("Choose at least one mailbox patch.", nameof(patchFiles)); }
        options ??= new();
        List<string> arguments = ["am", options.ThreeWay ? "--3way" : "--no-3way", options.KeepCr ? "--keep-cr" : "--no-keep-cr"];
        if (options.SignOff) { arguments.Add("--signoff"); }
        if (options.IgnoreWhitespace) { arguments.Add("--ignore-whitespace"); }
        if (options.KeepSubject) { arguments.Add("--keep"); }
        if (options.CommitterDateIsAuthorDate) { arguments.Add("--committer-date-is-author-date"); }
        arguments.AddRange(["--", .. files]);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            if ((await ReadAmStateAsync(token).ConfigureAwait(false)).IsActive) { throw new InvalidOperationException("A mailbox application is already active. Continue, skip or abort it before starting another."); }
            return await ExecuteAsync(arguments, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Applies the directory's files in ordinal filename order, retaining the original dialog's mailbox-directory mode.</summary>
    public Task<GitResult> ApplyPatchDirectoryAsync(string directory, GitAmOptions? options = null, CancellationToken cancellationToken = default)
    {
        string fullPath = ExportOutputPath(directory);
        if (!Directory.Exists(fullPath)) { throw new DirectoryNotFoundException(fullPath); }
        return ApplyMailboxPatchesAsync(Directory.GetFiles(fullPath).OrderBy(file => System.IO.Path.GetFileName(file), StringComparer.Ordinal), options, cancellationToken);
    }

    public Task<GitAmState> GetAmStateAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(ReadAmStateAsync, cancellationToken);

    public Task<GitResult> ManageAmAsync(GitOperationAction action, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(action)) { throw new ArgumentOutOfRangeException(nameof(action)); }
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            if (!(await ReadAmStateAsync(token).ConfigureAwait(false)).IsActive) { throw new InvalidOperationException("There is no active mailbox application to continue, skip or abort."); }
            return await ExecuteAsync(["am", "--" + action.ToString().ToLowerInvariant()], token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<GitAmState> ReadAmStateAsync(CancellationToken cancellationToken)
    {
        await GetInfoAsync(cancellationToken).ConfigureAwait(false);
        string directory = await GetGitPathAsync("rebase-apply", cancellationToken).ConfigureAwait(false);
        if (!File.Exists(System.IO.Path.Combine(directory, "applying"))) { return new GitAmState(false, null, null, null, null, [], null); }
        int? current = await NumberAsync("next").ConfigureAwait(false);
        int? last = await NumberAsync("last").ConfigureAwait(false);
        GitResult original = await ExecuteAsync(["rev-parse", "--verify", "ORIG_HEAD"], cancellationToken, allowedExitCodes: [1, 128]).ConfigureAwait(false);
        GitResult patch = await ExecuteAsync(["am", "--show-current-patch=diff"], cancellationToken).ConfigureAwait(false);
        string messageFile = System.IO.Path.Combine(directory, "final-commit");
        string? subject = File.Exists(messageFile) ? (await File.ReadAllTextAsync(messageFile, cancellationToken).ConfigureAwait(false)).Split('\n')[0].TrimEnd('\r') : null;
        string[] conflicts = (await GetStatusAsync(cancellationToken).ConfigureAwait(false)).Changes.Where(change => change.IsConflict).Select(change => change.Path).ToArray();
        return new GitAmState(true, current, last, subject, original.Success ? ReadOutputLine(original.StandardOutput) : null, conflicts, patch.StandardOutput);

        async Task<int?> NumberAsync(string name)
        {
            string file = System.IO.Path.Combine(directory, name);
            if (!File.Exists(file)) { return null; }
            return int.TryParse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;
        }
    }

    private static string ExistingPatchFile(string path)
    {
        string file = ExportOutputPath(path);
        if (!File.Exists(file)) { throw new FileNotFoundException("The patch file does not exist.", file); }
        return file;
    }
}
