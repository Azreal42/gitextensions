using System.Buffers;
using System.Text;

namespace GitExtensions.Linux;

/// <summary>A command-specific text projection. The runner retains the original streams and exit code.</summary>
public sealed record GitOutputDecoding(string StandardOutput, string StandardError)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];

    internal static GitOutputDecoding Utf8(GitBinaryResult result)
    {
        List<string> warnings = [];
        string output = GitOutputText.Utf8(result.StandardOutput, "stdout", warnings);
        string error = GitOutputText.Utf8(result.StandardErrorBytes, "stderr", warnings);
        return new(output, error) { Warnings = warnings };
    }
}

internal static class GitOutputText
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    internal static string Utf8(ReadOnlySpan<byte> bytes, string stream, ICollection<string> warnings)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { }
        StringBuilder text = new(); bool escaped = false;
        while (!bytes.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf8(bytes, out Rune rune, out int consumed);
            if (status == OperationStatus.Done) { text.Append(rune.ToString()); bytes = bytes[consumed..]; }
            else
            {
                text.Append("\\x").Append(bytes[0].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                bytes = bytes[1..]; escaped = true;
            }
        }
        if (escaped)
        {
            string warning = "Git " + stream + " contains bytes outside its declared UTF-8 text fields; they are displayed as \\xNN and retained in the raw stream.";
            if (!warnings.Contains(warning)) { warnings.Add(warning); }
        }
        return text.ToString();
    }
}
