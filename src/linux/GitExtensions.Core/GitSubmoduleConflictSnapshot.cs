namespace GitExtensions.Linux;

/// <summary>The actual unmerged gitlink stages and a separately validated child working tree.</summary>
public sealed class GitSubmoduleConflictSnapshot
{
    internal GitSubmoduleConflictSnapshot(string path, string? @base, string? local, string? remote, string? current,
        string? error, SubmoduleConflictBinding binding)
    { Path = path; Base = @base; Local = local; Remote = remote; Current = current; ValidationError = error; Binding = binding; }

    public string Path { get; }
    public string? Base { get; }
    public string? Local { get; }
    public string? Remote { get; }
    public string? Current { get; }
    public string? ValidationError { get; }
    public bool CanOpen => ValidationError is null && Binding.ModuleInfo is not null;
    public bool CanStageCurrent => CanOpen && Current is not null;
    public bool CanCheckoutBranch => CanOpen && Local is not null && Remote is not null;
    internal SubmoduleConflictBinding Binding { get; }
}

/// <summary>Native add output together with the verified resulting gitlink; success never implies a parent commit.</summary>
public sealed record GitSubmoduleConflictStageResult(GitResult Result, bool IsResolved, string? StagedCommit, string? ValidationError = null);

internal sealed record SubmoduleConflictBinding(string RepositoryRoot, string IndexSignature,
    IReadOnlyList<WorktreeNode> SourceNodes, IReadOnlyList<WorktreeNode> ModuleNodes, GitRepositoryInfo? ModuleInfo);
