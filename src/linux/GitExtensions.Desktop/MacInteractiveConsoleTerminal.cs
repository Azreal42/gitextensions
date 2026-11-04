using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace GitExtensions.Linux.Desktop;

/// <summary>LaunchServices opens a private command file; the authenticated helper owns the actual Git lifetime.</summary>
internal static class MacInteractiveConsoleTerminal
{
    [SupportedOSPlatform("macos")]
    internal static Process Start(string commandPath, string executable, IReadOnlyList<string> arguments,
        string repositoryPath, GitExecutionEnvironment environment)
    {
        WriteCommand(commandPath, executable, arguments, repositoryPath, environment);
        ProcessStartInfo start = new("/usr/bin/open") { UseShellExecute = false };
        environment.ApplyTo(start);
        start.ArgumentList.Add("-b"); start.ArgumentList.Add("com.apple.Terminal");
        start.ArgumentList.Add(commandPath);
        return Process.Start(start) ?? throw new IOException("The interactive macOS terminal could not be opened.");
    }

    [SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
    internal static void WriteCommand(string commandPath, string executable, IReadOnlyList<string> arguments,
        string repositoryPath, GitExecutionEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(environment);
        StringBuilder command = new("#!/bin/sh\ncd ");
        command.Append(Quote(Path.GetFullPath(repositoryPath))).Append(" || exit 1\nexec /usr/bin/env -i");
        foreach ((string name, string? value) in environment.EnvironmentVariables)
        {
            if (value is null) { continue; }
            if (name.Length == 0 || name.Contains('=')) { throw new ArgumentException("A terminal environment name is invalid.", nameof(environment)); }
            command.Append(' ').Append(Quote(name + "=" + value));
        }
        command.Append(' ').Append(Quote(executable));
        foreach (string argument in arguments) { command.Append(' ').Append(Quote(argument)); }
        command.Append('\n');
        // The owning socket directory remains private until the helper exits.
        // CreateNew never follows or overwrites an existing path.
        using FileStream file = new(commandPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        });
        File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        file.Write(new UTF8Encoding(false).GetBytes(command.ToString()));
    }

    private static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) { throw new ArgumentException("Terminal launch values cannot contain NUL."); }
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
