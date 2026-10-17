namespace GitExtensions.Linux;

public enum GitRefsSortBy
{
    GitDefault = 0,
    AuthorDate,
    CommitterDate,
    CreatorDate,
    TaggerDate,
    RefName,
    VersionRefName,
    ObjectSize,
    Upstream
}

public enum GitRefsSortOrder { Ascending = 0, Descending = 1 }

/// <summary>The native Windows for-each-ref ordering, independent of commit log ordering.</summary>
public sealed record GitReferenceSorting(GitRefsSortBy By = GitRefsSortBy.GitDefault,
    GitRefsSortOrder Order = GitRefsSortOrder.Descending)
{
    internal void Validate()
    {
        if (!Enum.IsDefined(By)) { throw new ArgumentOutOfRangeException(nameof(By)); }
        if (!Enum.IsDefined(Order)) { throw new ArgumentOutOfRangeException(nameof(Order)); }
    }
}

public sealed partial class GitRepository
{
    private GitReferenceSorting _referenceSorting = new();

    /// <summary>Immutable presentation preference; each native reference read captures its own snapshot.</summary>
    public GitReferenceSorting ReferenceSorting
    {
        get => Volatile.Read(ref _referenceSorting);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            Volatile.Write(ref _referenceSorting, value);
        }
    }

    private static string[] ReferenceSortArguments(GitReferenceSorting sorting, bool includeTags)
    {
        sorting.Validate();
        string? key = sorting.By switch
        {
            GitRefsSortBy.GitDefault => null,
            GitRefsSortBy.AuthorDate => "authordate",
            GitRefsSortBy.CommitterDate => "committerdate",
            GitRefsSortBy.CreatorDate => "creatordate",
            GitRefsSortBy.TaggerDate => "taggerdate",
            GitRefsSortBy.RefName => "refname",
            GitRefsSortBy.VersionRefName => "version:refname",
            GitRefsSortBy.ObjectSize => "objectsize",
            GitRefsSortBy.Upstream => "upstream",
            _ => throw new ArgumentOutOfRangeException(nameof(sorting))
        };
        if (key is null) { return []; }
        string order = sorting.Order == GitRefsSortOrder.Descending ? "-" : "";
        // Keep Windows Commands.GetRefs' key order: the direct key is primary, the
        // dereferenced key is secondary. version:refname cannot be dereferenced.
        string dereferencedKey = sorting.By == GitRefsSortBy.VersionRefName ? "refname" : key;
        return includeTags ? [$"--sort={order}*{dereferencedKey}", $"--sort={order}{key}"] : [$"--sort={order}{key}"];
    }
}
