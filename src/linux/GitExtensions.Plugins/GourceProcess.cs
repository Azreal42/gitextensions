using System.Diagnostics;
using System.Text;

namespace GitExtensions.Linux.Plugins;

internal static class GourceProcess
{
    // Interpret quoting only. In particular $, backticks, semicolons and pipes are
    // literal arguments, never shell commands or environment expansions.
    internal static string[] ParseArguments(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Contains('\0')) { throw new ArgumentException("Arguments cannot contain NUL.", nameof(arguments)); }
        List<string> result = [];
        StringBuilder current = new();
        char quote = '\0';
        bool started = false;
        for (int index = 0; index < arguments.Length; index++)
        {
            char character = arguments[index];
            if (character == '\\' && quote != '\'')
            {
                if (index + 1 == arguments.Length) { throw new ArgumentException("Arguments end with an incomplete escape.", nameof(arguments)); }
                char next = arguments[index + 1];
                if (quote == '"' && next is not ('"' or '\\')) { current.Append(character); }
                else { current.Append(next); index++; }
                started = true;
            }
            else if (quote != '\0')
            {
                if (character == quote) { quote = '\0'; }
                else { current.Append(character); }
                started = true;
            }
            else if (character is '\'' or '"') { quote = character; started = true; }
            else if (char.IsWhiteSpace(character))
            {
                if (started) { result.Add(current.ToString()); current.Clear(); started = false; }
            }
            else { current.Append(character); started = true; }
        }
        if (quote != '\0') { throw new ArgumentException("Arguments contain an unterminated quote.", nameof(arguments)); }
        if (started) { result.Add(current.ToString()); }
        return result.ToArray();
    }

    internal static ProcessStartInfo StartInfo(string executable, string directory, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (executable.Contains('\0')) { throw new ArgumentException("Executable cannot contain NUL.", nameof(executable)); }
        if (!Directory.Exists(directory)) { throw new DirectoryNotFoundException(directory); }
        ProcessStartInfo info = new(executable) { WorkingDirectory = directory, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            if (argument.Contains('\0')) { throw new ArgumentException("Arguments cannot contain NUL.", nameof(arguments)); }
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    internal static async Task<GitResult> RunAsync(string executable, string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        GitCommandLogProcessKind? journalKind = null, GitCommandLogProcessContext? journalContext = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (journalKind is { } kind && !Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(journalKind)); }
        ProcessStartInfo info = StartInfo(executable, directory, arguments);
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        using Process process = new() { StartInfo = info };
        // The post-update caller freezes UI/stack metadata before its first await.
        journalContext = journalKind is not null ? journalContext ?? GitCommandLog.CaptureProcessContext() : null;
        cancellationToken.ThrowIfCancellationRequested();
        GitCommandLogProcessOperation? journal = journalKind is { } origin ? GitCommandLog.StartProcess(origin, directory, journalContext) : null;
        bool started = false, drained = false;
        int? exitCode = null;
        Exception? failure = null;
        try
        {
            process.Start(); started = true;
            journal?.SetProcessId(process.Id);
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string standardOutput = await output.ConfigureAwait(false), standardError = await error.ConfigureAwait(false);
                drained = true; exitCode = process.ExitCode;
                return new(exitCode.Value, standardOutput, standardError);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Keep the existing real native wait and pipe drainage on owned build cancellation.
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await Task.WhenAll(output, error).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                drained = true;
                throw;
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (journal is not null)
            {
                if (!started || drained) { journal.Complete(exitCode, failure is OperationCanceledException); }
                else { journal.CompleteUnobserved(); } // Unexpected observation failure does not invent an exit or kill an external app.
            }
        }
    }
}
