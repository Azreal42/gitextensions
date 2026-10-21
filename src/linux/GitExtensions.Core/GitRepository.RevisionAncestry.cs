namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>
    /// Reads the first fifty native ancestors, including the selected commit, for artificial-row placement.
    /// This independent, uncached traversal uses every parent and no history-view filters.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadRevisionAncestryAsync(string revision, CancellationToken cancellationToken = default)
    {
        string selected = ValidateValue(revision);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable operation = BeginOperation();
        string commit = ValidateObjectId(await ResolveRevisionAsync(selected, cancellationToken).ConfigureAwait(false));
        GitResult result = await ExecuteAsync(["--no-optional-locks", "rev-list", "--max-count=50", commit, "--"], cancellationToken).ConfigureAwait(false);
        List<string> ancestry = [];
        string[] lines = result.StandardOutput.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string hash = lines[index].TrimEnd('\r');
            if (index == lines.Length - 1 && hash.Length == 0) { break; }
            if (hash.Length != commit.Length || !IsObjectId(hash) || ancestry.Contains(hash, StringComparer.OrdinalIgnoreCase))
            { throw new InvalidDataException("Git returned an invalid or duplicate ancestor identity."); }
            ancestry.Add(hash.ToLowerInvariant());
        }
        if (ancestry.Count is 0 or > 50 || ancestry[0] != commit)
        { throw new InvalidDataException("Git returned an invalid bounded ancestry traversal."); }
        cancellationToken.ThrowIfCancellationRequested();
        return ancestry;
    }
}
