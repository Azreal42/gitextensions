using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public async Task<GitRebaseCapabilities> GetRebaseCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        Version version = await GetRangeDiffGitVersionAsync(cancellationToken).ConfigureAwait(false);
        async Task<bool> Config(string name)
            => (await ExecuteAsync(["config", "--bool", "--get", name], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false)).StandardOutput.Trim() == "true";
        return new(version, version >= new Version(2, 19), version >= new Version(2, 38),
            await Config("rebase.autosquash").ConfigureAwait(false), await Config("rebase.autostash").ConfigureAwait(false),
            await Config("rebase.updaterefs").ConfigureAwait(false));
    }

    public Task<GitResult> RebaseAsync(GitRebaseOptions options, string? editorExecutable = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            using RebaseLease lease = await AcquireRebaseLeaseAsync(token).ConfigureAwait(false);
            if (HasRebaseBackend(lease))
            { throw new InvalidOperationException("A rebase is already in progress. Continue, skip or abort it first."); }
            await CleanupObsoleteRebaseJournalsAsync(lease).ConfigureAwait(false);
            if (options.From is null ^ options.OnTo is null)
            { throw new ArgumentException("From and OnTo must both have values, or neither may.", nameof(options)); }
            string branch = ValidateValue(options.BranchName);
            if (editorExecutable is not null) { GitRebaseEditor.ValidateExecutable(editorExecutable); }
            Encoding encoding = await GetCommitMessageEncodingAsync(token).ConfigureAwait(false);
            // Resolve only to validate. Keep the branch name in argv: --onto may switch and rewrite that branch.
            await ResolveRevisionAsync(branch, token).ConfigureAwait(false);
            if (options.From is not null)
            {
                await ResolveRevisionAsync(ValidateValue(options.From), token).ConfigureAwait(false);
                await ResolveRevisionAsync(ValidateValue(options.OnTo!), token).ConfigureAwait(false);
            }
            GitRebaseCapabilities capabilities = await GetRebaseCapabilitiesAsync(token).ConfigureAwait(false);
            if (options.UpdateRefs is not null && !capabilities.SupportUpdateRefs)
            { throw new NotSupportedException("This Git version does not support rebase --update-refs."); }
            if ((await GetRebaseStateAsync(token).ConfigureAwait(false)).IsActive)
            { throw new InvalidOperationException("A rebase is already in progress. Continue, skip or abort it first."); }
            if (Directory.Exists(await GetGitPathAsync("gitextensions-rebase", token).ConfigureAwait(false)))
            { throw new InvalidOperationException("An existing interactive plan must be continued or aborted first."); }
            string directory = await GetGitPathAsync(GitRebaseEditor.NativeStateDirectoryName, token).ConfigureAwait(false);
            if (Directory.Exists(directory))
            { throw new InvalidOperationException("Stale native rebase editor state exists. Reload and complete its recovery before starting another rebase."); }
            GitRepositoryInfo info = await GetInfoAsync(token).ConfigureAwait(false);
            NativeRebaseEditorState state = new(info.RootPath, info.GitDirectory, editorExecutable ?? "", encoding.WebName, [], options) { TodoEncodingName = "utf-8" };
            token.ThrowIfCancellationRequested();
            CreatePrivateRebaseDirectory(directory);
            try
            {
                GitRebaseEditor.WriteNativeState(directory, state);
                List<string> arguments = ["rebase"];
                if (options.IgnoreDate) { arguments.Add("--ignore-date"); }
                else if (options.CommitterDateIsAuthorDate) { arguments.Add("--committer-date-is-author-date"); }
                else
                {
                    if (options.Interactive) { arguments.AddRange(["--interactive", options.AutoSquash ? "--autosquash" : "--no-autosquash"]); }
                    if (options.PreserveMerges) { arguments.Add(capabilities.SupportRebaseMerges ? "--rebase-merges" : "--preserve-merges"); }
                }
                if (options.UpdateRefs is bool update) { arguments.Add(update ? "--update-refs" : "--no-update-refs"); }
                if (options.AutoStash) { arguments.Add("--autostash"); }
                if (options.OnTo is not null) { arguments.AddRange(["--onto", options.OnTo]); }
                arguments.Add("--");
                if (options.From is not null) { arguments.Add(options.From); }
                arguments.Add(branch);
                return await ExecuteNativeRebaseAsync(arguments, directory, state, token).ConfigureAwait(false);
            }
            finally { await CleanupRebaseStateAsync(directory, lease, createdByThisCommand: true).ConfigureAwait(false); }
        }, cancellationToken);
    }

    public Task<GitResult> ContinueRebaseAsync(CancellationToken cancellationToken = default)
        => ManageRebaseOperationAsync(GitOperationAction.Continue, cancellationToken);
    public Task<GitResult> ContinueRebaseAsync(string editorExecutable, CancellationToken cancellationToken = default)
        => RunNativeRebaseOperationAsync(GitOperationAction.Continue, editorExecutable, cancellationToken);
    public Task<GitResult> SkipRebaseAsync(CancellationToken cancellationToken = default)
        => ManageRebaseOperationAsync(GitOperationAction.Skip, cancellationToken);
    public Task<GitResult> SkipRebaseAsync(string editorExecutable, CancellationToken cancellationToken = default)
        => RunNativeRebaseOperationAsync(GitOperationAction.Skip, editorExecutable, cancellationToken);
    public Task<GitResult> AbortRebaseAsync(CancellationToken cancellationToken = default)
        => ManageRebaseOperationAsync(GitOperationAction.Abort, cancellationToken);
    public Task<GitResult> EditRebaseTodoAsync(string? editorExecutable = null, CancellationToken cancellationToken = default)
        => RunNativeRebaseOperationAsync(null, editorExecutable, cancellationToken);

    private Task<GitResult> RunNativeRebaseOperationAsync(GitOperationAction? action, string? editorExecutable, CancellationToken cancellationToken)
        => WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            using RebaseLease lease = await AcquireRebaseLeaseAsync(token).ConfigureAwait(false);
            return await RunNativeRebaseOperationWithLeaseAsync(action, editorExecutable, lease, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<GitResult> RunNativeRebaseOperationWithLeaseAsync(GitOperationAction? action, string? editorExecutable,
        RebaseLease lease, CancellationToken token)
    {
        if (!HasRebaseBackend(lease)) { throw new InvalidOperationException("No rebase is in progress."); }
        // Abort must remain available after an invalid or incompatible config
        // change. Git restores its own checkout; no editor or current codec is needed.
        if (action == GitOperationAction.Abort)
        {
            GitResult aborted = await ExecuteAsync(["rebase", "--abort"], token).ConfigureAwait(false);
            IReadOnlyList<string> warnings = await CleanupRebaseJournalsWithWarningsAsync(lease).ConfigureAwait(false);
            return aborted with { Warnings = [.. aborted.Warnings, .. warnings] };
        }
        RebaseJournalInspection journals = InspectRebaseJournals(lease.GitDirectory);
        if (journals.Error is not null) { throw new InvalidOperationException("The rebase journal is invalid. Abort remains available. " + journals.Error); }
        GitRebaseState rebase = await GetRebaseStateAsync(token).ConfigureAwait(false);
        if (rebase.JournalError is not null) { throw new InvalidOperationException("The rebase journal is invalid. Abort remains available. " + rebase.JournalError); }
        if (rebase.InstructionError is not null)
        { throw new InvalidOperationException("The rebase instruction text is unreadable. Abort remains available. " + rebase.InstructionError); }
        if (rebase.EncodingError is not null) { throw new NotSupportedException(rebase.EncodingError); }
        string legacy = System.IO.Path.Combine(lease.GitDirectory, "gitextensions-rebase");
        if (Directory.Exists(legacy))
        {
            if (action is GitOperationAction legacyAction)
            { return await RunLegacyRebaseOperationWithLeaseAsync(legacyAction, editorExecutable, lease, token).ConfigureAwait(false); }
            _ = await ValidateLegacyRebaseCodecAsync(legacy, lease, token).ConfigureAwait(false);
            throw new NotSupportedException("A legacy rebase plan has frozen messages. Continue, skip or abort it using its saved plan; native todo editing is unavailable.");
        }
        if (action is null && !rebase.CanEditTodo) { throw new InvalidOperationException("The apply backend has no editable todo."); }
        if (editorExecutable is not null) { GitRebaseEditor.ValidateExecutable(editorExecutable); }
        string directory = System.IO.Path.Combine(lease.GitDirectory, GitRebaseEditor.NativeStateDirectoryName);
        bool created = false;
        NativeRebaseEditorState state;
        if (Directory.Exists(directory))
        {
            state = GitRebaseEditor.ReadNativeState(directory);
            Encoding encoding = await GetCommitMessageEncodingAsync(token).ConfigureAwait(false);
            if (state.EncodingName != encoding.WebName)
            { throw new InvalidOperationException("The commit encoding changed during this rebase. Restore its original encoding before editing or continuing."); }
            if (editorExecutable is not null) { state = state with { EditorExecutable = editorExecutable }; GitRebaseEditor.WriteNativeState(directory, state); }
        }
        else
        {
            Encoding encoding = await GetCommitMessageEncodingAsync(token).ConfigureAwait(false);
            GitRepositoryInfo info = await GetInfoAsync(token).ConfigureAwait(false);
            state = new(info.RootPath, info.GitDirectory, editorExecutable ?? "", encoding.WebName, []) { TodoEncodingName = "utf-8" };
            token.ThrowIfCancellationRequested(); CreatePrivateRebaseDirectory(directory); created = true;
            GitRebaseEditor.WriteNativeState(directory, state);
        }
        try
        {
            string operation = action is null ? "--edit-todo" : "--" + action.Value.ToString().ToLowerInvariant();
            GitResult result;
            try { result = await ExecuteNativeRebaseAsync(["rebase", operation], directory, state, token).ConfigureAwait(false); }
            catch (GitCommandException error) when (action == GitOperationAction.Skip)
            {
                // Skip can successfully advance to a new conflict. Only record it if Git actually moved.
                GitRebaseState after = await GetRebaseStateAsync(CancellationToken.None).ConfigureAwait(false);
                if (rebase.CurrentCommit is not null && after.CurrentCommit != rebase.CurrentCommit)
                { GitRebaseEditor.WriteNativeState(directory, state with { SkippedCommits = [.. state.SkippedCommits, rebase.CurrentCommit] }); }
                throw new GitCommandException(error.Arguments, error.Result);
            }
            if (action == GitOperationAction.Skip && rebase.CurrentCommit is not null && Directory.Exists(directory))
            { GitRebaseEditor.WriteNativeState(directory, state with { SkippedCommits = [.. state.SkippedCommits, rebase.CurrentCommit] }); }
            return result;
        }
        finally { await CleanupRebaseStateAsync(directory, lease, created).ConfigureAwait(false); }
    }

    private Task<GitResult> ExecuteNativeRebaseAsync(IReadOnlyList<string> arguments, string directory,
        NativeRebaseEditorState state, CancellationToken token)
        // Git's user-format renderer reencodes its accumulated script buffer. UTF-8 avoids
        // mixed or repeatedly recoded todo subjects; messages and commit objects keep their saved codec.
        => ExecuteAsync(["-c", "i18n.commitEncoding=" + state.EncodingName, "-c", "i18n.logOutputEncoding=UTF-8", .. arguments], token,
            environment: GitRebaseEditor.NativeEnvironmentFor(directory, state),
            outputDecoder: GitRebaseOutputDecoder.ForEncoding(state.EncodingName, GitRebaseEditor.NativeTodoEncodingName(state) ?? "utf-8", "utf-8"));

}
