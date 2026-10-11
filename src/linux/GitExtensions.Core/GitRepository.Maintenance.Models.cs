namespace GitExtensions.Linux;

public sealed record GitFsckOptions(bool Full = true, bool IncludeUnreachable = false, bool NoReflogs = false, bool Strict = false);
public enum GitFsckIssueKind { Dangling, Unreachable }
public sealed record GitFsckIssue(GitFsckIssueKind Kind, string ObjectType, string Hash);
public sealed record GitFsckResult(int ExitCode, string StandardOutput, string StandardError, IReadOnlyList<GitFsckIssue> Issues)
{
    public bool IsValid => ExitCode == 0;
}

public sealed record GitRepositoryStatistics(long LooseObjects, long LooseSizeBytes, long PackedObjects, long PackFiles, long PackedSizeBytes,
    long PrunablePackedObjects, long GarbageObjects, long GarbageSizeBytes, IReadOnlyList<string> Alternates, string Diagnostics);
