namespace GitExtensions.Linux;

/// <summary>An immutable indexed submodule registration and its actual native status, guarded before a selected operation.</summary>
public sealed class GitSubmoduleSnapshot
{
    internal GitSubmoduleSnapshot(string name, string path, string? url, string? indexHead, string? head, string? commit,
        string branch, char code, string? error, GitSubmoduleRemovalSelection? removal, SubmoduleManagementBinding binding)
    {
        Name = name; Path = path; Url = url; IndexHead = indexHead; Head = head; Commit = commit; Branch = branch;
        Code = code; ValidationError = error; RemovalSelection = removal; Binding = binding;
    }
    public string Name { get; }
    public string Path { get; }
    public string? Url { get; }
    public string? IndexHead { get; }
    public string? Head { get; }
    public string? Commit { get; }
    public string Branch { get; }
    public bool IsInitialized => Code is ' ' or '+' or 'U';
    public bool IsUpToDate => Code == ' ';
    public bool IsConflicted => Code == 'U';
    public string Status => IsConflicted ? "Conflicted" : !IsInitialized ? "Not initialized" : IsUpToDate ? "Up-to-date" : "Modified";
    public string? ValidationError { get; }
    public bool CanOpen => ValidationError is null && Binding.ModuleInfo is not null;
    public bool CanUpdate => ValidationError is null;
    public bool CanSync => ValidationError is null;
    public bool CanRemove => ValidationError is null && RemovalSelection is not null;
    public GitSubmoduleRemovalSelection? RemovalSelection { get; }
    internal char Code { get; }
    internal SubmoduleManagementBinding Binding { get; }
}

internal sealed record SubmoduleManagementBinding(string RepositoryRoot, string IndexSignature, string ConfigurationRevision,
    IReadOnlyList<WorktreeNode> SourceNodes, IReadOnlyList<WorktreeNode> ModuleNodes, GitRepositoryInfo? ModuleInfo);
