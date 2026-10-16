namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Reads reference actions for a commit using Windows GetRefs' native target interpretation.</summary>
    public async Task<IReadOnlyList<GitReference>> GetQuickActionReferencesAsync(string commitHash,
        CancellationToken cancellationToken = default)
    {
        string commit = ValidateObjectId(commitHash);
        IReadOnlyList<ReferenceTarget> targets = await ReadReferenceTargetsAsync(
            ["refs/heads/", "refs/remotes/", "refs/tags/"], cancellationToken, noOptionalLocks: true).ConfigureAwait(false);
        List<GitReference> references = [], defaultHeads = [];
        foreach (ReferenceTarget target in targets)
        {
            GitReference reference = target.Reference;
            if (reference.RevisionHash != commit || target.ObjectType != "commit") { continue; }
            if (IsQuickActionDefaultRemoteHead(reference.Name)) { defaultHeads.Add(reference); }
            else { references.Add(reference); }
        }
        // GitModule.ParseRefs suppresses only a one-segment remote's default HEAD when another
        // reference of that remote has the same target. Isolated HEAD and slash-named HEAD remain visible.
        HashSet<string> remotes = references.Where(reference => reference.IsRemote)
            .Select(reference => QuickActionRemote(reference.Name)).ToHashSet(StringComparer.Ordinal);
        references.AddRange(defaultHeads.Where(reference => !remotes.Contains(QuickActionRemote(reference.Name))));
        cancellationToken.ThrowIfCancellationRequested();
        return references;
    }

    private static string QuickActionRemote(string name)
    {
        const int prefix = 13; // refs/remotes/
        int separator = name.IndexOf('/', prefix);
        return separator < 0 ? name[prefix..] : name[prefix..separator];
    }

    private static bool IsQuickActionDefaultRemoteHead(string name)
    {
        const int prefix = 13, suffix = 5; // refs/remotes/ and /HEAD
        return name.StartsWith("refs/remotes/", StringComparison.Ordinal) && name.EndsWith("/HEAD", StringComparison.Ordinal)
            && name.Length > prefix + suffix && name.AsSpan(prefix, name.Length - prefix - suffix).IndexOf('/') < 0;
    }
}
