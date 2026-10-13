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
        ArgumentNullException.ThrowIfNull(plan);
        GitRebaseEditor.ValidateExecutable(editorExecutable);
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? directory = null;
        try
        {
            await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
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
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            RebaseEditorState state = new(editorExecutable, await GetGitPathAsync("rebase-merge/done", cancellationToken).ConfigureAwait(false), messages, encoding.WebName);
            await File.WriteAllBytesAsync(System.IO.Path.Combine(directory, "todo"), todoBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "state.json"), JsonSerializer.Serialize(state), cancellationToken).ConfigureAwait(false);
            return await ExecuteAsync(["-c", "i18n.logOutputEncoding=" + encoding.WebName, "rebase", "--interactive", "--keep-empty", "--", upstream], cancellationToken,
                environment: GitRebaseEditor.EnvironmentFor(directory, state)).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (directory is not null)
                {
                    await CleanupRebaseStateAsync(directory).ConfigureAwait(false);
                }
            }
            finally
            {
                _mutationLock.Release();
            }
        }
    }

    private async Task<GitResult> ManageRebaseOperationAsync(GitOperationAction action, CancellationToken cancellationToken)
    {
        string legacy = await GetGitPathAsync("gitextensions-rebase", cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(legacy))
        { return await RunNativeRebaseOperationAsync(action, null, cancellationToken).ConfigureAwait(false); }
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureWorktreeAsync(cancellationToken).ConfigureAwait(false);
            Encoding encoding = await GetCommitMessageEncodingAsync(cancellationToken).ConfigureAwait(false);
            RebaseEditorState state = GitRebaseEditor.ReadState(legacy);
            if (action != GitOperationAction.Abort && state.EncodingName != encoding.WebName)
            { throw new InvalidOperationException("The commit encoding changed during this rebase. Restore its original encoding before continuing."); }
            return await ExecuteAsync(["-c", "i18n.logOutputEncoding=" + state.EncodingName, "rebase", "--" + action.ToString().ToLowerInvariant()],
                cancellationToken, environment: GitRebaseEditor.EnvironmentFor(legacy, state)).ConfigureAwait(false);
        }
        finally
        {
            try { await CleanupRebaseStateAsync(legacy).ConfigureAwait(false); }
            finally { _mutationLock.Release(); }
        }
    }

    private async Task CleanupRebaseStateAsync(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        if (!Directory.Exists(await GetGitPathAsync("rebase-merge", CancellationToken.None).ConfigureAwait(false))
            && !Directory.Exists(await GetGitPathAsync("rebase-apply", CancellationToken.None).ConfigureAwait(false)))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<string> GetGitPathAsync(string name, CancellationToken cancellationToken)
        => (await ExecuteAsync(["rev-parse", "--path-format=absolute", "--git-path", name], cancellationToken).ConfigureAwait(false)).StandardOutput.TrimEnd('\r', '\n');

    private async Task<IReadOnlyList<string>> GetRebaseCommitsAsync(string upstream, string head, CancellationToken cancellationToken)
        => (await ExecuteAsync(["rev-list", "--reverse", "--topo-order", "--no-merges", upstream + ".." + head], cancellationToken).ConfigureAwait(false))
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
