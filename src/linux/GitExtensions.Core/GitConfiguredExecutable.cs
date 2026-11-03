using System.ComponentModel;

namespace GitExtensions.Linux;

/// <summary>Resolve a configured program against its captured search path, without changing the parent environment.</summary>
internal static class GitConfiguredExecutable
{
    internal static string Resolve(string executable, string? searchPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        if (executable.Contains('\0')) { throw new ArgumentException("Executable paths cannot contain NUL.", nameof(executable)); }
        if (System.IO.Path.IsPathRooted(executable)) { return executable; }
        if (executable.Contains(System.IO.Path.DirectorySeparatorChar) || executable.Contains(System.IO.Path.AltDirectorySeparatorChar))
        { return System.IO.Path.GetFullPath(executable); }

        // CreateProcess adds .exe for a bare program name. This is an executable
        // lookup, not a command-shell lookup through PATHEXT or command text.
        string name = OperatingSystem.IsWindows() && !System.IO.Path.HasExtension(executable) && !executable.EndsWith('.')
            ? executable + ".exe" : executable;
        IEnumerable<string> directories = new[]
        {
            Environment.ProcessPath is { } process ? System.IO.Path.GetDirectoryName(process)! : AppContext.BaseDirectory,
            Environment.CurrentDirectory,
        }.Concat((searchPath ?? "").Split(System.IO.Path.PathSeparator));
        foreach (string entry in directories)
        {
            string directory = OperatingSystem.IsWindows() && entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"'
                ? entry[1..^1] : entry;
            string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, name));
            if (File.Exists(candidate)) { return candidate; }
        }
        // Falling back to Process.Start's ambient PATH could select a different
        // Git than the immutable context that owns the command and its probes.
        throw new Win32Exception(2, "The configured executable could not be found: " + executable);
    }
}
