using GitCommands.ExternalLinks;

namespace GitExtensions.Linux;

/// <summary>A reference attached to this exact revision, with Windows IGitRef.LocalName already projected.</summary>
public sealed record GitRevisionLinkReference(bool IsRemote, string LocalName);

/// <summary>Configured raw URL fields, not GetRemotesAsync's synthesized push fallback. Preserve caller ordering.</summary>
public sealed record GitRevisionLinkRemote(string Name, string? Url, string? PushUrl);

/// <summary>Caller-captured data only: no module, repository discovery, configuration IO or network.</summary>
public sealed class GitRevisionLinkContext
{
    public GitRevisionLinkContext(string hash, string? message, IReadOnlyList<GitRevisionLinkReference> attachedRefs,
        IReadOnlyList<GitRevisionLinkRemote> rawRemotes)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(attachedRefs);
        ArgumentNullException.ThrowIfNull(rawRemotes);
        Hash = hash;
        Message = message;
        AttachedRefs = Array.AsReadOnly(attachedRefs.ToArray());
        RawRemotes = Array.AsReadOnly(rawRemotes.ToArray());
    }

    public string Hash { get; }
    /// <summary>Full commit message including its subject; excludes Git notes.</summary>
    public string? Message { get; }
    public IReadOnlyList<GitRevisionLinkReference> AttachedRefs { get; }
    public IReadOnlyList<GitRevisionLinkRemote> RawRemotes { get; }
}

public enum GitRevisionLinkDiagnosticKind { InvalidXml, InvalidPattern, InvalidFormat }

public sealed record GitRevisionLinkDiagnostic(GitRevisionLinkDiagnosticKind Kind, string? DefinitionName,
    string? Property, string Message)
{
    public GitDistributedSettingsScope? Scope { get; init; }
}

public sealed class GitRevisionLinkParseResult
{
    internal GitRevisionLinkParseResult(IEnumerable<ExternalLink> links, IEnumerable<GitRevisionLinkDiagnostic> diagnostics)
    {
        ExternalLink[] captured = links.ToArray();
        Links = Array.AsReadOnly(captured);
        DistinctLinks = Array.AsReadOnly(captured.Distinct().ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>Windows parser order, including duplicates and invalid-format error links.</summary>
    public IReadOnlyList<ExternalLink> Links { get; }
    /// <summary>CommitInfo's final first-URI-wins distinct projection; captions do not participate.</summary>
    public IReadOnlyList<ExternalLink> DistinctLinks { get; }
    public IReadOnlyList<GitRevisionLinkDiagnostic> Diagnostics { get; }
}
