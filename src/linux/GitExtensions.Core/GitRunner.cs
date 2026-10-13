using System.Diagnostics;
using System.Text;

namespace GitExtensions.Linux;

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public byte[] StandardOutputBytes { get; init; } = [];
    public byte[] StandardErrorBytes { get; init; } = [];
}

public sealed record GitBinaryResult(int ExitCode, byte[] StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
    public byte[] StandardErrorBytes { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
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
    /// <summary>Optional desktop fallback. Applied only to child processes with a display, preserving explicit user helpers.</summary>
    public static string? AskpassFallbackPath { get; set; }
    /// <summary>The desktop UI culture name, propagated only to its askpass fallback. It never contains credential data.</summary>
    public static string? AskpassUiCulture { get; set; }

    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        Func<GitBinaryResult, GitOutputDecoding>? outputDecoder = null)
    {
        GitBinaryResult result = await RunBinaryAsync(workingDirectory, arguments, cancellationToken,
            standardInput is null ? null : Encoding.UTF8.GetBytes(standardInput), environment).ConfigureAwait(false);
        GitOutputDecoding decoded = outputDecoder is null ? GitOutputDecoding.Utf8(result) : outputDecoder(result);
        return new GitResult(result.ExitCode, decoded.StandardOutput, decoded.StandardError)
        { StandardOutputBytes = result.StandardOutput, StandardErrorBytes = result.StandardErrorBytes, Warnings = decoded.Warnings };
    }

    public async Task<GitBinaryResult> RunBinaryAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        byte[]? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null)
        => await RunBinaryCoreAsync(workingDirectory, arguments, cancellationToken, standardInput, environment, applyAskpass: true).ConfigureAwait(false);

    private async Task<GitBinaryResult> RunBinaryCoreAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        byte[]? standardInput,
        IReadOnlyDictionary<string, string?>? environment,
        bool applyAskpass)
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

        if (applyAskpass)
        {
            await ApplyAskpassAsync(startInfo, arguments, cancellationToken).ConfigureAwait(false);
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        using MemoryStream buffer = new();
        using MemoryStream errorBuffer = new();
        Task output = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        Task error = process.StandardError.BaseStream.CopyToAsync(errorBuffer);
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
            await error.ConfigureAwait(false);
            if (process.ExitCode == 0 && inputError is not null)
            {
                throw inputError;
            }

            List<string> warnings = [];
            byte[] errorBytes = errorBuffer.ToArray();
            return new GitBinaryResult(process.ExitCode, buffer.ToArray(), GitOutputText.Utf8(errorBytes, "stderr", warnings))
            { StandardErrorBytes = errorBytes, Warnings = warnings };
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

    private async Task ApplyAskpassAsync(ProcessStartInfo startInfo, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        string? helper = AskpassFallbackPath;
        startInfo.Environment.TryGetValue("DISPLAY", out string? display);
        startInfo.Environment.TryGetValue("WAYLAND_DISPLAY", out string? waylandDisplay);
        if (string.IsNullOrEmpty(helper) || !Path.IsPathFullyQualified(helper) || !File.Exists(helper)
            || string.IsNullOrWhiteSpace(display) && string.IsNullOrWhiteSpace(waylandDisplay))
        {
            return;
        }

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(helper) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
        {
            return;
        }

        bool explicitGitHelper = startInfo.Environment.ContainsKey("GIT_ASKPASS");
        bool explicitSshHelper = startInfo.Environment.ContainsKey("SSH_ASKPASS");
        bool nativeHelper = false;
        if (!explicitGitHelper && !explicitSshHelper)
        {
            // core.askpass sits between GIT_ASKPASS and SSH_ASKPASS in Git's precedence.
            // Probe with the same global options/environment so repository, -c and config-env choices are respected.
            string[] prefix = GlobalArguments(arguments);
            GitBinaryResult configured = await RunBinaryCoreAsync(startInfo.WorkingDirectory, [.. prefix, "config", "--get", "core.askpass"],
                cancellationToken, null, new Dictionary<string, string?>(startInfo.Environment), applyAskpass: false).ConfigureAwait(false);
            if (configured.ExitCode == 1)
            {
                startInfo.Environment["GIT_ASKPASS"] = helper;
                nativeHelper = true;
            }
        }

        if (!explicitSshHelper)
        {
            startInfo.Environment["SSH_ASKPASS"] = helper;
            nativeHelper = true;
            // force also permits a Wayland-only desktop, where OpenSSH's default checks DISPLAY.
            // Authentication still goes through the SSH agent before an askpass is requested.
            if (!startInfo.Environment.ContainsKey("SSH_ASKPASS_REQUIRE"))
            {
                startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            }
        }

        if (nativeHelper && !string.IsNullOrWhiteSpace(AskpassUiCulture))
        {
            startInfo.Environment["GITEXTENSIONS_UI_CULTURE"] = AskpassUiCulture;
        }
    }

    private static string[] GlobalArguments(IReadOnlyList<string> arguments)
    {
        int index = 0;
        while (index < arguments.Count && arguments[index].StartsWith('-') && arguments[index] != "--")
        {
            string option = arguments[index++];
            if (option is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace" or "--config-env")
            {
                if (index == arguments.Count)
                {
                    break;
                }
                index++;
            }
        }

        return arguments.Take(index).ToArray();
    }
}
