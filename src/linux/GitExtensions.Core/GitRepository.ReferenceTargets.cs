namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    private sealed record ReferenceTarget(GitReference Reference, string? ObjectType);

    private async Task<IReadOnlyList<ReferenceTarget>> ReadReferenceTargetsAsync(IReadOnlyList<string> patterns,
        CancellationToken cancellationToken, bool noOptionalLocks = false)
    {
        GitReferenceSorting sorting = ReferenceSorting;
        // This is Windows Commands.GetRefs' expression, not recursive ^{commit} peeling.
        // Keeping the direct object separately lets callers detect a reannotated tag at the same commit.
        const string format = "--format=%(refname)%00%(objectname)%00"
            + "%(if)%(authordate)%(then)%(objectname)%(else)%(*objectname)%(end)%00"
            + "%(if)%(authordate)%(then)%(objecttype)%(else)%(*objecttype)%(end)%00"
            + "%(upstream:short)%00%(HEAD)%00%(symref)";
        List<string> arguments = ["for-each-ref", .. ReferenceSortArguments(sorting, includeTags: true), format, .. patterns];
        if (noOptionalLocks) { arguments.Insert(0, "--no-optional-locks"); }
        GitResult result = await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        List<ReferenceTarget> targets = [];
        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\0');
            if (fields.Length != 7) { throw new FormatException("Incomplete Git reference metadata."); }
            GitReference reference = new(fields[0], fields[1], EmptyToNull(fields[4]), fields[5] == "*", EmptyToNull(fields[6]))
                { RevisionHash = EmptyToNull(fields[2]) };
            targets.Add(new(reference, EmptyToNull(fields[3])));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return targets;
    }
}
