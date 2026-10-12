using System.Text;
using GitExtensions.Linux;

internal static class FilePreviewScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("truncated UTF8 file previews retain complete characters at the real byte limit", Utf8BoundaryAsync),
        ("truncated UTF16 and UTF32 BOM previews omit only incomplete final characters", UnicodeBoundaryAsync),
        ("preview truncation never hides interior invalid bytes or an invalid complete EOF", InvalidSequencesAsync)
    ];

    private static async Task Utf8BoundaryAsync()
    {
        using Files files = new();
        const int limit = 2_000_000;
        string prefix = new('a', limit - 1);
        byte[] complete = new UTF8Encoding(false, true).GetBytes(prefix + "é\n");
        byte[] bytes = await files.PrefixAsync("utf8 λ.txt", complete, limit);
        Check(bytes.Length == limit && bytes[^1] == 0xc3, "The actual file read stops between the two UTF8 bytes.");
        GitDecodedFile preview = GitFileEncodingCodec.DecodeFile(bytes, "utf-8", truncated: true);
        Check(!preview.IsBinary && preview.Text == prefix && !preview.IsLossless, "Only the incomplete final codepoint is omitted, without replacement glyphs or a binary classification.");
        Check(preview.EncodingName == "utf-8" && !preview.HasBom, "Preview metadata still describes the actual codec.");
        GitDecodedFile completeEnd = GitFileEncodingCodec.DecodeFile(bytes, "utf-8");
        Check(completeEnd.IsBinary && !completeEnd.IsLossless, "The same bytes as a complete file remain invalid, rather than silently dropping their EOF.");
        GitDecodedFile full = GitFileEncodingCodec.DecodeFile(complete, "utf-8");
        Check(!full.IsBinary && full.IsLossless && full.Text == prefix + "é\n", "The actual untruncated file is valid and byte-reversible.");
        GitDecodedFile legacy = GitFileEncodingCodec.DecodeFile(bytes, "iso-8859-1", truncated: true);
        Check(!legacy.IsBinary && legacy.IsLossless && legacy.Text == prefix + "Ã", "The same remembered raw byte remains available to a different codec.");
        foreach (int length in new[] { 1, 2, 3 })
        {
            byte[] partial = Encoding.UTF8.GetBytes("🦊")[..length];
            GitDecodedFile empty = GitFileEncodingCodec.DecodeFile(partial, "utf-8", truncated: true);
            Check(!empty.IsBinary && !empty.IsLossless && empty.Text == "", "An incomplete first rune does not invent a replacement character.");
        }
        GitDecodedFile exact = GitFileEncodingCodec.DecodeFile(Encoding.UTF8.GetBytes("été 🦊"), "utf-8", truncated: true);
        Check(exact.Text == "été 🦊" && exact.IsLossless && !exact.IsBinary, "A preview ending at a character boundary remains lossless for its stored bytes.");
        await files.UnchangedAsync();
    }

    private static async Task UnicodeBoundaryAsync()
    {
        using Files files = new();
        Encoding[] encodings = [new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true)];
        foreach (Encoding encoding in encodings)
        {
            byte[] complete = [.. encoding.GetPreamble(), .. encoding.GetBytes("a🦊z")];
            int prefixLength = encoding.GetPreamble().Length + encoding.GetByteCount("a");
            for (int pending = 1; pending < 4; pending++)
            {
                byte[] bytes = await files.PrefixAsync(encoding.WebName + "-" + pending + ".txt", complete, prefixLength + pending);
                GitDecodedFile preview = GitFileEncodingCodec.DecodeFile(bytes, "windows-1252", truncated: true);
                Check(preview.HasBom && preview.EncodingName == encoding.WebName, "The real BOM wins over the session default during preview decoding.");
                Check(preview.Text == "a" && !preview.IsBinary && !preview.IsLossless, "UTF16 surrogate/odd-byte and UTF32 partial-unit tails omit only the pending character.");
                Check(GitFileEncodingCodec.DecodeFile(bytes, "windows-1252").IsBinary, "A complete EOF containing that pending tail stays invalid.");
            }
            byte[] boundary = await files.PrefixAsync(encoding.WebName + "-complete.txt", complete, prefixLength + 4);
            GitDecodedFile exact = GitFileEncodingCodec.DecodeFile(boundary, "windows-1252", truncated: true);
            Check(exact.Text == "a🦊" && exact.HasBom && exact.IsLossless && !exact.IsBinary, "A full surrogate pair/codepoint survives with exact bytes.");
            GitDecodedFile full = GitFileEncodingCodec.DecodeFile(complete);
            Check(full.Text == "a🦊z" && full.IsLossless && !full.IsBinary, "Full Unicode file decoding retains the original behavior.");
        }
        await files.UnchangedAsync();
    }

    private static async Task InvalidSequencesAsync()
    {
        using Files files = new();
        (string Name, byte[] Bytes, string Encoding)[] invalid =
        [
            ("invalid-utf8-interior", [0x61, 0xe2, 0x28, 0xa1, 0x7a], "utf-8"),
            ("invalid-utf8-end", [0x61, 0xff], "utf-8"),
            ("invalid-utf16-interior", [0xff, 0xfe, 0x61, 0, 0, 0xd8, 0x78, 0, 0x7a, 0], "utf-8"),
            ("invalid-utf32-codepoint", [0xff, 0xfe, 0, 0, 0x61, 0, 0, 0, 0, 0, 0x11, 0, 0x7a, 0, 0, 0], "utf-8"),
            ("nul-utf8", [0x61, 0, 0x7a], "utf-8"),
            ("nul-utf16", [0xff, 0xfe, 0x61, 0, 0, 0, 0x7a, 0], "utf-8")
        ];
        foreach ((string name, byte[] source, string encoding) in invalid)
        {
            byte[] bytes = await files.PrefixAsync(name + ".txt", source, source.Length);
            foreach (bool truncated in new[] { false, true })
            {
                GitDecodedFile decoded = GitFileEncodingCodec.DecodeFile(bytes, encoding, truncated);
                Check(decoded.IsBinary && !decoded.IsLossless && !decoded.Text.Contains('\ufffd'), "The strict decoder must reject invalid interior/full codepoints and NUL even when more file bytes exist.");
            }
        }
        await files.UnchangedAsync();
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class Files : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gitextensions-file-preview-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, byte[]> _files = [];
        public Files() => Directory.CreateDirectory(_directory);
        public async Task<byte[]> PrefixAsync(string name, byte[] bytes, int length)
        {
            string path = Path.Combine(_directory, name);
            _files[path] = bytes.ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            await using FileStream stream = File.OpenRead(path);
            byte[] prefix = new byte[Math.Min(length, (int)stream.Length)];
            await stream.ReadExactlyAsync(prefix);
            return prefix;
        }
        public async Task UnchangedAsync()
        {
            foreach ((string path, byte[] bytes) in _files)
            { Check((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(bytes), "Preview decoding must leave every original file byte intact."); }
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
