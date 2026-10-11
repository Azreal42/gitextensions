using System.Text;

namespace GitExtensions.Linux;

public sealed partial class GitRepository
{
    /// <summary>Git supports UTF-8 and ASCII-compatible single-byte legacy commit encodings, not arbitrary text codecs.</summary>
    public async Task<Encoding> GetCommitMessageEncodingAsync(CancellationToken cancellationToken = default)
    {
        string? name = (await GetConfigAsync("^i18n\\.commitencoding$", cancellationToken: cancellationToken).ConfigureAwait(false)).LastOrDefault().Value;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(name) ? new UTF8Encoding(false, true)
                : Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException exception) { throw new NotSupportedException($"Unknown Git commit encoding: {name}.", exception); }
        const string gitAscii = "0123456789AZaz !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~\r\n";
        if (encoding.CodePage != Encoding.UTF8.CodePage && !encoding.IsSingleByte
            || !encoding.GetBytes(gitAscii).SequenceEqual(Encoding.ASCII.GetBytes(gitAscii)))
        {
            throw new NotSupportedException($"Git commit encoding '{name}' must be UTF-8 or an ASCII-compatible single-byte encoding.");
        }
        // UTF-8 drafts/templates and stdin must not acquire a BOM.
        return encoding.CodePage == Encoding.UTF8.CodePage ? new UTF8Encoding(false, true) : encoding;
    }

    private Task<GitResult> CommitEncodedAsync(IReadOnlyList<string> arguments, string? message, CancellationToken cancellationToken)
        => WithMutationAsync(async token =>
        {
            await EnsureWorktreeAsync(token).ConfigureAwait(false);
            Encoding encoding = await GetCommitMessageEncodingAsync(token).ConfigureAwait(false);
            byte[]? bytes = message is null ? null : encoding.GetBytes(message);
            GitBinaryResult binary = await _runner.RunBinaryAsync(Path, [.. GlobalOptions, "-c", "i18n.logOutputEncoding=UTF-8", .. arguments], token, bytes).ConfigureAwait(false);
            GitResult result = new(binary.ExitCode, Encoding.UTF8.GetString(binary.StandardOutput), binary.StandardError);
            if (!result.Success) { throw new GitCommandException(arguments, result); }
            return result;
        }, cancellationToken);
}
