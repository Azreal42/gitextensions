namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Reads reference actions for a commit using Windows GetRefs' native target interpretation.</summary>
    public async Task<IReadOnlyList<GitReference>> GetQuickActionReferencesAsync(string commitHash,
        CancellationToken cancellationToken = default)
    {
        string commit = ValidateObjectId(commitHash);
        // Keep Windows' authordate/raw-or-dereference expression, rather than adding our own
        // tag peeling policy. Git versions can differ in how nested tags are dereferenced.
        const string format = "--format=%(refname)%00%(objectname)%00"
            + "%(if)%(authordate)%(then)%(objectname)%(else)%(*objectname)%(end)%00"
            + "%(if)%(authordate)%(then)%(objecttype)%(else)%(*objecttype)%(end)%00"
            + "%(upstream:short)%00%(HEAD)%00%(symref)";
        GitResult result = await ExecuteAsync(["--no-optional-locks", "for-each-ref", format,
            "refs/heads/", "refs/remotes/", "refs/tags/"], cancellationToken).ConfigureAwait(false);
        List<GitReference> references = [], defaultHeads = [];
        foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\0');
            if (fields.Length != 7) { throw new FormatException("Incomplete Git reference metadata."); }
            if (fields[2] != commit || fields[3] != "commit") { continue; }
            // Mutations guard the native ref object, including the tag object, rather than its commit.
            GitReference reference = new(fields[0], fields[1], EmptyToNull(fields[4]), fields[5] == "*", EmptyToNull(fields[6]));
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
