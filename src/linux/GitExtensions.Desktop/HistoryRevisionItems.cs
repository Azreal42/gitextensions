namespace GitExtensions.Linux.Desktop;

/// <summary>A comparison endpoint in the history, never a fabricated Git commit or object name.</summary>
internal sealed record ArtificialHistoryRevision
{
    internal const string WorkingTreeKey = ":working-tree";
    internal const string IndexKey = ":index";
    internal GitCompareRevisionKind Kind { get; }
    internal string GraphId => Kind == GitCompareRevisionKind.WorkingTree ? WorkingTreeKey : IndexKey;
    internal string Subject => Kind == GitCompareRevisionKind.WorkingTree ? "Working directory" : "Commit index";
    internal GitCompareRevision Revision => new("", Kind);
    internal IReadOnlyList<string> ParentIds { get; }

    internal ArtificialHistoryRevision(GitCompareRevisionKind kind, string? head)
    {
        if (kind is not (GitCompareRevisionKind.WorkingTree or GitCompareRevisionKind.Index)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        Kind = kind;
        ParentIds = kind == GitCompareRevisionKind.WorkingTree ? [IndexKey] : head is null ? [] : [head];
    }
}

/// <summary>Stable view keys for actual GitCommit objects and explicit artificial endpoints in one native list.</summary>
internal static class HistoryRevisionItems
{
    /// <summary>Complete row payload equality across query generations, not record/list reference equality.</summary>
    internal static bool HasSameSnapshotData(object left, object right) => (left, right) switch
    {
        (GitCommit first, GitCommit second) => string.Equals(first.Hash, second.Hash, StringComparison.Ordinal)
            && first.Parents.SequenceEqual(second.Parents, StringComparer.Ordinal)
            && string.Equals(first.AuthorName, second.AuthorName, StringComparison.Ordinal)
            && string.Equals(first.AuthorEmail, second.AuthorEmail, StringComparison.Ordinal)
            && first.AuthoredAt.EqualsExact(second.AuthoredAt)
            && string.Equals(first.Subject, second.Subject, StringComparison.Ordinal)
            && string.Equals(first.Body, second.Body, StringComparison.Ordinal)
            && string.Equals(first.FullMessage, second.FullMessage, StringComparison.Ordinal),
        (ArtificialHistoryRevision first, ArtificialHistoryRevision second) => first.Kind == second.Kind
            && first.ParentIds.SequenceEqual(second.ParentIds, StringComparer.Ordinal),
        _ => false
    };

    internal static string Key(object item) => item switch
    {
        GitCommit commit => commit.Hash,
        ArtificialHistoryRevision artificial => artificial.GraphId,
        _ => throw new ArgumentException("A history row must be a commit or an artificial endpoint.", nameof(item))
    };
    internal static IReadOnlyList<string> Parents(object item) => item switch
    {
        GitCommit commit => commit.Parents,
        ArtificialHistoryRevision artificial => artificial.ParentIds,
        _ => throw new ArgumentException("Unknown history row.", nameof(item))
    };
    internal static string Subject(object item) => item switch
    {
        GitCommit commit => commit.Subject,
        ArtificialHistoryRevision artificial => artificial.Subject,
        _ => throw new ArgumentException("Unknown history row.", nameof(item))
    };
    internal static GitCompareRevision Revision(object item) => item switch
    {
        GitCommit commit => new(commit.Hash),
        ArtificialHistoryRevision artificial => artificial.Revision,
        _ => throw new ArgumentException("Unknown history row.", nameof(item))
    };
}
