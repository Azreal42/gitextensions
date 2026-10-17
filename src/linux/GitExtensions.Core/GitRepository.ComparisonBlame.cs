using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Blames the actual captured endpoint bytes; an index source is never substituted by HEAD or the working file.</summary>
    public async Task<GitFileBlame> GetComparisonFileBlameAsync(GitCompareRevision source, string path,
        GitFileBlameOptions? options = null, CancellationToken cancellationToken = default, string? fileEncodingName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(source.Kind)) { throw new ArgumentOutOfRangeException(nameof(source)); }
        cancellationToken.ThrowIfCancellationRequested();
        path = ValidatePath(path);
        if (source.Kind == GitCompareRevisionKind.EmptyTree)
        { return new(path, null, [], []) { Source = source with { Revision = "" } }; }
        if (source.Kind == GitCompareRevisionKind.Commit)
        {
            GitFileBlame committed = await GetFileBlameAsync(path, source.Revision, options, cancellationToken, fileEncodingName).ConfigureAwait(false);
            return committed with { Source = source with { Revision = committed.Revision! } };
        }
        options ??= new();
        byte[] bytes = await GetComparisonFileBytesAsync(source, path, cancellationToken).ConfigureAwait(false);
        Encoding encoding = GitFileEncodingCodec.GetEncoding(fileEncodingName ?? await GetFileEncodingNameAsync(cancellationToken).ConfigureAwait(false));
        List<string> arguments = ["--no-optional-locks", "--literal-pathspecs", "blame", "--encoding=UTF-8", "--line-porcelain"];
        if (options.IgnoreWhitespace) { arguments.Add("-w"); }
        if (options.DetectMoves) { arguments.Add("-M"); }
        if (options.DetectCopies) { arguments.Add("-C"); }
        arguments.AddRange(["--contents", "-", "--", path]);
        GitBinaryResult result = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, .. arguments], cancellationToken, standardInput: bytes).ConfigureAwait(false);
        if (!result.Success) { throw new GitCommandException(arguments, new(result.ExitCode, "", result.StandardError)); }
        string output = DecodeFileBlameOutput(result.StandardOutput, encoding, cancellationToken);
        return ParseFileBlame(path, target: null, output, bytes, encoding, cancellationToken)
            with { Source = source with { Revision = "" } };
    }
}
