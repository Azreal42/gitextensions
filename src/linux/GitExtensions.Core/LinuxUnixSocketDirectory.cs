using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace GitExtensions.Linux;

/// <summary>Owns a private Linux directory whose control socket fits sockaddr_un.sun_path.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxUnixSocketDirectory : IDisposable
{
    private const int MaximumSocketBytes = 107;
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private int _disposed;

    private LinuxUnixSocketDirectory(string directory)
    {
        DirectoryPath = directory;
        SocketPath = Path.Combine(directory, "control");
    }

    public string DirectoryPath { get; }
    public string SocketPath { get; }

    /// <summary>Uses the normal temporary directory unless only its socket-path length requires /tmp.</summary>
    public static LinuxUnixSocketDirectory Create(string prefix = "gitextensions-socket-")
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        { throw new ArgumentException("A socket directory prefix must contain only ASCII letters, digits, '-' or '_'.", nameof(prefix)); }

        string name = prefix + "XXXXXX";
        if (Encoding.UTF8.GetByteCount(Path.Combine("/tmp", name, "control")) > MaximumSocketBytes)
        { throw new ArgumentException("The socket directory prefix is too long for a Unix control socket.", nameof(prefix)); }
        string parent = Path.GetFullPath(Path.GetTempPath());
        if (Encoding.UTF8.GetByteCount(Path.Combine(parent, name, "control")) > MaximumSocketBytes)
        { parent = "/tmp"; }

        // mkdtemp exclusively creates the unpredictable final component with mode0700; it never adopts an existing link/directory.
        byte[] template = Encoding.UTF8.GetBytes(Path.Combine(parent, name) + '\0');
        if (MakeTemporaryDirectory(template) == IntPtr.Zero)
        { throw new IOException("Cannot create a private Unix socket directory in '" + parent + "'.", new Win32Exception(Marshal.GetLastPInvokeError())); }
        string directory = Encoding.UTF8.GetString(template, 0, template.Length - 1);
        try
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
            { throw new IOException("The newly created Unix socket directory is a symbolic link."); }
            // Normalize owner rights reduced by umask and a setgid bit inherited from a legitimate shared TMPDIR.
            File.SetUnixFileMode(directory, PrivateMode);
            if (File.GetUnixFileMode(directory) != PrivateMode)
            { throw new IOException("The newly created Unix socket directory is not private mode0700."); }
            return new LinuxUnixSocketDirectory(directory);
        }
        catch (Exception failure)
        {
            try { Directory.Delete(directory); }
            catch (Exception cleanupFailure) { failure.Data["UnixSocketDirectoryCleanupFailure"] = cleanupFailure; }
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        try
        {
            if (new DirectoryInfo(DirectoryPath).LinkTarget is not null)
            { throw new IOException("The private Unix socket directory was replaced by a symbolic link."); }
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
        catch { Volatile.Write(ref _disposed, 0); throw; }
    }

    [DllImport("libc", EntryPoint = "mkdtemp", SetLastError = true)]
    private static extern IntPtr MakeTemporaryDirectory([In, Out] byte[] template);
}
