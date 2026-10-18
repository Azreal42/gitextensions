namespace GitExtensions.Linux;

/// <summary>Immutable Windows-format XML values and an opaque baseline for one guarded file edit.</summary>
public sealed class GitXmlSettingsSnapshot
{
    internal GitXmlSettingsSnapshot(string path, GitRulesLocation location, bool exists, string hash,
        GitRulesIdentity? identity, UnixFileMode? mode, FileAttributes attributes, IReadOnlyDictionary<string, string?> values)
    {
        Path = path; Location = location; Exists = exists; Hash = hash;
        Identity = identity; Mode = mode; Attributes = attributes; Values = values;
    }

    public string Path { get; }
    public bool Exists { get; }
    public IReadOnlyDictionary<string, string?> Values { get; }
    internal GitRulesLocation Location { get; }
    internal string Hash { get; }
    internal GitRulesIdentity? Identity { get; }
    internal UnixFileMode? Mode { get; }
    internal FileAttributes Attributes { get; }
}
