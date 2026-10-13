using System.Text;

namespace GitExtensions.Linux;

/// <summary>Decode only Git's identified subject fields; paths, external programs and other text remain UTF-8.</summary>
internal static class GitRebaseOutputDecoder
{
    private enum SubjectKind { Commit, Todo, Log }

    internal static Func<GitBinaryResult, GitOutputDecoding> ForEncoding(string encodingName, string? todoEncodingName = null, string? logEncodingName = null)
    {
        Encoding commit = GitRebaseEditor.GetStrictEncoding(encodingName);
        Encoding todo = GitRebaseEditor.GetStrictEncoding(todoEncodingName ?? encodingName);
        Encoding log = GitRebaseEditor.GetStrictEncoding(logEncodingName ?? encodingName);
        return result =>
        {
            List<string> warnings = [];
            string output = Decode(result.StandardOutput, commit, todo, log, standardError: false, warnings);
            string error = Decode(result.StandardErrorBytes, commit, todo, log, standardError: true, warnings);
            return new(output, error) { Warnings = warnings };
        };
    }

    private static string Decode(ReadOnlySpan<byte> bytes, Encoding commit, Encoding todo, Encoding log, bool standardError, ICollection<string> warnings)
    {
        StringBuilder text = new(); string stream = standardError ? "stderr" : "stdout";
        while (!bytes.IsEmpty)
        {
            int end = bytes.IndexOfAny((byte)'\r', (byte)'\n');
            if (end < 0) { end = bytes.Length; }
            ReadOnlySpan<byte> line = bytes[..end];
            int prefix = line.StartsWith("\u001b[K"u8) ? 3 : line.StartsWith("\u001b[2K"u8) ? 4 : 0;
            if (SubjectField(line[prefix..], standardError, out int start, out int length, out SubjectKind kind))
            {
                Encoding encoding = kind switch { SubjectKind.Todo => todo, SubjectKind.Log => log, _ => commit };
                start += prefix;
                text.Append(GitOutputText.Utf8(line[..start], stream, warnings));
                try { text.Append(encoding.GetString(line.Slice(start, length))); }
                catch (DecoderFallbackException)
                {
                    foreach (byte value in line.Slice(start, length)) { text.Append("\\x").Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)); }
                    string warning = "Git " + stream + " has a subject that is invalid in its declared " + encoding.WebName + " codec; its bytes are displayed as \\xNN and retained.";
                    if (!warnings.Contains(warning)) { warnings.Add(warning); }
                }
                text.Append(GitOutputText.Utf8(line[(start + length)..], stream, warnings));
            }
            else { text.Append(GitOutputText.Utf8(line, stream, warnings)); }
            bytes = bytes[end..];
            while (!bytes.IsEmpty && bytes[0] is (byte)'\r' or (byte)'\n') { text.Append((char)bytes[0]); bytes = bytes[1..]; }
        }
        return text.ToString();
    }

    private static bool SubjectField(ReadOnlySpan<byte> line, bool standardError, out int start, out int length, out SubjectKind kind)
    {
        start = length = 0;
        kind = SubjectKind.Commit;
        if (standardError)
        {
            if (HashSubject(line, "error: could not apply "u8, out start))
            { length = line.Length - start; return true; }
            if (HashSubject(line, "Could not apply "u8, out start)
                || HashSubject(line, "Stopped at "u8, out start))
            { kind = SubjectKind.Todo; length = line.Length - start; return true; }
            if (line.StartsWith("Stopped at "u8))
            {
                int hashEnd = "Stopped at "u8.Length;
                while (hashEnd < line.Length && IsHex(line[hashEnd])) { hashEnd++; }
                if (IsHash(line["Stopped at "u8.Length..hashEnd]) && line[hashEnd..].StartsWith(" ("u8) && line[^1] == ')')
                { start = hashEnd + 2; length = line.Length - start - 1; return true; }
            }
        }
        else
        {
            if (line.StartsWith("Applying: "u8)) { start = "Applying: "u8.Length; length = line.Length - start; return true; }
            if (line.StartsWith("Patch failed at "u8))
            {
                int number = "Patch failed at "u8.Length, end = number;
                while (end < line.Length && line[end] is >= (byte)'0' and <= (byte)'9') { end++; }
                if (end > number && end < line.Length && line[end] == ' ')
                { start = end + 1; length = line.Length - start; return true; }
            }
            if (line.Length > 0 && line[0] == '[')
            {
                int closing = line.IndexOf("] "u8);
                if (closing > 0)
                {
                    int hash = line[..closing].LastIndexOf((byte)' ') + 1;
                    if (hash > 1 && IsHash(line[hash..closing]))
                    { kind = SubjectKind.Log; start = closing + 2; length = line.Length - start; return true; }
                }
            }
        }
        return false;
    }

    private static bool HashSubject(ReadOnlySpan<byte> line, ReadOnlySpan<byte> prefix, out int start)
    {
        start = 0;
        if (!line.StartsWith(prefix)) { return false; }
        int hashEnd = prefix.Length;
        while (hashEnd < line.Length && IsHex(line[hashEnd])) { hashEnd++; }
        if (!IsHash(line[prefix.Length..hashEnd]) || !line[hashEnd..].StartsWith("... "u8)) { return false; }
        start = hashEnd + 4; return true;
    }

    private static bool IsHash(ReadOnlySpan<byte> hash)
    {
        if (hash.Length is < 4 or > 64) { return false; }
        foreach (byte value in hash) { if (!IsHex(value)) { return false; } }
        return true;
    }
    private static bool IsHex(byte value) => value is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
}
