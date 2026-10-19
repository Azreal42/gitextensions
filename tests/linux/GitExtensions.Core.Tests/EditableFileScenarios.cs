using System.Text;
using GitExtensions.Linux;

internal static class EditableFileScenarios
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("editable files preserve original BOM and strict effective codecs without adding a newline", CodecsAsync),
        ("editable files unchanged saves retain exact bytes timestamp and mode", NoopAsync),
        ("editable file snapshots never expose mutable original bytes", ImmutableAsync),
        ("editable files keep mixed EOL and missing final EOL exactly", EndingsAsync),
        ("editable files reject unrepresentable edits before touching bytes or permissions", StrictAsync),
        ("editable files expose binary and invalid decoding readonly and refuse save", BinaryAsync),
        ("editable files reject changed bytes deletion replacement and permission changes", ConflictsAsync),
        ("editable files preserve symbolic links and original readonly modes", LinkedAsync),
        ("editable files refuse retargeted file and parent links without touching either target", RetargetAsync),
        ("editable files never create a missing file and refuse directories and cycles", MissingAsync),
        ("editable files cancellation before read and save leaves disk untouched", CancelAsync),
        ("editable file competing drafts cannot silently overwrite an accepted save", DraftsAsync),
        ("editable files treat newline metacharacters and Unicode in filenames literally", LiteralNamesAsync)
    ];
    private static async Task CodecsAsync()
    {
        using Fixture f = new();
        foreach ((string name, bool bom) in new[] { ("utf-8", false), ("utf-8", true), ("utf-16", false), ("utf-16", true),
            ("utf-16BE", true), ("utf-32", true), ("utf-32BE", true), ("windows-1252", false) })
        {
            Encoding codec = GitFileEncodingCodec.GetEncoding(name);
            byte[] prefix = !bom ? [] : name switch { "utf-8" => [239,187,191], "utf-16" => [255,254], "utf-16BE" => [254,255],
                "utf-32" => [255,254,0,0], _ => [0,0,254,255] };
            byte[] original = [.. prefix, .. codec.GetBytes("Café €\r\nend")]; await File.WriteAllBytesAsync(f.Path, original);
            EditableFileSnapshot baseline = await f.Store.ReadAsync(f.Path, bom ? "us-ascii" : name);
            Check(baseline.CanEdit && baseline.Text == "Café €\r\nend" && baseline.HasBom == bom, "A BOM takes precedence over the fallback and never becomes document text.");
            EditableFileSnapshot saved = await f.Store.SaveAsync(baseline, baseline.Text + "!");
            Check(File.ReadAllBytes(f.Path).SequenceEqual(prefix.Concat(codec.GetBytes("Café €\r\nend!"))), "Save emits exactly the original BOM and actual codec, including UTF8 without BOM.");
            Check(saved.Text == baseline.Text + "!" && saved.EncodingName == baseline.EncodingName, "The verified post-write baseline uses the actual codec.");
        }
    }
    private static async Task NoopAsync()
    {
        using Fixture f = new(); byte[] original = "no final newline"u8.ToArray(); await File.WriteAllBytesAsync(f.Path, original);
        DateTime stamp = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc); File.SetLastWriteTimeUtc(f.Path, stamp);
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(f.Path);
        EditableFileSnapshot snapshot = await f.Store.ReadAsync(f.Path); await f.Store.SaveAsync(snapshot, snapshot.Text);
        Check(File.GetLastWriteTimeUtc(f.Path) == stamp && File.ReadAllBytes(f.Path).SequenceEqual(original), "An unchanged Save does not rewrite the file.");
        Check(OperatingSystem.IsWindows() || File.GetUnixFileMode(f.Path) == mode, "An unchanged Save never adjusts modes.");
    }
    private static async Task ImmutableAsync()
    {
        using Fixture f = new(); await File.WriteAllBytesAsync(f.Path, [239,187,191,65]);
        EditableFileSnapshot snapshot = await f.Store.ReadAsync(f.Path); byte[] copy = snapshot.GetBytes(); Array.Fill(copy, (byte)0);
        Check(snapshot.GetBytes().SequenceEqual(new byte[] {239,187,191,65}), "Mutating a public byte copy cannot mutate the baseline.");
        await f.Store.SaveAsync(snapshot, "B"); Check(File.ReadAllBytes(f.Path).SequenceEqual(new byte[] {239,187,191,66}), "The private BOM remains intact.");
    }
    private static async Task EndingsAsync()
    {
        using Fixture f = new(); const string mixed = "first\r\nsecond\nthird\rlast";
        await File.WriteAllBytesAsync(f.Path, Encoding.UTF8.GetBytes(mixed));
        EditableFileSnapshot snapshot = await f.Store.ReadAsync(f.Path); await f.Store.SaveAsync(snapshot, mixed.Replace("last", "tail", StringComparison.Ordinal));
        Check(File.ReadAllBytes(f.Path).SequenceEqual(Encoding.UTF8.GetBytes("first\r\nsecond\nthird\rtail")), "The generic writer performs no rules-file final-EOL normalization.");
    }
    private static async Task StrictAsync()
    {
        using Fixture f = new(); byte[] bytes = [0x63,0x61,0x66,0xe9]; await File.WriteAllBytesAsync(f.Path, bytes);
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(f.Path);
        EditableFileSnapshot snapshot = await f.Store.ReadAsync(f.Path, "windows-1252");
        await Expect<EncoderFallbackException>(() => f.Store.SaveAsync(snapshot, "unrepresentable λ"));
        await Expect<ArgumentException>(() => f.Store.SaveAsync(snapshot, "bad\0text"));
        Check(File.ReadAllBytes(f.Path).SequenceEqual(bytes) && (OperatingSystem.IsWindows() || File.GetUnixFileMode(f.Path) == mode), "Encoding validation precedes writes and temporary permission changes.");
        await Expect<NotSupportedException>(() => f.Store.ReadAsync(f.Path, "not-a-real-codec"));
    }
    private static async Task BinaryAsync()
    {
        using Fixture f = new();
        foreach (byte[] bytes in new[] { new byte[] {0,1,255}, new byte[] {0xff,0xfe,1}, new byte[] {0xc3,0x28} })
        {
            await File.WriteAllBytesAsync(f.Path, bytes); EditableFileSnapshot snapshot = await f.Store.ReadAsync(f.Path);
            Check(snapshot.IsBinary && !snapshot.CanEdit, "Invalid/truncated decoding is a readonly inspection, never replacement-character text.");
            await Expect<InvalidOperationException>(() => f.Store.SaveAsync(snapshot, "replacement"));
            Check(File.ReadAllBytes(f.Path).SequenceEqual(bytes), "Saving a binary/invalid buffer is refused before any write.");
        }
    }
    private static async Task ConflictsAsync()
    {
        using Fixture f = new();
        foreach (string kind in new[] { "bytes", "delete", "replace", "mode" })
        {
            await File.WriteAllTextAsync(f.Path, "original"); EditableFileSnapshot snapshot = await f.Store.ReadAsync(f.Path);
            switch (kind)
            {
                case "bytes": await File.WriteAllTextAsync(f.Path, "external"); break;
                case "delete": File.Delete(f.Path); break;
                case "replace": await File.WriteAllTextAsync(f.Path + ".replacement", "original"); File.Move(f.Path + ".replacement", f.Path, true); break;
                case "mode": if (OperatingSystem.IsWindows()) { continue; } File.SetUnixFileMode(f.Path, snapshot.Mode!.Value ^ UnixFileMode.UserExecute); break;
            }
            await Expect<IOException>(() => f.Store.SaveAsync(snapshot, "my draft"));
            Check(kind == "delete" ? !File.Exists(f.Path) : File.ReadAllText(f.Path) != "my draft", "A conflicting destination remains exactly the external actor's file.");
        }
    }
    private static async Task LinkedAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using Fixture f = new(); string target = f.Path + ".target"; await File.WriteAllTextAsync(target, "original");
        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.GroupRead; File.SetUnixFileMode(target, mode);
        File.CreateSymbolicLink(f.Path, target); EditableFileSnapshot snapshot = await f.Store.ReadAsync(f.Path);
        await f.Store.SaveAsync(snapshot, "updated");
        Check(new FileInfo(f.Path).LinkTarget == target && File.ReadAllText(target) == "updated" && File.GetUnixFileMode(target) == mode,
            "The shared writer updates the followed inode while preserving the symlink and restoring its readonly mode.");
    }
    private static async Task RetargetAsync()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using Fixture f = new(); string first = f.Path + ".one", second = f.Path + ".two";
        await File.WriteAllTextAsync(first, "one"); await File.WriteAllTextAsync(second, "two"); File.CreateSymbolicLink(f.Path, first);
        EditableFileSnapshot snapshot = await f.Store.ReadAsync(f.Path); File.Delete(f.Path); File.CreateSymbolicLink(f.Path, second);
        await Expect<IOException>(() => f.Store.SaveAsync(snapshot, "draft"));
        Check(File.ReadAllText(first) == "one" && File.ReadAllText(second) == "two", "A retargeted file link never redirects an old draft.");
        File.Delete(f.Path); Directory.CreateDirectory(first + ".dir"); Directory.CreateDirectory(second + ".dir");
        await File.WriteAllTextAsync(System.IO.Path.Combine(first + ".dir", "file"), "one"); await File.WriteAllTextAsync(System.IO.Path.Combine(second + ".dir", "file"), "two");
        Directory.CreateSymbolicLink(f.Path, first + ".dir"); string nested = System.IO.Path.Combine(f.Path, "file");
        snapshot = await f.Store.ReadAsync(nested); Directory.Delete(f.Path); Directory.CreateSymbolicLink(f.Path, second + ".dir");
        await Expect<IOException>(() => f.Store.SaveAsync(snapshot, "draft")); Check(File.ReadAllText(nested) == "two", "A replaced parent link is refused as well.");
    }
    private static async Task MissingAsync()
    {
        using Fixture f = new(); await Expect<FileNotFoundException>(() => f.Store.ReadAsync(f.Path)); Check(!File.Exists(f.Path), "A read never creates a new file.");
        await Expect<IOException>(() => f.Store.ReadAsync(f.Root));
        if (!OperatingSystem.IsWindows())
        { File.CreateSymbolicLink(f.Path, f.Path); await Expect<IOException>(() => f.Store.ReadAsync(f.Path)); }
    }
    private static async Task CancelAsync()
    {
        using Fixture f = new(); await File.WriteAllTextAsync(f.Path, "original"); EditableFileSnapshot snapshot = await f.Store.ReadAsync(f.Path);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Expect<OperationCanceledException>(() => f.Store.ReadAsync(f.Path, cancellationToken: cancelled.Token));
        await Expect<OperationCanceledException>(() => f.Store.SaveAsync(snapshot, "draft", cancelled.Token));
        Check(File.ReadAllText(f.Path) == "original", "Pre-cancellation starts no writer.");
    }
    private static async Task DraftsAsync()
    {
        using Fixture f = new(); await File.WriteAllTextAsync(f.Path, "original");
        EditableFileSnapshot first = await f.Store.ReadAsync(f.Path), second = await f.Store.ReadAsync(f.Path);
        first = await f.Store.SaveAsync(first, "accepted"); await Expect<IOException>(() => f.Store.SaveAsync(second, "stale"));
        Check(File.ReadAllText(f.Path) == "accepted", "A stale draft never rebases silently onto a prior write.");
        await f.Store.SaveAsync(first, "next"); Check(File.ReadAllText(f.Path) == "next", "The returned verified baseline can be saved again.");
    }
    private static async Task LiteralNamesAsync()
    {
        using Fixture f = new(); string path = System.IO.Path.Combine(f.Root, "line\n$(not-executed); λ[1].txt");
        await File.WriteAllTextAsync(path, "original"); EditableFileSnapshot snapshot = await f.Store.ReadAsync(path);
        await f.Store.SaveAsync(snapshot, "literal save");
        Check(File.ReadAllText(path) == "literal save" && Directory.EnumerateFiles(f.Root).Count() == 1, "No shell, normalization or sibling destination is involved.");
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ge-file-store-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Root, "literal [1] λ file.txt");
        internal EditableFileStore Store { get; } = new();
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task Expect<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
