namespace GitExtensions.Linux;

/// <summary>A complete immutable disk baseline. Public byte access returns a copy.</summary>
public sealed class EditableFileSnapshot
{
    private readonly byte[] _bytes;
    internal EditableFileSnapshot(string path, GitRulesLocation location, GitRulesIdentity identity, byte[] bytes,
        GitDecodedFile decoded, UnixFileMode? mode, FileAttributes attributes)
    {
        Path = path; Location = location; Identity = identity; _bytes = bytes.ToArray();
        ContentHash = GitRulesFiles.Hash(bytes); Text = decoded.Text; EncodingName = decoded.EncodingName;
        IsBinary = decoded.IsBinary; CanEdit = decoded.IsLossless && !decoded.IsBinary;
        Bom = GitRulesFiles.Bom(bytes); Mode = mode; Attributes = attributes;
    }
    public string Path { get; }
    public string ResolvedPath => Location.Path;
    public string ContentHash { get; }
    public string Text { get; }
    public string EncodingName { get; }
    public bool HasBom => Bom.Length != 0;
    public bool IsBinary { get; }
    public bool CanEdit { get; }
    public UnixFileMode? Mode { get; }
    public byte[] GetBytes() => _bytes.ToArray();
    internal GitRulesLocation Location { get; }
    internal GitRulesIdentity Identity { get; }
    internal byte[] Bom { get; }
    internal FileAttributes Attributes { get; }
}

/// <summary>The writer started; callers must retain possible disk effects even when verification failed.</summary>
public sealed class EditableFileWriteException : IOException
{
    internal EditableFileWriteException(Exception inner) : base("The file write started but could not be verified. Reload the file before saving again. " + inner.Message, inner) { }
    public bool MayHaveChangedFile => true;
}

internal sealed record EditableFileWriteBaseline(string Path, string ResolvedPath, GitRulesLocation Location,
    GitRulesIdentity? Identity, string ContentHash, UnixFileMode? Mode, FileAttributes Attributes);
