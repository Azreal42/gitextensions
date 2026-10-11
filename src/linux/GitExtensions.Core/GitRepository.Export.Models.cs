namespace GitExtensions.Linux;

public enum GitArchiveFormat { Zip, Tar }

public sealed record GitArchiveOptions(string OutputPath, string Revision = "HEAD", GitArchiveFormat Format = GitArchiveFormat.Zip,
    string Prefix = "", IReadOnlyList<string>? Paths = null, string? DiffRevision = null, int? CompressionLevel = null);

public sealed record GitArchiveResult(string OutputPath, string Revision, long BytesWritten, IReadOnlyList<string> Paths);

/// <param name="FromRevision">Exclusive lower bound. Null exports the history through ToRevision, including its root.</param>
/// <param name="SelectedRevisions">Oldest first, as in the original dialog: one commit; two inclusive range endpoints; more than two individual commits.</param>
public sealed record GitFormatPatchOptions(string OutputDirectory, string? FromRevision = null, string ToRevision = "HEAD",
    IReadOnlyList<string>? SelectedRevisions = null, int StartNumber = 1, bool CoverLetter = false, bool SignOff = false,
    string? SubjectPrefix = null, bool? Numbered = null, bool FindRenames = true, bool FindCopies = true, bool BreakRewrites = true,
    bool IncludeBinary = true);

public sealed record GitFormatPatchResult(string OutputDirectory, IReadOnlyList<string> Files);

public enum GitPatchFileKind { Diff, Mailbox }
public enum GitPatchTarget { WorkingTree, Index, IndexAndWorkingTree }
public enum GitPatchWhitespace { Warn, Nowarn, Fix, Error, ErrorAll }

public sealed record GitApplyPatchOptions(bool IgnoreWhitespace = false, bool Reverse = false, bool CheckOnly = false,
    GitPatchTarget Target = GitPatchTarget.WorkingTree, bool ThreeWay = false, GitPatchWhitespace Whitespace = GitPatchWhitespace.Warn,
    int StripCount = 1, string? Directory = null);

public sealed record GitAmOptions(bool ThreeWay = true, bool SignOff = false, bool IgnoreWhitespace = false,
    bool KeepSubject = false, bool KeepCr = false, bool CommitterDateIsAuthorDate = false);

public sealed record GitAmState(bool IsActive, int? CurrentPatch, int? PatchCount, string? Subject, string? OriginalHead,
    IReadOnlyList<string> ConflictedPaths, string? CurrentPatchDiff);
