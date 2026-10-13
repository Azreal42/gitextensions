using System.Globalization;
using System.Text;

namespace GitExtensions.Linux;

public sealed record GitRevertOptions(bool AutoCommit = false, int? MainlineParent = null);
public sealed record GitRevertRevision(GitCommit Commit, IReadOnlyList<GitCommit> Parents);
public sealed record GitRevertResult(GitResult CommandResult, GitRevertRevision Revision, IReadOnlyList<string> ConflictedPaths)
{
    public bool Success => CommandResult.Success;
}

public sealed partial class GitRepository
{
    public async Task<GitRevertRevision> GetRevertRevisionAsync(string revision, CancellationToken cancellationToken = default)
    {
        string hash = await ResolveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        GitCommit commit = (await GetLogAsync(1, hash, cancellationToken, all: false).ConfigureAwait(false)).Single();
        List<GitCommit> parents = [];
        foreach (string parent in commit.Parents)
        { parents.Add((await GetLogAsync(1, parent, cancellationToken, all: false).ConfigureAwait(false)).Single()); }
        return new(commit, parents);
    }

    /// <summary>Freeze all hashes before the first revert; the caller supplies the grid's ascending order.</summary>
    public async Task<IReadOnlyList<GitRevertRevision>> PrepareRevertSelectionAsync(IEnumerable<string> revisions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        string[] selection = revisions.ToArray();
        if (selection.Length == 0) { throw new ArgumentException("Select at least one commit.", nameof(revisions)); }
        await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
        List<GitRevertRevision> result = [];
        foreach (string revision in selection) { result.Add(await GetRevertRevisionAsync(revision, cancellationToken).ConfigureAwait(false)); }
        return result;
    }

    public Task<GitRevertResult> RevertAsync(string revision, GitRevertOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return WithMutationAsync<GitRevertResult>(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            GitRevertRevision selected = await GetRevertRevisionAsync(revision, token).ConfigureAwait(false);
            if (selected.Parents.Count > 1)
            {
                if (options.MainlineParent is not int parent || parent < 1 || parent > selected.Parents.Count)
                { throw new ArgumentException("Select an existing parent of the merge commit.", nameof(options)); }
            }
            else if (options.MainlineParent is not null)
            { throw new ArgumentException("A parent can only be selected for a merge commit.", nameof(options)); }
            if ((await GetStatusAsync(token).ConfigureAwait(false)).Changes.Any(change => change.IsConflict))
            { throw new InvalidOperationException("Resolve the existing conflicts before reverting another commit."); }

            Encoding encoding = await GetCommitMessageEncodingAsync(token).ConfigureAwait(false);
            string mergePath = await GetGitPathAsync("MERGE_MSG", token).ConfigureAwait(false);
            string draftPath = await GetGitPathAsync("COMMITMESSAGE", token).ConfigureAwait(false);
            string existing = await ReadRevertDraftAsync(mergePath, draftPath, encoding, token).ConfigureAwait(false);
            List<string> arguments = ["revert", "--no-edit"];
            if (!options.AutoCommit) { arguments.Add("--no-commit"); }
            if (options.MainlineParent is int mainline) { arguments.AddRange(["-m", mainline.ToString(CultureInfo.InvariantCulture)]); }
            arguments.AddRange(["--", selected.Commit.Hash]);
            GitResult result;
            try
            {
                result = await ExecuteAsync(arguments, token, allowedExitCodes: [1],
                    environment: new Dictionary<string, string?> { ["GIT_EDITOR"] = "true" }).ConfigureAwait(false);
            }
            catch (GitCommandException error)
            {
                // Git can return 128 for prepare-commit-msg rejection after applying the
                // reverse changes. FormRevertCommit still preserves the prior draft then.
                result = error.Result;
            }
            if (!string.IsNullOrWhiteSpace(existing))
            {
                // FormRevertCommit preserves the previously edited message, even across a failed revert.
                string generated = await ReadRevertDraftAsync(mergePath, draftPath, encoding, token).ConfigureAwait(false);
                byte[] bytes = encoding.GetBytes(existing + "\n\n" + generated);
                token.ThrowIfCancellationRequested();
                string temporary = mergePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temporary, mergePath, overwrite: true); }
                finally { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
            }
            string[] conflicts = (await GetStatusAsync(token).ConfigureAwait(false)).Changes.Where(change => change.IsConflict).Select(change => change.Path).ToArray();
            if (!result.Success && conflicts.Length == 0) { throw new GitCommandException(arguments, result); }
            return new(result, selected, conflicts);
        }, cancellationToken);
    }

    // The legacy public wrapper keeps automatic commit and throwing-on-failure semantics.
    private async Task<GitResult> RevertCommittedAsync(string revision, CancellationToken cancellationToken)
    {
        GitRevertResult result = await RevertAsync(revision, new GitRevertOptions(AutoCommit: true), cancellationToken).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(["revert", "--no-edit", "--", result.Revision.Commit.Hash], result.CommandResult); }
        return result.CommandResult;
    }

    private static async Task<string> ReadRevertDraftAsync(string mergePath, string draftPath, Encoding encoding, CancellationToken token)
    {
        string path = File.Exists(mergePath) ? mergePath : draftPath;
        if (!File.Exists(path)) { return ""; }
        byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        int offset = encoding.CodePage == Encoding.UTF8.CodePage && bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }
}
