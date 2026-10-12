namespace GitExtensions.Linux;

public enum GitDiffAppearance { Patch, GitWordDiff, Difftastic }

/// <summary>Presentation only. ViewerWidth is the unscaled editor width used by the original Difftastic column calculation.</summary>
public sealed record GitDiffDisplayOptions(bool UseGitColoring = true, bool ReverseGitColoring = true,
    GitDiffAppearance Appearance = GitDiffAppearance.Patch, bool SyntaxHighlighting = true, int ViewerWidth = 800)
{
    /// <summary>Session override only. Null resolves the repository's effective i18n.filesencoding.</summary>
    public string? FileEncodingName { get; init; }
}

/// <summary>Patch is the canonical uncolored Git patch. Display may contain SGR or a read-only alternative representation.</summary>
public sealed record GitDiffDocument(string Patch, string Display, GitDiffAppearance EffectiveAppearance, bool CanStageEquivalent)
{
    /// <summary>The columns used for the captured Difftastic output, including an explicit user DFT_WIDTH override.</summary>
    public int DifftasticWidth { get; init; }
    /// <summary>Presentation uses Git/tool coloring even when explicitly empty custom colors emit no SGR.</summary>
    public bool UseGitColoring { get; init; }
    public string FileEncodingName { get; init; } = "utf-8";
    public ReadOnlyMemory<byte> PatchBytes { get; init; }
    /// <summary>Decoded patch text reconstructs every canonical byte with the recorded file codec.</summary>
    public bool IsLosslessPatch { get; init; }
}
