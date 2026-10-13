namespace GitExtensions.Linux;

/// <summary>The FormVerify flags: an unchecked Full check emits no flag, unlike the legacy GitFsckOptions contract.</summary>
public sealed record GitObjectRecoveryOptions(bool FullCheck = false, bool IncludeUnreachable = false, bool NoReflogs = true);
public enum GitLostObjectType { Commit, Blob, Tree, Tag, Other }
public sealed record GitLostObject(string Hash, GitLostObjectType Type, string RawType, string Diagnostic)
{
    public DateTimeOffset? Date { get; init; }
    public string? Subject { get; init; }
    public string? Author { get; init; }
    public string? Parent { get; init; }
    public string? TagName { get; init; }
    public string? ReadError { get; init; }
}
public sealed record GitObjectRecoveryReport(int ExitCode, string StandardOutput, string StandardError,
    IReadOnlyList<GitLostObject> Objects, string LostFoundDirectory)
{
    public bool Success => ExitCode == 0;
}
/// <summary>Bytes are the exact uncompressed object payload, without the Git object header. Tags are never peeled.</summary>
public sealed record GitObjectData(string Hash, GitLostObjectType Type, string RawType, byte[] Bytes);
public sealed record GitObjectPreview(string Hash, GitObjectData? Object, string? Text, GitDiffDocument? DiffDocument,
    string FileEncodingName, string SuggestedFileName, string? ReadError);
public sealed record GitObjectExportResult(string DestinationPath, long BytesWritten, bool ConvertedLineEndings);
public sealed record GitRecoveredObjectTag(string Name, string ObjectId);
public sealed record GitObjectRecoveryError(string? ObjectId, string Operation, string Message);
/// <summary>Partial batch effects are observed from actual refs, including cancellation after Git has changed a ref.</summary>
public sealed record GitObjectRecoveryResult(IReadOnlyList<GitRecoveredObjectTag> CreatedTags,
    IReadOnlyList<string> DeletedTags, IReadOnlyList<GitObjectRecoveryError> Errors, bool Cancelled)
{
    public bool EffectsVerified { get; init; } = true;
    public IReadOnlyList<GitRecoveredObjectTag> DivergentTags { get; init; } = [];
    public bool Success => !Cancelled && Errors.Count == 0;
    public bool HasRepositoryChanges => CreatedTags.Count != 0 || DeletedTags.Count != 0 || DivergentTags.Count != 0 || !EffectsVerified;
}
