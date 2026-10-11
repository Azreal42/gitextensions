namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    public Task<GitSparseCheckoutState> GetSparseCheckoutAsync(CancellationToken cancellationToken = default)
        => WithMutationAsync(ReadSparseCheckoutAsync, cancellationToken);

    /// <summary>Selects literal directory names in cone mode; root files remain visible according to Git's cone rules.</summary>
    public Task<GitResult> SetSparseCheckoutAsync(IEnumerable<string> directories, bool sparseIndex = false, CancellationToken cancellationToken = default)
    {
        string[] paths = ValidateSparseDirectories(directories);
        return MutateAsync(["sparse-checkout", "set", "--cone", "--skip-checks", sparseIndex ? "--sparse-index" : "--no-sparse-index", "--", .. paths], cancellationToken);
    }

    public Task<GitResult> AddSparseCheckoutAsync(IEnumerable<string> directories, CancellationToken cancellationToken = default)
    {
        string[] paths = ValidateSparseDirectories(directories);
        return WithMutationAsync(async token =>
        {
            GitSparseCheckoutState state = await ReadSparseCheckoutAsync(token).ConfigureAwait(false);
            if (!state.Enabled || !state.ConeMode)
            {
                throw new InvalidOperationException("Adding literal directories requires an active cone-mode sparse checkout.");
            }

            return await ExecuteAsync(["sparse-checkout", "add", "--skip-checks", "--", .. paths], token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Sets explicit Git ignore-style patterns. Pattern syntax is data and is never shell-expanded.</summary>
    public Task<GitResult> SetSparsePatternsAsync(IEnumerable<string> patterns, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        string[] values = patterns.ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("Provide at least one sparse checkout pattern.", nameof(patterns));
        }

        foreach (string pattern in values)
        {
            ArgumentException.ThrowIfNullOrEmpty(pattern);
            ValidateSparseLine(pattern);
        }

        return MutateAsync(["sparse-checkout", "set", "--no-cone", "--no-sparse-index", "--", .. values], cancellationToken);
    }

    public Task<GitResult> ReapplySparseCheckoutAsync(CancellationToken cancellationToken = default)
        => MutateAsync(["sparse-checkout", "reapply"], cancellationToken);

    public Task<GitResult> DisableSparseCheckoutAsync(CancellationToken cancellationToken = default)
        => MutateAsync(["sparse-checkout", "disable"], cancellationToken);

    private async Task<GitSparseCheckoutState> ReadSparseCheckoutAsync(CancellationToken cancellationToken)
    {
        bool enabled = await GetBooleanConfigAsync("core.sparseCheckout", cancellationToken).ConfigureAwait(false);
        bool cone = await GetBooleanConfigAsync("core.sparseCheckoutCone", cancellationToken).ConfigureAwait(false);
        bool sparseIndex = await GetBooleanConfigAsync("index.sparse", cancellationToken).ConfigureAwait(false);
        string[] patterns = enabled
            ? (await ExecuteAsync(["sparse-checkout", "list"], cancellationToken).ConfigureAwait(false)).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];
        if (cone)
        {
            patterns = patterns.Select(GitQuotedPath.Decode).ToArray();
        }

        return new GitSparseCheckoutState(enabled, cone, sparseIndex, patterns);
    }

    private async Task<bool> GetBooleanConfigAsync(string key, CancellationToken cancellationToken)
    {
        GitResult result = await ExecuteAsync(["config", "--type=bool", "--get", key], cancellationToken, allowedExitCodes: [1]).ConfigureAwait(false);
        return result.Success && result.StandardOutput.TrimEnd('\r', '\n') == "true";
    }

    private string[] ValidateSparseDirectories(IEnumerable<string> directories)
    {
        string[] paths = ValidatePaths(directories);
        foreach (string path in paths)
        {
            ValidateSparseLine(path);
            if (File.Exists(System.IO.Path.Combine(Path, path)))
            {
                throw new ArgumentException("Cone-mode sparse checkout selects directories, not individual files.", nameof(directories));
            }
        }

        return paths;
    }

    private static void ValidateSparseLine(string value)
    {
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            // Even cone-mode Git stores these names in a line-oriented ignore-pattern file.
            throw new ArgumentException("Git sparse checkout cannot represent directory names or patterns containing NUL, CR or LF.", nameof(value));
        }
    }
}
