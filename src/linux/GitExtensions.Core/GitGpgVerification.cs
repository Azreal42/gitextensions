using GitCommands.Git.Gpg;

namespace GitExtensions.Linux;

/// <summary>Verification of one immutable annotated-tag object captured from the current refs.</summary>
public sealed record GitAnnotatedTagVerification(string Name, string ObjectId, TagStatus Status,
    int RawExitCode, int MessageExitCode, string RawVerificationMessage, string VerificationMessage);

/// <summary>The commit result remains available independently of its directly attached annotated tags.</summary>
public sealed record GitGpgVerification(string CommitHash, GpgInfo Information, string CommitDisplayText,
    IReadOnlyList<GitAnnotatedTagVerification> Tags);
