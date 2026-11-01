using GitCommands.Git;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<GitResult> StartBisectAsync(string? badRevision = null, IEnumerable<string>? goodRevisions = null, IEnumerable<string>? paths = null,
        bool noCheckout = false, bool firstParent = false, CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
    {
        string[] good = goodRevisions?.ToArray() ?? [];
        if (badRevision is null && good.Length > 0)
        {
            throw new ArgumentException("Good revisions require an initial bad revision.", nameof(goodRevisions));
        }

        string[] selected = paths is null ? [] : ValidatePaths(paths);
        return WithMutationAsync(async token =>
        {
            if (File.Exists(await GetGitPathAsync("BISECT_START", token).ConfigureAwait(false)))
            {
                throw new InvalidOperationException("A bisect is already active. Reset it before starting another.");
            }

            List<string> arguments = ["bisect", "start"];
            if (noCheckout)
            {
                arguments.Add("--no-checkout");
            }

            if (firstParent)
            {
                arguments.Add("--first-parent");
            }

            if (badRevision is not null)
            {
                arguments.Add(await ResolveRevisionAsync(badRevision, token).ConfigureAwait(false));
            }

            foreach (string revision in good)
            {
                arguments.Add(await ResolveRevisionAsync(revision, token).ConfigureAwait(false));
            }

            if (selected.Length > 0) { arguments.AddRange(["--", .. selected]); }
            return await ExecuteBisectProcessAsync(arguments, token, outputReceived).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<GitResult> MarkBisectAsync(GitBisectOption verdict, string? revision = null, CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
    {
        if (!Enum.IsDefined(verdict))
        {
            throw new ArgumentOutOfRangeException(nameof(verdict));
        }

        return WithMutationAsync(async token =>
        {
            if (!File.Exists(await GetGitPathAsync("BISECT_START", token).ConfigureAwait(false)))
            {
                throw new InvalidOperationException("Start a bisect before classifying a commit.");
            }

            List<string> arguments = ["bisect", verdict.ToString().ToLowerInvariant()];
            if (revision is not null)
            {
                arguments.Add(await ResolveRevisionAsync(revision, token).ConfigureAwait(false));
            }

            // Exit 2 is Git's terminal, inconclusive result when only skipped candidates remain.
            return await ExecuteBisectProcessAsync(arguments, token, outputReceived, allowedExitCodes: [2]).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<GitResult> ResetBisectAsync(string? revision = null, CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null)
        => WithMutationAsync(async token =>
        {
            List<string> arguments = ["bisect", "reset"];
            if (revision is not null)
            {
                await ResolveRevisionAsync(revision, token).ConfigureAwait(false);
                arguments.Add(ValidateValue(revision));
            }

            return await ExecuteBisectProcessAsync(arguments, token, outputReceived).ConfigureAwait(false);
        }, cancellationToken);

    public Task<GitBisectState> GetBisectStateAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(ReadBisectStateAsync, cancellationToken);

    private async Task<GitBisectState> ReadBisectStateAsync(CancellationToken cancellationToken)
    {
        string startFile = await GetGitPathAsync("BISECT_START", cancellationToken).ConfigureAwait(false);
        bool active = File.Exists(startFile);
        bool noCheckout = active && File.Exists(await GetGitPathAsync("BISECT_HEAD", cancellationToken).ConfigureAwait(false));
        // BISECT_HEAD is a pseudoref in --no-checkout mode and can also be stored through the ref backend.
        string? current = null;
        if (active)
        {
            GitResult bisectHead = await ExecuteAsync(["rev-parse", "--verify", "BISECT_HEAD"], cancellationToken, allowedExitCodes: [1, 128]).ConfigureAwait(false);
            if (bisectHead.Success)
            {
                current = bisectHead.StandardOutput.TrimEnd('\r', '\n');
                noCheckout = true;
            }
        }

        current ??= await HasHeadAsync(cancellationToken).ConfigureAwait(false) ? await ResolveRevisionAsync("HEAD", cancellationToken).ConfigureAwait(false) : null;
        if (!active)
        {
            return new GitBisectState(false, null, current, null, [], [], null, "", false, false);
        }

        string original = (await File.ReadAllTextAsync(startFile, cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n');
        string logFile = await GetGitPathAsync("BISECT_LOG", cancellationToken).ConfigureAwait(false);
        string log = File.Exists(logFile) ? await File.ReadAllTextAsync(logFile, cancellationToken).ConfigureAwait(false) : "";
        IReadOnlyList<GitReference> references = await GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        string? bad = references.FirstOrDefault(reference => reference.Name == "refs/bisect/bad")?.Hash;
        string[] good = references.Where(reference => reference.Name.StartsWith("refs/bisect/good-", StringComparison.Ordinal)).Select(reference => reference.Hash).ToArray();
        string[] skipped = references.Where(reference => reference.Name.StartsWith("refs/bisect/skip-", StringComparison.Ordinal)).Select(reference => reference.Hash).ToArray();
        string? firstBad = GitBisectLog.ReadBadRevisions(log).LastOrDefault();

        bool firstParent = File.Exists(await GetGitPathAsync("BISECT_FIRST_PARENT", cancellationToken).ConfigureAwait(false));
        return new GitBisectState(true, original, current, bad, good, skipped, firstBad, log, noCheckout, firstParent);
    }
}
