namespace GitExtensions.Linux;

/// <summary>An actual child-to-superproject association. The child's HEAD is deliberately not a stale-edit guard.</summary>
public sealed class GitSuperprojectSnapshot
{
    internal GitSuperprojectSnapshot(string childRoot, string superprojectRoot, string submodulePath, string? indexHead,
        SuperprojectBinding binding)
    { ChildRoot = childRoot; SuperprojectRoot = superprojectRoot; SubmodulePath = submodulePath; IndexHead = indexHead; Binding = binding; }
    public string ChildRoot { get; }
    public string SuperprojectRoot { get; }
    public string SubmodulePath { get; }
    public string? IndexHead { get; }
    internal SuperprojectBinding Binding { get; }
}

/// <summary>Native effects remain real even when Git fails, readback fails, or another actor changes the child HEAD.</summary>
public sealed record GitSuperprojectStageResult(GitResult Result, bool Verified, string ChildHead, string? StagedHead, string? Error);

internal sealed record SuperprojectBinding(GitRepositoryInfo Child, GitRepositoryInfo Parent, string Registration,
    string IndexSignature, IReadOnlyList<WorktreeNode> Nodes, GitExecutionEnvironment Environment);
