using System.Text;

namespace GitExtensions.Linux;

/// <summary>Windows find-in-files settings. Expressions use Git's basic regexp mode unless UserArguments select another mode.</summary>
public sealed record GitGrepOptions(string Expression, string? Revision = null, bool Cached = false,
    bool IgnoreCase = false, bool MatchWholeWord = false, string? UserArguments = null,
    IReadOnlyList<string>? Paths = null, string? EncodingName = null);

/// <summary>Git's line contents and byte column are retained independently of their selected display encoding.</summary>
public sealed record GitGrepMatch(string Path, ReadOnlyMemory<byte> PathBytes, bool HasLosslessPath,
    int LineNumber, int? ByteColumn, ReadOnlyMemory<byte> LineBytes, GitDecodedFile Decoded, bool IsContext)
{
    public string Text => Decoded.Text;
    public bool IsLossless => Decoded.IsLossless;
    public string EncodingName => Decoded.EncodingName;
    /// <summary>One-based UTF-16 column for a text editor; null when the byte offset cannot be decoded exactly.</summary>
    public int? TextColumn
    {
        get
        {
            if (!IsLossless || ByteColumn is not int column || column < 1 || column > LineBytes.Length + 1) { return null; }
            GitDecodedFile prefix = GitFileEncodingCodec.DecodeFile(LineBytes.Span[..(column - 1)], EncodingName);
            return prefix.IsLossless ? prefix.Text.Length + 1 : null;
        }
    }
}

/// <summary>A native binary-file match identifies the source path without inventing a line or column.</summary>
public sealed record GitGrepBinaryMatch(string Path, ReadOnlyMemory<byte> PathBytes, bool HasLosslessPath);

/// <summary>Revision is the resolved immutable commit hash, or null for the worktree/index.</summary>
public sealed record GitGrepResult(string? Revision, bool Cached, string EncodingName,
    IReadOnlyList<GitGrepMatch> Matches, string StandardError)
{
    public IReadOnlyList<GitGrepBinaryMatch> BinaryMatches { get; init; } = [];
    public bool HasMatches => BinaryMatches.Count > 0 || Matches.Any(match => !match.IsContext);
}

internal static class GitGrepArguments
{
    // Quote parsing never invokes a shell or expands variables. Preserve regexp backslashes
    // unless they quote a delimiter, whitespace or another backslash.
    internal static string[] Split(string text)
    {
        List<string> result = []; StringBuilder token = new(); char quote = '\0'; bool started = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\0') { throw new ArgumentException("Git grep arguments cannot contain NUL."); }
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == quote || quote != '\'' && (text[i + 1] is '\\' or '"' or '\'' || quote == '\0' && char.IsWhiteSpace(text[i + 1]))))
            { token.Append(text[++i]); started = true; continue; }
            if (quote != '\0')
            { if (c == quote) { quote = '\0'; } else { token.Append(c); } started = true; continue; }
            if (c is '\'' or '"') { quote = c; started = true; continue; }
            if (char.IsWhiteSpace(c))
            { if (started) { result.Add(token.ToString()); token.Clear(); started = false; } continue; }
            token.Append(c); started = true;
        }
        if (quote != '\0') { throw new ArgumentException("An unmatched quote appears in the Git grep arguments."); }
        if (started) { result.Add(token.ToString()); }
        return result.ToArray();
    }

    internal static bool IsExpression(string expression)
    {
        // Mirrors the Windows -e expression detection without interpreting an ordinary regexp as arguments.
        for (int i = 0; i + 2 < expression.Length; i++)
        {
            if ((i == 0 || char.IsWhiteSpace(expression[i - 1])) && expression[i] == '-' && expression[i + 1] == 'e' && char.IsWhiteSpace(expression[i + 2])) { return true; }
        }
        return false;
    }
}
