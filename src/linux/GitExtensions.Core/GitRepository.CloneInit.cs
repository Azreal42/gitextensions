namespace GitExtensions.Linux;

public sealed record GitInitOptions(string? InitialBranch = null, bool Bare = false, bool Shared = false);
public sealed record GitCloneOptions(bool Bare = false, bool RecurseSubmodules = true, string? Branch = null, bool NoCheckout = false, int? Depth = null, bool? SingleBranch = false);

public sealed partial class GitRepository
{
    public static async Task<GitRepository> InitAsync(string path, GitInitOptions options, CancellationToken cancellationToken = default, GitRunner? runner = null)
    {
        runner ??= new();
        using IDisposable operation = runner.BeginOperation();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        List<string> arguments = ["init"];
        if (options.InitialBranch is not null) { arguments.Add("--initial-branch=" + ValidateValue(options.InitialBranch)); }
        if (options.Bare) { arguments.Add("--bare"); }
        if (options.Shared) { arguments.Add("--shared=all"); }
        string fullPath = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        GitRepository repository = new(fullPath, runner);
        arguments.AddRange(["--", fullPath]);
        await repository.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        return await OpenAsync(fullPath, cancellationToken, runner).ConfigureAwait(false);
    }

    public static async Task<GitRepository> CloneAsync(string source, string destination, GitCloneOptions options, CancellationToken cancellationToken = default, GitRunner? runner = null)
    {
        runner ??= new();
        using IDisposable operation = runner.BeginOperation();
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string fullPath = System.IO.Path.GetFullPath(destination);
        await CloneCommandAsync(source, fullPath, options, cancellationToken, runner: runner).ConfigureAwait(false);
        return await OpenAsync(fullPath, cancellationToken, runner).ConfigureAwait(false);
    }

    /// <summary>Runs the native clone only. Discovery of the destination is a separate readonly operation.</summary>
    public static async Task<GitResult> CloneCommandAsync(string source, string destination, GitCloneOptions options,
        CancellationToken cancellationToken = default, Action<GitProcessOutput>? outputReceived = null, GitRunner? runner = null)
    {
        runner ??= new();
        using IDisposable operation = runner.BeginOperation();
        ValidateValue(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Depth is <= 0) { throw new ArgumentOutOfRangeException(nameof(options), "Clone depth must be positive."); }
        if (options.NoCheckout && options.Branch is not null) { throw new ArgumentException("Choose a branch or no checkout.", nameof(options)); }
        List<string> arguments = ["clone", "-v", "--progress"];
        if (options.Bare) { arguments.Add("--bare"); }
        if (options.RecurseSubmodules && !options.Bare && !options.NoCheckout) { arguments.Add("--recurse-submodules"); }
        if (options.NoCheckout && !options.Bare) { arguments.Add("--no-checkout"); }
        if (options.Branch is not null) { arguments.Add("--branch=" + ValidateValue(options.Branch)); }
        if (options.Depth is int depth)
        {
            arguments.Add("--depth=" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The original clone dialog keeps other remote branches available in a shallow clone.
            if (options.SingleBranch is bool singleBranch) { arguments.Add(singleBranch ? "--single-branch" : "--no-single-branch"); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = System.IO.Path.GetFullPath(destination);
        string parent = System.IO.Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Clone destination requires a parent.", nameof(destination));
        Directory.CreateDirectory(parent);
        GitRepository repository = new(parent, runner);
        arguments.AddRange(["--", source, fullPath]);
        if (outputReceived is null) { return await repository.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false); }
        GitResult result = await repository._runner.RunAsync(parent, [.. GlobalOptions, .. arguments], cancellationToken,
            outputReceived: outputReceived).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(arguments, result); }
        return result;
    }

    public static async Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string source, CancellationToken cancellationToken = default, GitRunner? runner = null)
    {
        runner ??= new();
        using IDisposable operation = runner.BeginOperation();
        GitResult result = await new GitRepository(System.IO.Path.GetTempPath(), runner).ExecuteAsync(["ls-remote", "--heads", "--", ValidateValue(source)], cancellationToken).ConfigureAwait(false);
        const string prefix = "refs/heads/";
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[(line.IndexOf('\t') + 1)..].TrimEnd('\r'))
            .Where(reference => reference.StartsWith(prefix, StringComparison.Ordinal))
            .Select(reference => reference[prefix.Length..]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
