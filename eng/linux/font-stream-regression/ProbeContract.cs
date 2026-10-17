using System.Security.Cryptography;
using System.Text.Json;

namespace GitExtensions.FontStreamRegression;

internal sealed record BinaryPin(string Path, string Kind, long Bytes, string Sha256, int Mode);
internal sealed record ModulePin(string Name, string Version, string Mvid, string Path, string Sha256);
internal sealed record ProbeInput(int Version, string Family, string FontPath, long FontBytes, string FontSha256,
    string BackendSha256, string NativeSha256, BinaryPin[] Binaries);

internal sealed class ProbeReceipt
{
    public string Status { get; set; } = "STARTED";
    public string? Error { get; set; }
    public int Pid { get; set; } = Environment.ProcessId;
    public string InputSha256 { get; set; } = "";
    public string Family { get; set; } = "";
    public string ResolvedFamily { get; set; } = "";
    public int Exports { get; set; }
    public long VerifiedBytes { get; set; }
    public int GcPasses { get; set; }
    public long WorkerAllocatedBytes { get; set; }
    public bool GcOverlap { get; set; }
    public bool WorkerJoined { get; set; }
    public bool WorkerEndedEarly { get; set; }
    public string? WorkerError { get; set; }
    public int[] CollectionsBefore { get; set; } = [];
    public int[] CollectionsAfter { get; set; } = [];
    public ModulePin[] Modules { get; set; } = [];
    public string NativePath { get; set; } = "";
    public string NativeSha256 { get; set; } = "";
    public string RuntimePath { get; set; } = "";
    public string RuntimeSha256 { get; set; } = "";
    public bool BinaryInputsUnchanged { get; set; }
}

internal static class ProbeProtocol
{
    internal const int Exports = 256;
    internal const int MaxFontBytes = 32 * 1024 * 1024;
    internal const int MaxLogBytes = 512 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string HashFile(string path)
    { using FileStream stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }

    internal static byte[] ReadFont(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        { throw new IOException("The private source font must be a regular, non-symlink file."); }
        using FileStream stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > MaxFontBytes) { throw new IOException("Font source exceeds the finite 32 MiB fixture budget."); }
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) { throw new IOException("Font source changed size while being read."); }
        return bytes;
    }

    internal static FileStream CreatePrivate(string path)
    {
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
        if (OperatingSystem.IsLinux()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        return new FileStream(path, options);
    }

    internal static void WriteJson(string path, object value)
    { using FileStream stream = CreatePrivate(path); JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }

    internal static BinaryPin[] Snapshot(string directory)
    {
        List<BinaryPin> rows = [];
        void Visit(string current)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new IOException("Probe closure cannot contain symlinks."); }
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                int mode = OperatingSystem.IsLinux() ? (int)File.GetUnixFileMode(path) : 0;
                rows.Add(new(Path.GetRelativePath(directory, path), isDirectory ? "directory" : "file",
                    isDirectory ? 0 : new FileInfo(path).Length, isDirectory ? "" : HashFile(path), mode));
                if (isDirectory) { Visit(path); }
            }
        }
        Visit(directory); return rows.OrderBy(row => row.Path, StringComparer.Ordinal).ToArray();
    }

    internal static string NativeHash(string directory)
    {
        string[] candidates = new[] { "libSkiaSharp.so", "runtimes/linux-x64/native/libSkiaSharp.so" }
            .Select(path => Path.Combine(directory, path)).Where(File.Exists).ToArray();
        string[] hashes = candidates.Select(HashFile).Distinct(StringComparer.Ordinal).ToArray();
        if (hashes.Length != 1) { throw new IOException("A unique Linux x64 Skia native payload must be present."); }
        return hashes[0];
    }

    internal static void Require(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}
