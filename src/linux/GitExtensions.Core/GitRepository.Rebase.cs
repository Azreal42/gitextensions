using System.Text;
using System.Text.Json;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public async Task<GitRebasePlan> GetRebasePlanAsync(string upstream, CancellationToken cancellationToken = default)
    {
        string baseCommit = await ResolveRevisionAsync(upstream, cancellationToken).ConfigureAwait(false);
        string head = await ResolveRevisionAsync("HEAD", cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> hashes = await GetRebaseCommitsAsync(baseCommit, head, cancellationToken).ConfigureAwait(false);
        List<GitRebaseStep> steps = [];
        foreach (string hash in hashes)
        {
            GitCommit commit = (await GetLogAsync(1, hash, cancellationToken).ConfigureAwait(false)).Single();
            steps.Add(new GitRebaseStep(GitRebaseAction.Pick, hash, commit.Subject));
        }

        return new GitRebasePlan(baseCommit, head, steps);
    }

    public async Task<GitResult> InteractiveRebaseAsync(GitRebasePlan plan, string editorExecutable, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = _runner.BeginOperation();
        ArgumentNullException.ThrowIfNull(plan);
        GitRebaseEditor.ValidateExecutable(editorExecutable);
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? directory = null;
        RebaseLease? lease = null;
        bool created = false;
        try
        {
            await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
            lease = await AcquireRebaseLeaseAsync(cancellationToken).ConfigureAwait(false);
            if (HasRebaseBackend(lease)) { throw new InvalidOperationException("A rebase is already in progress."); }
            await CleanupObsoleteRebaseJournalsAsync(lease).ConfigureAwait(false);
            Encoding encoding = await GetCommitMessageEncodingAsync(cancellationToken).ConfigureAwait(false);
            if (await ResolveRevisionAsync("HEAD", cancellationToken).ConfigureAwait(false) != plan.Head)
            {
                throw new InvalidOperationException("HEAD changed since this rebase plan was prepared. Reload the plan.");
            }

            string upstream = await ResolveRevisionAsync(plan.Upstream, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> expected = await GetRebaseCommitsAsync(upstream, plan.Head, cancellationToken).ConfigureAwait(false);
            if (plan.Steps.Count != expected.Count || !plan.Steps.Select(step => step.Hash).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            {
                throw new ArgumentException("The plan must contain every original commit exactly once. Use Drop to remove a commit.", nameof(plan));
            }

            GitRebaseStep? firstKept = plan.Steps.FirstOrDefault(step => step.Action != GitRebaseAction.Drop);
            if (firstKept?.Action is GitRebaseAction.Squash or GitRebaseAction.Fixup)
            {
                throw new ArgumentException("The first retained commit cannot be squash or fixup.", nameof(plan));
            }

            StringBuilder todo = new();
            Dictionary<string, string> messages = new(StringComparer.Ordinal);
            foreach (GitRebaseStep step in plan.Steps)
            {
                if (!Enum.IsDefined(step.Action))
                {
                    throw new ArgumentOutOfRangeException(nameof(plan), "Unsupported rebase action.");
                }

                if (step.Action == GitRebaseAction.Reword || step.Action == GitRebaseAction.Squash && !string.IsNullOrWhiteSpace(step.Message))
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(step.Message);
                    if (step.Message.Contains('\0'))
                    {
                        throw new ArgumentException("Commit messages cannot contain NUL.", nameof(plan));
                    }

                    messages.Add(step.Hash, step.Message);
                }

                todo.Append(step.Action.ToString().ToLowerInvariant()).Append(' ').Append(step.Hash).Append(' ')
                    .Append(step.Subject.Replace('\n', ' ').Replace('\r', ' ').Replace('\0', ' ')).Append('\n');
            }

            directory = await GetGitPathAsync("gitextensions-rebase", cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(directory))
            {
                throw new InvalidOperationException("An existing Git Extensions rebase plan must be continued or aborted first.");
            }

            if (Directory.Exists(await GetGitPathAsync("rebase-merge", cancellationToken).ConfigureAwait(false))
                || Directory.Exists(await GetGitPathAsync("rebase-apply", cancellationToken).ConfigureAwait(false)))
            {
                throw new InvalidOperationException("A rebase is already in progress.");
            }

            // Validate every editor byte before creating state or letting Git rewrite commits.
            byte[] todoBytes = encoding.GetBytes(todo.ToString());
            foreach (string message in messages.Values) { _ = encoding.GetBytes(message); }
            if (Directory.Exists(await GetGitPathAsync(GitRebaseEditor.NativeStateDirectoryName, cancellationToken).ConfigureAwait(false)))
            { throw new InvalidOperationException("A native rebase session must be completed first."); }
            CreatePrivateRebaseDirectory(directory); created = true;

            RebaseEditorState state = new(editorExecutable, await GetGitPathAsync("rebase-merge/done", cancellationToken).ConfigureAwait(false), messages, encoding.WebName);
            await File.WriteAllBytesAsync(System.IO.Path.Combine(directory, "todo"), todoBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "state.json"), JsonSerializer.Serialize(state), cancellationToken).ConfigureAwait(false);
            return await ExecuteAsync(["-c", "i18n.commitEncoding=" + state.EncodingName, "-c", "i18n.logOutputEncoding=UTF-8",
                "rebase", "--interactive", "--keep-empty", "--", upstream], cancellationToken,
                environment: GitRebaseEditor.EnvironmentFor(directory, state),
                outputDecoder: GitRebaseOutputDecoder.ForEncoding(state.EncodingName, state.EncodingName, "utf-8")).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (directory is not null && lease is not null && created)
                {
                    await CleanupRebaseStateAsync(directory, lease, createdByThisCommand: true).ConfigureAwait(false);
                }
            }
            finally
            {
                lease?.Dispose();
                _mutationLock.Release();
            }
        }
    }

    private Task<GitResult> ManageRebaseOperationAsync(GitOperationAction action, CancellationToken cancellationToken)
        => WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            using RebaseLease lease = await AcquireRebaseLeaseAsync(token).ConfigureAwait(false);
            string legacy = System.IO.Path.Combine(lease.GitDirectory, "gitextensions-rebase");
            if (action == GitOperationAction.Abort || !Directory.Exists(legacy))
            { return await RunNativeRebaseOperationWithLeaseAsync(action, null, lease, token).ConfigureAwait(false); }
            return await RunLegacyRebaseOperationWithLeaseAsync(action, null, lease, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<GitResult> RunLegacyRebaseOperationWithLeaseAsync(GitOperationAction action, string? editorExecutable,
        RebaseLease lease, CancellationToken token)
    {
        string directory = System.IO.Path.Combine(lease.GitDirectory, "gitextensions-rebase");
        try
        {
            GitRebaseState rebase = await GetRebaseStateAsync(token).ConfigureAwait(false);
            if (rebase.InstructionError is not null)
            { throw new InvalidOperationException("The rebase instruction text is unreadable. Abort remains available. " + rebase.InstructionError); }
            RebaseEditorState state = await ValidateLegacyRebaseCodecAsync(directory, lease, token).ConfigureAwait(false);
            if (editorExecutable is not null && editorExecutable != state.EditorExecutable)
            {
                GitRebaseEditor.ValidateExecutable(editorExecutable);
                token.ThrowIfCancellationRequested();
                state = state with { EditorExecutable = editorExecutable };
                WriteLegacyRebaseEditorState(directory, state);
            }
            return await ExecuteAsync(["-c", "i18n.commitEncoding=" + state.EncodingName, "-c", "i18n.logOutputEncoding=UTF-8",
                "rebase", "--" + action.ToString().ToLowerInvariant()],
                token, environment: GitRebaseEditor.EnvironmentFor(directory, state),
                outputDecoder: GitRebaseOutputDecoder.ForEncoding(state.EncodingName, state.EncodingName, "utf-8")).ConfigureAwait(false);
        }
        finally { await CleanupObsoleteRebaseJournalsAsync(lease).ConfigureAwait(false); }
    }

    private async Task<RebaseEditorState> ValidateLegacyRebaseCodecAsync(string directory, RebaseLease lease, CancellationToken token)
    {
        RebaseJournalInspection journals = InspectRebaseJournals(lease.GitDirectory);
        if (journals.Error is not null) { throw new InvalidOperationException("The rebase journal is invalid. Abort remains available. " + journals.Error); }
        EnsureRebaseMetadataSafety(directory); EnsureRebaseMetadataFileSafety(System.IO.Path.Combine(directory, "state.json"));
        RebaseEditorState state = GitRebaseEditor.ReadState(directory);
        if (!GitRebaseEditor.SamePath(state.DonePath, System.IO.Path.Combine(lease.GitDirectory, "rebase-merge", "done")))
        { throw new IOException("The legacy rebase journal belongs to another worktree."); }
        _ = GitRebaseEditor.GetStrictEncoding(state.EncodingName);
        Encoding encoding = await GetCommitMessageEncodingAsync(token).ConfigureAwait(false);
        if (state.EncodingName != encoding.WebName)
        { throw new InvalidOperationException("The commit encoding changed during this rebase. Restore its original encoding before editing or continuing."); }
        return state;
    }

    private static void WriteLegacyRebaseEditorState(string directory, RebaseEditorState state)
    {
        string path = System.IO.Path.Combine(directory, "state.json"), temporary = System.IO.Path.Combine(directory, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        EnsureRebaseMetadataSafety(directory); EnsureRebaseMetadataFileSafety(path);
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private async Task<string> GetGitPathAsync(string name, CancellationToken cancellationToken)
    {
        string path = (await ExecuteAsync(["rev-parse", "--path-format=absolute", "--git-path", name], cancellationToken).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n');
        if (!System.IO.Path.IsPathFullyQualified(path)) { throw new InvalidDataException("Git did not return an absolute administrative path."); }
        return System.IO.Path.GetFullPath(path);
    }

    private async Task<IReadOnlyList<string>> GetRebaseCommitsAsync(string upstream, string head, CancellationToken cancellationToken)
        => (await ExecuteAsync(["rev-list", "--reverse", "--topo-order", "--no-merges", upstream + ".." + head], cancellationToken).ConfigureAwait(false))
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
