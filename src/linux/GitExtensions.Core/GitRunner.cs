using System.Diagnostics;
using System.Text;

namespace GitExtensions.Linux;

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public sealed record GitBinaryResult(int ExitCode, byte[] StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public sealed class GitCommandException : Exception
{
    public GitCommandException(IReadOnlyList<string> arguments, GitResult result)
        : base($"git {arguments.FirstOrDefault() ?? ""} failed ({result.ExitCode}): {result.StandardError.Trim()}")
    {
        Arguments = arguments.ToArray();
        Result = result;
    }

    public IReadOnlyList<string> Arguments { get; }
    public GitResult Result { get; }
}

/// <summary>Runs Git directly. Paths, revision names and messages never pass through a shell.</summary>
public sealed class GitRunner(string executable = "git")
{
    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        GitBinaryResult result = await RunBinaryAsync(workingDirectory, arguments, cancellationToken,
            standardInput is null ? null : Encoding.UTF8.GetBytes(standardInput), environment).ConfigureAwait(false);
        return new GitResult(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput), result.StandardError);
    }

    public async Task<GitBinaryResult> RunBinaryAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        byte[]? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = new(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (argument.Contains('\0'))
            {
                throw new ArgumentException("Git arguments cannot contain NUL.", nameof(arguments));
            }

            startInfo.ArgumentList.Add(argument);
        }

        // A desktop operation must report authentication failures instead of hanging on an invisible prompt.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_EDITOR"] = "true";
        startInfo.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        startInfo.Environment["LC_ALL"] = "C.UTF-8";
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string?> setting in environment)
            {
                startInfo.Environment[setting.Key] = setting.Value;
            }
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        using MemoryStream buffer = new();
        Task output = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            IOException? inputError = null;
            if (standardInput is not null)
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(standardInput, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    // Git can reject input and close its pipe before we finish writing.
                    // Retain its actual exit code and stderr instead of replacing the failure with Broken pipe.
                    inputError = exception;
                }
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (IOException exception)
            {
                inputError ??= exception;
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await output.ConfigureAwait(false);
            if (process.ExitCode == 0 && inputError is not null)
            {
                throw inputError;
            }

            return new GitBinaryResult(process.ExitCode, buffer.ToArray(), await error.ConfigureAwait(false));
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process can exit between HasExited and Kill.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }
    }
}
