namespace GitExtensions.Linux;

/// <summary>An exact indexed registration, captured before the removal confirmation.</summary>
public sealed record GitSubmoduleRemovalSelection(string RepositoryPath, string Name, string Path, string? Url,
    string IndexHead, string ModulesFingerprint, bool IsInitialized);

/// <summary>Removal always preserves the module worktree and its internal repository.</summary>
public sealed record GitSubmoduleRemovalResult(string Path, bool RemovedModulesFileFromIndex);
