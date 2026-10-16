using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitExtensions.Linux;

public enum GitRulesFileKind { Ignore, LocalExclude, Attributes, MailMap }

/// <summary>An immutable editing baseline, including the original bytes' encoding and the followed file identity.</summary>
public sealed class GitRulesFileSnapshot
{
    internal GitRulesFileSnapshot(GitRulesFileKind kind, string directory, string root, string path, GitRulesLocation location,
        bool exists, string hash, string text, string encodingName, byte[] bom, string newLine, GitRulesIdentity? identity,
        UnixFileMode? mode, FileAttributes attributes)
    {
        Kind = kind; Directory = directory; RepositoryRoot = root; Path = path; Location = location; Exists = exists;
        ContentHash = hash; Text = text; EncodingName = encodingName; Bom = bom; NewLine = newLine; Identity = identity;
        Mode = mode; Attributes = attributes;
    }
    public GitRulesFileKind Kind { get; }
    public string Directory { get; }
    public string Path { get; }
    public string ResolvedPath => Location.Path;
    public bool Exists { get; }
    public string ContentHash { get; }
    public string Text { get; }
    public string EncodingName { get; }
    public bool HasBom => Bom.Length != 0;
    public string NewLine { get; }
    public UnixFileMode? Mode { get; }
    internal string RepositoryRoot { get; }
    internal GitRulesLocation Location { get; }
    internal GitRulesIdentity? Identity { get; }
    internal byte[] Bom { get; }
    internal FileAttributes Attributes { get; }
}

internal sealed record GitRulesPathPart(string Path, GitRulesIdentity? Identity, string? LinkTarget);
internal sealed record GitRulesLocation(string Path, IReadOnlyList<GitRulesPathPart> Parts);

/// <summary>Device/inode identity on Unix; the open handle is also the write destination, so a pathname replacement is not overwritten.</summary>
internal readonly record struct GitRulesIdentity(ulong Device, ulong Inode, uint Type)
{
    internal bool IsDirectory => Type == 0x4000;
    internal bool IsLink => Type == 0xa000;
    internal bool IsRegular => Type == 0x8000;

    internal static GitRulesIdentity? ReadPath(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            byte[] stat = new byte[256];
            if (LStat(path, stat) == 0) { return Parse(stat); }
            int error = Marshal.GetLastPInvokeError();
            if (error == 2) { return null; }
            throw new IOException("Cannot inspect rules file path: " + path, new Win32Exception(error));
        }
        FileInfo file = new(path);
        if (!file.Exists && file.LinkTarget is null && !System.IO.Directory.Exists(path)) { return null; }
        uint type = file.LinkTarget is not null ? 0xa000u : System.IO.Directory.Exists(path) ? 0x4000u : 0x8000u;
        return new((ulong)file.CreationTimeUtc.Ticks, 0, type);
    }

    internal static GitRulesIdentity ReadHandle(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            byte[] stat = new byte[256];
            if (FStat(handle, stat) != 0) { throw new IOException("Cannot identify the opened rules file.", new Win32Exception(Marshal.GetLastPInvokeError())); }
            return Parse(stat);
        }
        return new((ulong)File.GetCreationTimeUtc(handle).Ticks, 0, 0x8000);
    }

    private static GitRulesIdentity Parse(byte[] stat)
    {
        if (OperatingSystem.IsMacOS())
        { return new(BinaryPrimitives.ReadUInt32LittleEndian(stat), BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(8)), (uint)(BinaryPrimitives.ReadUInt16LittleEndian(stat.AsSpan(4)) & 0xf000)); }
        int modeOffset = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 16 : 24;
        return new(BinaryPrimitives.ReadUInt64LittleEndian(stat), BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(modeOffset)) & 0xf000);
    }

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] value);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(SafeFileHandle handle, [Out] byte[] value);
}

internal static class GitRulesFiles
{
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string NewLine(string text)
    {
        int index = text.IndexOf('\n');
        return index >= 0 ? index > 0 && text[index - 1] == '\r' ? "\r\n" : "\n" : text.Contains('\r') ? "\r" : "\n";
    }
    internal static bool EndsInNewLine(string text) => text.EndsWith('\n') || text.EndsWith('\r');
    internal static byte[] Bom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { return bytes[..4].ToArray(); }
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { return bytes[..3].ToArray(); }
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe }) || bytes.StartsWith(new byte[] { 0xfe, 0xff })) { return bytes[..2].ToArray(); }
        return [];
    }
    internal static GitRulesLocation Resolve(string path)
    {
        List<GitRulesPathPart> observed = [];
        Queue<string> parts = Components(path);
        string current = System.IO.Path.GetPathRoot(path)!;
        int links = 0;
        while (parts.TryDequeue(out string? part))
        {
            string next = System.IO.Path.Combine(current, part);
            GitRulesIdentity? identity = GitRulesIdentity.ReadPath(next);
            string? link = identity?.IsLink == true ? new FileInfo(next).LinkTarget : null;
            observed.Add(new(next, identity, link));
            if (identity is null)
            {
                return new(System.IO.Path.Combine([next, .. parts]), observed);
            }
            if (identity.Value.IsLink)
            {
                if (++links > 40 || link is null) { throw new IOException("The rules file has an unreadable or cyclic symbolic link: " + next); }
                string target = System.IO.Path.GetFullPath(link, current);
                parts = new Queue<string>(Components(target).Concat(parts)); current = System.IO.Path.GetPathRoot(target)!;
            }
            else
            {
                if (parts.Count > 0 && !identity.Value.IsDirectory) { throw new IOException("The rules file parent is not a directory: " + next); }
                current = next;
            }
        }
        return new(current, observed);
    }
    private static Queue<string> Components(string path) => new(path[System.IO.Path.GetPathRoot(path)!.Length..]
        .Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));

    internal static void CheckLocation(GitRulesLocation expected, GitRulesLocation actual, bool createdParents = false)
    {
        if (expected.Path != actual.Path) { throw Changed(); }
        int existing = expected.Parts.TakeWhile(part => part.Identity is not null).Count();
        if (!expected.Parts.Take(existing).SequenceEqual(actual.Parts.Take(existing)) || actual.Parts.Count < existing) { throw Changed(); }
        if (existing == expected.Parts.Count)
        {
            if (actual.Parts.Count != existing) { throw Changed(); }
        }
        else if (!createdParents)
        {
            if (!expected.Parts.SequenceEqual(actual.Parts)) { throw Changed(); }
        }
        else if (actual.Parts.Skip(existing).Any(part => part.LinkTarget is not null || part.Identity is not null && !part.Identity.Value.IsDirectory && part != actual.Parts[^1]))
        { throw Changed(); }
    }
    internal static IOException Changed() => new("The rules file, its permissions, or its symbolic-link destination changed outside this editor. Reload it before saving.");
}
