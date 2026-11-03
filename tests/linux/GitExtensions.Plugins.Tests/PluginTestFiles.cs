using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using GitExtensions.Linux;

namespace GitExtensions.Plugins.Tests;

internal static class PluginTestFiles
{
    internal static string PhysicalPath(string path)
    {
        if (!OperatingSystem.IsMacOS()) { return Path.GetFullPath(path); }
        IntPtr buffer = Marshal.AllocHGlobal(4096);
        try
        {
            if (RealPath(path, buffer) == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            return Marshal.PtrToStringUTF8(buffer)!;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    // Keep Git's hostile path bytes even on a filesystem that cannot materialize
    // those names. Both operating systems read the same real Git tree/index.
    internal static async Task StageBlobAsync(GitRepository repository, string path, byte[] content)
    {
        bool materialized = !OperatingSystem.IsWindows() || path.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !path.Contains('\\');
        if (materialized) { await File.WriteAllBytesAsync(Path.Combine(repository.Path, path), content); }
        GitRunner runner = new(repository.GitExecutable);
        GitBinaryResult blob = await runner.RunBinaryAsync(repository.Path, ["hash-object", "-w", "--stdin"], standardInput: content);
        if (!blob.Success) { throw new InvalidOperationException(blob.StandardError); }
        string oid = Encoding.ASCII.GetString(blob.StandardOutput).Trim();
        byte[] record = Encoding.UTF8.GetBytes("100644 " + oid + "\t" + path + "\0");
        // ignorestat sets CE_VALID as Git inserts the entry, without passing
        // the hostile name through Windows path normalization a second time.
        string[] options = materialized ? [] : ["-c", "core.ignorestat=true"];
        GitBinaryResult added = await runner.RunBinaryAsync(repository.Path,
            ["-c", "core.protectNTFS=false", .. options, "update-index", "-z", "--index-info"], standardInput: record);
        if (!added.Success) { throw new InvalidOperationException(added.StandardError); }
    }
    internal static void DeleteDirectory(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            { File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly); }
        }
        Directory.Delete(root, recursive: true);
    }
    [SupportedOSPlatform("macos")]
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)] private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
}

internal sealed class PluginTestProfile : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ge-plugin-profile-" + Guid.NewGuid().ToString("N"));
    internal string GlobalConfig => Path.Combine(_root, "global.gitconfig");
    internal PluginTestProfile() { Directory.CreateDirectory(_root); File.WriteAllText(GlobalConfig, ""); }
    internal static string EmptyHooks(GitRepository repository)
    {
        string path = Path.Combine(repository.Path, ".git", "empty-test-hooks"); Directory.CreateDirectory(path); return path;
    }
    public void Dispose() => PluginTestFiles.DeleteDirectory(_root);
}
