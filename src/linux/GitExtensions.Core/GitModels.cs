using GitCommands.Remotes;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Linux;

public sealed record GitChange(string Path, string? OriginalPath, char IndexStatus, char WorkTreeStatus, bool IsConflict, bool IsSubmodule)
{
    public int? SimilarityPercent { get; init; }
    public bool IsStaged => IndexStatus is not ('.' or '?' or '!');
    public bool IsUntracked => IndexStatus == '?';
    public bool IsIgnored => IndexStatus == '!';
    public bool IsModified => WorkTreeStatus != '.' || IsUntracked;
}

public sealed record GitStatus(string? Branch, string? Head, int Ahead, int Behind, IReadOnlyList<GitChange> Changes)
{
    public bool IsDetached => Branch == "(detached)";
    public bool IsUnborn => Head is null;
}

public sealed record GitCommit(string Hash, IReadOnlyList<string> Parents, string AuthorName, string AuthorEmail, DateTimeOffset AuthoredAt, string Subject, string Body)
{
    public ObjectId? Sha1Id => ObjectId.TryParse(Hash, out ObjectId value) ? value : null;
}

public sealed record GitReference(string Name, string Hash, string? Upstream, bool IsCurrent, string? SymbolicTarget = null)
{
    /// <summary>Windows GetRefs' native raw-or-dereferenced target, distinct from the ref object's identity.</summary>
    public string? RevisionHash { get; init; } = Hash;
    public string? RemoteColor { get; init; }
    public bool IsBranch => Name.StartsWith("refs/heads/", StringComparison.Ordinal);
    public bool IsRemote => Name.StartsWith("refs/remotes/", StringComparison.Ordinal);
    public bool IsTag => Name.StartsWith("refs/tags/", StringComparison.Ordinal);
    public string ShortName => IsBranch ? Name[11..] : IsRemote ? Name[13..] : IsTag ? Name[10..] : Name;
    public ObjectId? Sha1Id => ObjectId.TryParse(Hash, out ObjectId value) ? value : null;
}

public sealed record GitRemote(string Name, IReadOnlyList<string> FetchUrls, IReadOnlyList<string> PushUrls)
{
    public Uri? BrowseUri
    {
        get
        {
            string? url = FetchUrls.FirstOrDefault();
            if (url is null)
            {
                return null;
            }

            GitHostingRemoteParser hosting = new();
            if (hosting.TryExtractGitHostingDataFromRemoteUrl(url, out string? host, out string? owner, out string? repository))
            {
                return new Uri($"https://{host}/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}");
            }

            AzureDevOpsRemoteParser azure = new();
            return azure.TryExtractAzureDevopsDataFromRemoteUrl(url, out owner, out string? project, out repository)
                && AzureDevOpsRemoteParser.BuildRepositoryUrl(url, owner, project, repository) is string browse
                    ? new Uri(browse) : null;
        }
    }
}

public sealed record GitStash(string Reference, string Hash, string Subject, DateTimeOffset CreatedAt);
public sealed record GitWorktree(string Path, string? Head, string? Branch, bool IsBare, bool IsDetached, string? LockedReason, string? PrunableReason);
public sealed record GitSubmodule(string Name, string Path, string? Url, string? ExpectedHead, string? Head, bool IsInitialized);
public sealed record GitBlameLine(string Hash, int OriginalLine, int LineNumber, string AuthorName, string AuthorEmail, DateTimeOffset AuthoredAt, string Summary, string Text);
public sealed record GitTreeEntry(string Mode, string Type, string Hash, string Path)
{
    public bool IsSubmodule => Mode == "160000";
    public ObjectId? Sha1Id => ObjectId.TryParse(Hash, out ObjectId value) ? value : null;
}

public enum GitOperationAction { Continue, Abort, Skip }
public enum GitConfigScope { Local, Global }
public sealed record GitRepositoryInfo(string RootPath, string GitDirectory, string CommonDirectory, bool IsBare);
public enum GitResetMode { Soft, Mixed, Hard, Keep, Merge }
public sealed record GitSearchMatch(string Path, int LineNumber, string Text);
public sealed record GitReflogEntry(string Hash, string Selector, string Description, DateTimeOffset CommitDate);
public sealed record GitSparseCheckoutState(bool Enabled, bool ConeMode, bool SparseIndex, IReadOnlyList<string> Patterns);
public sealed record GitBisectState(bool IsActive, string? StartingRevision, string? CurrentRevision, string? BadRevision,
    IReadOnlyList<string> GoodRevisions, IReadOnlyList<string> SkippedRevisions, string? FirstBadRevision, string Log, bool NoCheckout, bool FirstParent)
{
    public bool IsInconclusive => Log.Contains("# only skipped commits left to test", StringComparison.Ordinal);
    public IReadOnlyList<string> PossibleFirstBadRevisions => Log.Split('\n')
        .Where(line => line.StartsWith("# possible first bad commit: [", StringComparison.Ordinal))
        .Select(line => line[30..line.IndexOf(']', 30)]).Distinct(StringComparer.Ordinal).ToArray();
}
public sealed record GitNote(string ObjectHash, string NoteHash, string NotesRef);
public sealed record GitCommitOptions(
    string? Message = null,
    bool Amend = false,
    bool SignOff = false,
    bool? Sign = null,
    string? SigningKey = null,
    string? FixupRevision = null,
    string? SquashRevision = null,
    string? TemplatePath = null,
    bool AllowEmpty = false,
    bool ResetAuthor = false,
    string? Author = null,
    bool NoVerify = false,
    string? AmendRevision = null);
