using System.Text;

namespace GitExtensions.Linux;

public sealed record GitFileEncodingChoice(string Name, string DisplayName);
public sealed record GitDecodedFile(string Text, string EncodingName, bool HasBom, bool IsBinary, bool IsLossless);
public sealed record GitDecodedPatch(string Text, bool IsLossless);

/// <summary>File content codecs are independent of Git's commit encoding. No replacement fallback is permitted.</summary>
public static class GitFileEncodingCodec
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public static IReadOnlyList<GitFileEncodingChoice> AvailableEncodings { get; } = CreateChoices();

    public static Encoding GetEncoding(string? name = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (string.IsNullOrWhiteSpace(name)) { return Utf8; }
        try
        {
            Encoding encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (encoding.CodePage == 65000) { throw new NotSupportedException("UTF-7 is not a supported file encoding."); }
            return encoding.CodePage == 65001 ? Utf8 : encoding;
        }
        catch (ArgumentException exception) { throw new NotSupportedException($"Unknown file encoding '{name}'.", exception); }
    }

    public static bool SupportsPatchText(Encoding encoding)
    {
        const string syntax = "AZaz09 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~\r\n";
        return encoding.GetBytes(syntax).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(syntax));
    }

    /// <summary>A truncated preview may omit only an incomplete trailing encoded character; every cached byte is retained by its caller.</summary>
    public static GitDecodedFile DecodeFile(ReadOnlySpan<byte> bytes, string? defaultEncodingName = null, bool truncated = false)
    {
        Encoding encoding = GetEncoding(defaultEncodingName);
        int skip = 0;
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = GetEncoding("utf-32"); skip = 4; }
        else if (bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = GetEncoding("utf-32BE"); skip = 4; }
        else if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) { encoding = GetEncoding("utf-16"); skip = 2; }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) { encoding = GetEncoding("utf-16BE"); skip = 2; }
        else if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = Utf8; skip = 3; }
        bool unicode = encoding.CodePage is 1200 or 1201 or 12000 or 12001;
        if (!unicode && bytes.Contains((byte)0)) { return Binary(bytes, encoding, skip); }
        try
        {
            string text;
            if (truncated)
            {
                char[] characters = new char[encoding.GetMaxCharCount(bytes.Length - skip)];
                int count = encoding.GetDecoder().GetChars(bytes[skip..], characters, flush: false);
                text = new string(characters, 0, count);
            }
            else { text = encoding.GetString(bytes[skip..]); }
            byte[] encoded = encoding.GetBytes(text);
            bool lossless = encoded.AsSpan().SequenceEqual(bytes[skip..]);
            bool incompleteEnd = truncated && encoded.Length < bytes.Length - skip && bytes[skip..].StartsWith(encoded);
            if (text.Contains('\0') || !lossless && !incompleteEnd) { return Binary(bytes, encoding, skip); }
            return new(text, encoding.WebName, skip != 0, false, lossless);
        }
        catch (DecoderFallbackException) { return Binary(bytes, encoding, skip); }
        catch (EncoderFallbackException) { return Binary(bytes, encoding, skip); }
    }

    /// <summary>Git headers, paths and diagnostics are UTF-8; hunk contents retain the file's original bytes.</summary>
    public static GitDecodedPatch DecodePatch(ReadOnlySpan<byte> bytes, string encodingName, bool wordDiff = false)
    {
        Encoding content = GetEncoding(encodingName);
        StringBuilder text = new();
        PatchState state = new();
        bool lossless = true;
        int start = 0;
        while (start < bytes.Length)
        {
            int newline = bytes[start..].IndexOf((byte)'\n');
            int end = newline < 0 ? bytes.Length : start + newline + 1;
            ReadOnlySpan<byte> line = bytes[start..end];
            string shadow = Encoding.Latin1.GetString(line);
            bool body = IsContentLine(shadow, state, wordDiff);
            Encoding codec = body ? content : Utf8;
            try { text.Append(codec.GetString(line)); }
            catch (DecoderFallbackException)
            {
                // Preserve every byte in a reversible one-byte shadow for inspection; never invent U+FFFD.
                text.Append(shadow); lossless = false;
            }
            start = end;
        }
        string result = text.ToString();
        if (!SupportsPatchText(content)) { lossless = false; }
        if (lossless)
        {
            try { lossless = EncodePatchCore(result, encodingName, wordDiff).AsSpan().SequenceEqual(bytes); }
            catch (EncoderFallbackException) { lossless = false; }
        }
        return new(result, lossless);
    }

    public static byte[] EncodePatch(string text, string encodingName)
        => EncodePatchCore(text, encodingName, wordDiff: false);

    private static byte[] EncodePatchCore(string text, string encodingName, bool wordDiff)
    {
        ArgumentNullException.ThrowIfNull(text);
        Encoding content = GetEncoding(encodingName);
        if (!SupportsPatchText(content)) { throw new NotSupportedException("Partial patches require an ASCII-compatible file encoding; the original patch bytes remain available."); }
        using MemoryStream output = new();
        PatchState state = new();
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline + 1;
            string line = text[start..end];
            output.Write((IsContentLine(line, state, wordDiff) ? content : Utf8).GetBytes(line));
            start = end;
        }
        return output.ToArray();
    }

    private sealed class PatchState
    {
        internal bool InHunk;
        internal bool IsRange;
        internal bool InRangeFileHeader;
    }

    private static bool IsContentLine(string line, PatchState state, bool wordDiff)
    {
        string plain = GitAnsiColorParser.StripAnsi(line);
        if (plain.StartsWith("diff --", StringComparison.Ordinal))
        { state.InHunk = false; state.IsRange = false; state.InRangeFileHeader = false; return false; }
        if (plain.StartsWith("    @@ Metadata", StringComparison.Ordinal)
            || (state.IsRange || !state.InHunk) && plain.Length > 0 && char.IsAsciiDigit(plain[0]) && plain.Contains(": ", StringComparison.Ordinal))
        { state.IsRange = true; state.InHunk = false; state.InRangeFileHeader = false; return false; }
        if (state.IsRange)
        {
            // The outer comparison prefix precedes a nested patch. Commit metadata and
            // ## pathname ## headings are UTF-8, including raw newlines in a filename.
            string candidate = plain.StartsWith("    ", StringComparison.Ordinal) ? plain[4..] : plain;
            if (candidate.Length > 0 && candidate[0] is '-' or '+' or ' ') { candidate = candidate[1..]; }
            string heading = candidate.TrimStart(' ');
            if (state.InRangeFileHeader)
            {
                if (heading.TrimEnd('\r', '\n').EndsWith(" ##", StringComparison.Ordinal)) { state.InRangeFileHeader = false; }
                return false;
            }
            if (heading.StartsWith("## ", StringComparison.Ordinal))
            {
                state.InHunk = false;
                state.InRangeFileHeader = !heading.TrimEnd('\r', '\n').EndsWith(" ##", StringComparison.Ordinal);
                return false;
            }
            if (candidate.StartsWith("@@", StringComparison.Ordinal)) { state.InHunk = true; return true; }
            return state.InHunk;
        }
        if (plain.StartsWith("@@", StringComparison.Ordinal)) { state.InHunk = true; return true; }
        if (wordDiff && state.InHunk) { return !plain.StartsWith("\\ No newline at end of file", StringComparison.Ordinal); }
        return state.InHunk && plain.Length > 0 && (plain[0] is '-' or '+' or ' ');
    }

    private static GitDecodedFile Binary(ReadOnlySpan<byte> bytes, Encoding encoding, int skip)
        => new("Binary file\n" + Convert.ToHexString(bytes[..Math.Min(bytes.Length, 4096)]), encoding.WebName, skip != 0, true, false);

    private static IReadOnlyList<GitFileEncodingChoice> CreateChoices()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        List<Encoding> encodings = [Utf8, GetEncoding("us-ascii"), GetEncoding("utf-16"), GetEncoding("utf-16BE"), GetEncoding("utf-32"), GetEncoding("utf-32BE"), GetEncoding("iso-8859-1"), GetEncoding("windows-1252")];
        encodings.AddRange(Encoding.GetEncodings().Select(info => info.GetEncoding()).Where(encoding => encoding.IsSingleByte && SupportsPatchText(encoding)).OrderBy(encoding => encoding.EncodingName, StringComparer.Ordinal));
        return encodings.DistinctBy(encoding => encoding.CodePage).Select(encoding => new GitFileEncodingChoice(encoding.WebName, encoding.EncodingName)).ToArray();
    }
}
