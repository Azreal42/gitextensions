namespace GitExtensions.Linux;

public sealed record GitRemoteHeadsResult(GitResult Result, IReadOnlyList<GitReference> Heads);

public sealed partial class GitRepository
{
    /// <summary>Read server branch hashes in this repository's captured Git context, without fetching objects or refs.</summary>
    public async Task<IReadOnlyList<GitReference>> GetRemoteHeadsAsync(string remote, CancellationToken cancellationToken = default,
        Action<GitProcessOutput>? outputReceived = null)
    {
        GitRemoteHeadsResult read = await ReadRemoteHeadsAsync(remote, cancellationToken, outputReceived).ConfigureAwait(false);
        if (!read.Result.Success) { throw new GitCommandException([.. GlobalOptions, "ls-remote", "--heads", "--", ValidateValue(remote)], read.Result); }
        return read.Heads;
    }

    /// <summary>Retain the native result for the process window, including failed server reads.</summary>
    public async Task<GitRemoteHeadsResult> ReadRemoteHeadsAsync(string remote, CancellationToken cancellationToken = default,
        Action<GitProcessOutput>? outputReceived = null)
    {
        using IDisposable operation = _runner.BeginOperation();
        string[] arguments = [.. GlobalOptions, "ls-remote", "--heads", "--", ValidateValue(remote)];
        GitResult result = await _runner.RunAsync(Path, arguments, cancellationToken, outputReceived: outputReceived).ConfigureAwait(false);
        if (!result.Success) { return new(result, []); }
        Dictionary<string, GitReference> references = new(StringComparer.Ordinal);
        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string record = line.TrimEnd('\r');
            int separator = record.IndexOf('\t');
            if (separator < 0 || record.IndexOf('\t', separator + 1) >= 0)
            { throw new InvalidDataException("Git returned an invalid remote branch record."); }
            string hash = record[..separator], name = record[(separator + 1)..];
            if (hash.Length is not (40 or 64) || hash.Any(character => !char.IsAsciiHexDigit(character))
                || !name.StartsWith("refs/heads/", StringComparison.Ordinal) || name.Length == 11
                || name.Any(char.IsControl))
            { throw new InvalidDataException("Git returned an invalid remote branch name or object id."); }
            if (!references.TryAdd(name, new(name, hash, null, false)))
            { throw new InvalidDataException("Git returned duplicate remote branch names."); }
        }
        return new(result, references.Values.OrderBy(reference => reference.Name, StringComparer.Ordinal).ToArray());
    }
}
