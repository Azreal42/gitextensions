namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Starts the history Edit/Reword workflow against the selected ancestor, preserving Git's native todo graph.</summary>
    public Task<GitResult> RebaseCommitAsync(string revision, GitRebaseAction action, string editorExecutable,
        CancellationToken cancellationToken = default)
    {
        if (action is not (GitRebaseAction.Edit or GitRebaseAction.Reword)) { throw new ArgumentOutOfRangeException(nameof(action)); }
        GitRebaseEditor.ValidateExecutable(editorExecutable);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            using RebaseLease lease = await AcquireRebaseLeaseAsync(token).ConfigureAwait(false);
            if (HasRebaseBackend(lease))
            { throw new InvalidOperationException("A rebase is already in progress. Continue, skip or abort it first."); }
            await CleanupObsoleteRebaseJournalsAsync(lease).ConfigureAwait(false);
            string target = await ResolveRevisionAsync(revision, token).ConfigureAwait(false);
            GitResult ancestry = await ExecuteAsync(["merge-base", "--is-ancestor", target, "HEAD"], token, allowedExitCodes: [1]).ConfigureAwait(false);
            if (!ancestry.Success)
            { throw new InvalidOperationException("Select a commit belonging to the checked-out branch before editing its history."); }
            GitCommit commit = (await GetLogAsync(1, target, token).ConfigureAwait(false)).Single();
            GitRebaseCapabilities capabilities = await GetRebaseCapabilitiesAsync(token).ConfigureAwait(false);
            if (commit.Parents.Count > 1 && !capabilities.SupportRebaseMerges)
            { throw new NotSupportedException("Editing a merge commit requires Git with rebase --rebase-merges support."); }
            string? upstream = commit.Parents.FirstOrDefault();
            GitRebaseOptions options = new()
            {
                BranchName = upstream ?? target, Interactive = true, AutoStash = true,
                PreserveMerges = capabilities.SupportRebaseMerges, AutoSquash = false
            };
            System.Text.Encoding encoding = await GetCommitMessageEncodingAsync(token).ConfigureAwait(false);
            string directory = await GetGitPathAsync(GitRebaseEditor.NativeStateDirectoryName, token).ConfigureAwait(false);
            if (Directory.Exists(directory) || Directory.Exists(await GetGitPathAsync("gitextensions-rebase", token).ConfigureAwait(false)))
            { throw new InvalidOperationException("An existing rebase editor journal must be recovered before editing another commit."); }
            GitRepositoryInfo info = await GetInfoAsync(token).ConfigureAwait(false);
            NativeRebaseEditorState state = new(info.RootPath, info.GitDirectory, editorExecutable, encoding.WebName, [], options)
            { TodoEncodingName = "utf-8" };
            Dictionary<string, string?> environment = GitRebaseEditor.NativeEnvironmentFor(directory, state).ToDictionary(pair => pair.Key, pair => pair.Value);
            environment["GIT_SEQUENCE_EDITOR"] = GitRebaseEditor.TargetedCommitEditorCommand(editorExecutable, target, action);
            token.ThrowIfCancellationRequested();
            CreatePrivateRebaseDirectory(directory);
            try
            {
                GitRebaseEditor.WriteNativeState(directory, state);
                List<string> arguments = ["rebase", "--interactive", "--no-autosquash", "--keep-empty", "--reapply-cherry-picks", "--autostash"];
                if (capabilities.SupportRebaseMerges) { arguments.Add("--rebase-merges"); }
                if (upstream is null) { arguments.Add("--root"); }
                else { arguments.AddRange(["--", upstream]); }
                return await ExecuteAsync(["-c", "i18n.commitEncoding=" + state.EncodingName, "-c", "i18n.logOutputEncoding=UTF-8", .. arguments], token,
                    environment: environment,
                    outputDecoder: GitRebaseOutputDecoder.ForEncoding(state.EncodingName, "utf-8", "utf-8")).ConfigureAwait(false);
            }
            finally { await CleanupRebaseStateAsync(directory, lease, createdByThisCommand: true).ConfigureAwait(false); }
        }, cancellationToken);
    }
}
