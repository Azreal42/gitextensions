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
public sealed class GitRunner
{
    private readonly string? _legacyExecutable;
    private readonly GitExecutionEnvironment? _fixedEnvironment;
    private readonly AsyncLocal<GitExecutionEnvironment?> _operation = new();

    /// <summary>Uses current application options and ambient environment at each operation boundary.</summary>
    public GitRunner() { }
    /// <summary>Explicit injection retains native parent-PATH resolution and ignores application choices.</summary>
    public GitRunner(string executable) { ArgumentException.ThrowIfNullOrEmpty(executable); _legacyExecutable = executable; }
    public GitRunner(GitExecutionEnvironment environment) { _fixedEnvironment = environment ?? throw new ArgumentNullException(nameof(environment)); }
    public string Executable => ExecutionEnvironment.Executable;
    public GitExecutionEnvironment ExecutionEnvironment => _operation.Value ?? _fixedEnvironment
        ?? (_legacyExecutable is null ? GitEnvironment.Current : GitExecutionEnvironment.Capture(new() { Executable = _legacyExecutable }));
    public IDisposable BeginOperation()
    {
        GitExecutionEnvironment? previous = _operation.Value;
        _operation.Value ??= ExecutionEnvironment;
        return new GitEnvironmentOperation(() => _operation.Value = previous);
    }

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
        Func<GitBinaryResult, GitOutputDecoding>? outputDecoder = null,
        Action<GitProcessOutput>? outputReceived = null)
    {
        GitBinaryResult result = await RunBinaryAsync(workingDirectory, arguments, cancellationToken,
            standardInput is null ? null : Encoding.UTF8.GetBytes(standardInput), environment, outputReceived).ConfigureAwait(false);
        GitOutputDecoding decoded = outputDecoder is null ? GitOutputDecoding.Utf8(result) : outputDecoder(result);
        return new GitResult(result.ExitCode, decoded.StandardOutput, decoded.StandardError)
        { StandardOutputBytes = result.StandardOutput, StandardErrorBytes = result.StandardErrorBytes, Warnings = decoded.Warnings };
    }

    public async Task<GitBinaryResult> RunBinaryAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        byte[]? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<GitProcessOutput>? outputReceived = null)
        => await RunBinaryCoreAsync(workingDirectory, arguments, cancellationToken, standardInput, environment, applyAskpass: true, outputReceived,
            executionEnvironment: ExecutionEnvironment).ConfigureAwait(false);

    // The same native ownership/drain engine without Git-specific environment or authentication policy.
    internal Task<GitBinaryResult> RunExternalBinaryAsync(string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? environment,
        Action<GitProcessOutput>? outputReceived, Action<int>? processStarted)
        => RunBinaryCoreAsync(workingDirectory, arguments, cancellationToken, null, environment,
            applyAskpass: false, outputReceived, applyGitEnvironment: false, processStarted, ExecutionEnvironment);

    private async Task<GitBinaryResult> RunBinaryCoreAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        byte[]? standardInput,
        IReadOnlyDictionary<string, string?>? environment,
        bool applyAskpass,
        Action<GitProcessOutput>? outputReceived = null,
        bool applyGitEnvironment = true, Action<int>? processStarted = null, GitExecutionEnvironment? executionEnvironment = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        executionEnvironment ??= ExecutionEnvironment;
        ProcessStartInfo startInfo = new(executionEnvironment.Executable)
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
        executionEnvironment.ApplyTo(startInfo);
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (argument.Contains('\0'))
            {
                throw new ArgumentException("Git arguments cannot contain NUL.", nameof(arguments));
            }

            startInfo.ArgumentList.Add(argument);
        }

        if (applyGitEnvironment)
        {
            // Git operations retain their existing non-interactive desktop policy.
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_PAGER"] = "cat";
            startInfo.Environment["GIT_EDITOR"] = "true";
            startInfo.Environment["GIT_SEQUENCE_EDITOR"] = "true";
            startInfo.Environment["LC_ALL"] = "C.UTF-8";
            // Native editor helpers inherit the owning Git context, including an explicitly injected executable.
            startInfo.Environment[GitEnvironment.InheritedExecutableVariable] = executionEnvironment.Executable;
        }
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string?> setting in environment)
            {
                startInfo.Environment[setting.Key] = setting.Value;
            }
        }

        if (applyAskpass)
        {
            await ApplyAskpassAsync(startInfo, arguments, cancellationToken, executionEnvironment).ConfigureAwait(false);
        }

        DateTimeOffset started = DateTimeOffset.Now;
        Stopwatch elapsed = Stopwatch.StartNew();
        int? exitCode = null;
        GitProcessLaunch? launch = null;
        Exception? failure = null;
        long? logId = applyGitEnvironment ? GitCommandLog.Begin(arguments, workingDirectory, started) : null;
        Action<int>? startedObserver = processStarted;
        if (logId is long id)
        {
            startedObserver = processId => { GitCommandLog.SetProcessId(id, processId); processStarted?.Invoke(processId); };
        }
        try
        {
            if (OperatingSystem.IsLinux())
            {
                launch = _legacyExecutable is not null ? GitProcessLaunch.Configure(startInfo)
                    : GitProcessLaunch.Configure(startInfo, executionEnvironment.GetVariable("PATH") ?? "");
            }
            cancellationToken.ThrowIfCancellationRequested();
            GitBinaryResult result = await RunProcessAsync(startInfo, cancellationToken, standardInput, outputReceived, launch, startedObserver).ConfigureAwait(false);
            exitCode = result.ExitCode;
            return result;
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            if (logId is long completedId) { GitCommandLog.Complete(completedId, elapsed.Elapsed, exitCode, failure is OperationCanceledException); }
            if (OperatingSystem.IsLinux() && launch is not null)
            {
                try { launch.Dispose(); }
                catch (Exception cleanupFailure) when (failure is not null) { failure.Data["GitProcessLaunchCleanupFailure"] = cleanupFailure; }
            }
        }
    }

    private static async Task<GitBinaryResult> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken,
        byte[]? standardInput, Action<GitProcessOutput>? outputReceived, GitProcessLaunch? launch, Action<int>? processStarted)
    {
        using Process process = new() { StartInfo = startInfo };
        using GitProcessGroup? processes = OperatingSystem.IsLinux() ? new GitProcessGroup() : null;
        process.Start();
        using MemoryStream buffer = new();
        using MemoryStream errorBuffer = new();
        Task output = CopyOutputAsync(process.StandardOutput.BaseStream, buffer, GitProcessOutputStream.StandardOutput, outputReceived);
        Task error = CopyOutputAsync(process.StandardError.BaseStream, errorBuffer, GitProcessOutputStream.StandardError, outputReceived);
        Task control = Task.CompletedTask;
        Task<IOException?> input = Task.FromResult<IOException?>(null);
        TaskCompletionSource cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => cancellation.TrySetResult());
        try
        {
            if (OperatingSystem.IsLinux())
            {
                processes!.CaptureStartedChild(process);
                processes!.PinOutputPipes(process.StandardOutput.BaseStream, process.StandardError.BaseStream);
                control = launch!.ObserveExecAsync(process.Id, processes, cancellationToken);
                if (processStarted is not null) { control = NotifyStartedAsync(control, process.Id, processStarted, cancellationToken); }
            }
            else { processStarted?.Invoke(process.Id); }
            input = WriteInputAsync(process, standardInput, cancellationToken);
            Task exited = process.WaitForExitAsync(CancellationToken.None);
            // Monitor all four directions from the outset. Git can produce stdout while a large stdin is still blocked.
            List<Task> pending = [exited, input, output, error, control];
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending.Append(cancellation.Task)).ConfigureAwait(false);
                // Keep this signal until every pipe reaches EOF, including after an already exited parent.
                if (completed == cancellation.Task) { cancellationToken.ThrowIfCancellationRequested(); }
                await completed.ConfigureAwait(false); pending.Remove(completed);
                if (OperatingSystem.IsLinux() && completed == exited) { launch!.ParentExited(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            IOException? inputError = await input.ConfigureAwait(false);
            if (process.ExitCode == 0 && inputError is not null)
            {
                throw inputError;
            }

            List<string> warnings = [];
            byte[] errorBytes = errorBuffer.ToArray();
            return new GitBinaryResult(process.ExitCode, buffer.ToArray(), GitOutputText.Utf8(errorBytes, "stderr", warnings))
            { StandardErrorBytes = errorBytes, Warnings = warnings };
        }
        catch (Exception failure)
        {
            if (OperatingSystem.IsLinux())
            {
                try { launch!.CancelUnacknowledgedHandshake(); }
                catch (Exception handshakeFailure) { failure.Data["GitProcessHandshakeStopFailure"] = handshakeFailure; }
                // Ownership was captured synchronously after Process.Start: neither HELLO nor exec completion gates stopping.
                if (await processes!.StopAsync().ConfigureAwait(false) is Exception stopFailure)
                {
                    failure.Data["GitProcessStopFailure"] = stopFailure;
                    // Surface the initial exception even if permissions prevent stopping an inherited writer.
                    try { process.StandardInput.Dispose(); process.StandardOutput.Dispose(); process.StandardError.Dispose(); }
                    catch (Exception closeFailure) { failure.Data["GitProcessPipeCloseFailure"] = closeFailure; }
                }
            }
            else
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
                catch (InvalidOperationException) { /* The process exited between HasExited and Kill. */ }
            }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception waitFailure) { failure.Data["GitProcessWaitFailure"] = waitFailure; }
            if (OperatingSystem.IsLinux()) { launch!.ParentExited(); }
            try { await Task.WhenAll(input, output, error, control).ConfigureAwait(false); }
            catch { /* Preserve the original failure after the child and its pipes have stopped. */ }
            throw;
        }
    }

    private static async Task NotifyStartedAsync(Task nativeExec, int processId, Action<int> callback, CancellationToken token)
    {
        await nativeExec.ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); callback(processId);
    }

    private static async Task<IOException?> WriteInputAsync(Process process, byte[]? standardInput, CancellationToken token)
    {
        IOException? error = null;
        try { if (standardInput is not null) { await process.StandardInput.BaseStream.WriteAsync(standardInput, token).ConfigureAwait(false); } }
        catch (IOException exception) { error = exception; }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException exception) { error ??= exception; }
        }
        // Git can reject input and close its pipe; preserve its real failure rather than substitute Broken pipe.
        return error;
    }

    private static async Task CopyOutputAsync(Stream source, Stream destination, GitProcessOutputStream stream, Action<GitProcessOutput>? observer)
    {
        if (observer is null) { await source.CopyToAsync(destination).ConfigureAwait(false); return; }
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) { return; }
            await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            observer(new GitProcessOutput(stream, buffer.AsMemory(0, count).ToArray()));
        }
    }

    private async Task ApplyAskpassAsync(ProcessStartInfo startInfo, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        GitExecutionEnvironment executionEnvironment)
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

        string[] prefix = GlobalArguments(arguments, out bool informationRequest);
        // These requests print information or help instead of running a command.
        // Appending a config probe would turn --version into `version config`.
        if (informationRequest) { return; }

        bool explicitGitHelper = startInfo.Environment.ContainsKey("GIT_ASKPASS");
        bool explicitSshHelper = startInfo.Environment.ContainsKey("SSH_ASKPASS");
        bool nativeHelper = false;
        if (!explicitGitHelper && !explicitSshHelper)
        {
            // core.askpass sits between GIT_ASKPASS and SSH_ASKPASS in Git's precedence.
            // Probe with the same global options/environment so repository, -c and config-env choices are respected.
            GitBinaryResult configured = await RunBinaryCoreAsync(startInfo.WorkingDirectory, [.. prefix, "config", "--get", "core.askpass"],
                cancellationToken, null, new Dictionary<string, string?>(startInfo.Environment), applyAskpass: false,
                executionEnvironment: executionEnvironment).ConfigureAwait(false);
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

    private static string[] GlobalArguments(IReadOnlyList<string> arguments, out bool informationRequest)
    {
        informationRequest = false;
        int index = 0;
        while (index < arguments.Count && arguments[index].StartsWith('-') && arguments[index] != "--")
        {
            if (arguments[index] is "--version" or "-v" or "--help" or "-h"
                or "--exec-path" or "--html-path" or "--man-path" or "--info-path")
            { informationRequest = true; break; }
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

        if (index < arguments.Count && (arguments[index] is "version" or "help")) { informationRequest = true; }

        return arguments.Take(index).ToArray();
    }
}
