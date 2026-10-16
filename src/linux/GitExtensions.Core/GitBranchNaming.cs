using GitCommands.Git;

namespace GitExtensions.Linux;

/// <summary>Uses the original branch-name normaliser with its original replacement-token rules.</summary>
public static class GitBranchNaming
{
    public static string Normalize(string? name, string replacement = "_", bool allowTrailingSlash = false)
        => new GitBranchNameNormaliser().Normalise(name, new GitBranchNameOptions(replacement, allowTrailingSlash));

    /// <summary>Preserves the original forms' guard before normalising an editable branch-name field.</summary>
    public static string NormalizeForInput(string? name, bool enabled = true, string replacement = "_")
    {
        string input = name ?? "";
        return !enabled || !input.Any(GitCommands.PathUtil.IsValidPathChar) ? input : Normalize(input, replacement);
    }
}
