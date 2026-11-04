using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitExtensions.Linux.Desktop;

internal sealed record InteractiveFileConsoleRequest(string SocketPath, string Nonce, string RepositoryPath, string GitExecutable, string[] Arguments)
{
    // Optional for helpers/requests produced before environment snapshots were transported.
    public InteractiveFileConsoleEnvironment? ExecutionEnvironment { get; init; }
    internal GitExecutionEnvironment RestoreExecutionEnvironment()
    {
        GitExecutionEnvironment environment = ExecutionEnvironment?.Restore()
            ?? GitExecutionEnvironment.Capture(new() { Executable = GitExecutable });
        if (environment.Executable != GitExecutable)
        { throw new InvalidDataException("The interactive Git executable differs from its captured environment."); }
        return environment;
    }
}

/// <summary>Only effective child environment data, copied before a terminal or helper can outlive its caller.</summary>
internal sealed class InteractiveFileConsoleEnvironment
{
    [JsonConstructor]
    public InteractiveFileConsoleEnvironment(GitEnvironmentOptions options, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(environmentVariables);
        GitExecutionEnvironment captured = GitExecutionEnvironment.Capture(options, environmentVariables);
        Options = captured.Options; EnvironmentVariables = captured.EnvironmentVariables;
    }
    public GitEnvironmentOptions Options { get; }
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }
    internal static InteractiveFileConsoleEnvironment Capture(GitExecutionEnvironment environment)
        => new(environment.Options, environment.EnvironmentVariables);
    internal GitExecutionEnvironment Restore() => GitExecutionEnvironment.Capture(Options, EnvironmentVariables);
    internal static GitExecutionEnvironment WithTerminalType(GitExecutionEnvironment environment, string? terminalType)
    {
        if (environment.EnvironmentVariables.ContainsKey("TERM") || string.IsNullOrEmpty(terminalType)) { return environment; }
        Dictionary<string, string?> variables = new(environment.EnvironmentVariables, StringComparer.Ordinal) { ["TERM"] = terminalType };
        return GitExecutionEnvironment.Capture(environment.Options, variables);
    }

    internal static GitExecutionEnvironment WithUserTerminalContext(GitExecutionEnvironment environment, Func<string, string?> readTerminalVariable)
    {
        ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(readTerminalVariable);
        GitExecutionEnvironment context = WithTerminalType(environment, readTerminalVariable("TERM"));
        // A locale is one captured choice: importing other categories could
        // override its meaning. GUI launches without a locale use the encoding
        // selected by the user's terminal, without importing its PATH or tools.
        if (environment.EnvironmentVariables.Keys.Any(name => name is "LANG" or "LANGUAGE" || name.StartsWith("LC_", StringComparison.Ordinal)))
        { return context; }
        Dictionary<string, string?>? variables = null;
        foreach (string name in new[] { "LANG", "LC_ALL", "LC_COLLATE", "LC_CTYPE", "LC_MESSAGES", "LC_MONETARY", "LC_NUMERIC", "LC_TIME" })
        {
            if (readTerminalVariable(name) is not { Length: > 0 } value) { continue; }
            variables ??= new(context.EnvironmentVariables, StringComparer.Ordinal);
            variables[name] = value;
        }
        return variables is null ? context : GitExecutionEnvironment.Capture(context.Options, variables);
    }
}
internal sealed record InteractiveFileConsoleHello(string Nonce);
internal sealed record InteractiveFileConsoleResult(int ExitCode, bool Cancelled, string? Error);

internal static class InteractiveFileConsoleProtocol
{
    internal const string Mode = "--interactive-file-console";
    internal static async Task WriteAsync<T>(StreamWriter writer, T value)
    { await writer.WriteLineAsync(JsonSerializer.Serialize(value)); await writer.FlushAsync(); }
    internal static async Task<T> ReadAsync<T>(StreamReader reader, CancellationToken token = default)
    {
        string line = await reader.ReadLineAsync(token) ?? throw new IOException("The interactive Git console closed its control channel.");
        if (line.Length > 1024 * 1024) { throw new InvalidDataException("The interactive Git console record is too large."); }
        return JsonSerializer.Deserialize<T>(line) ?? throw new InvalidDataException("The interactive Git console record is invalid.");
    }
    internal static Task WaitForCloseInputAsync(CancellationToken token) => Task.Run(() =>
    {
        // Console.In is synchronized and its ReadLineAsync cannot interrupt an active TTY read.
        // Poll only this helper's inherited stdin; cancellation never signals the terminal emulator.
        byte[] value = new byte[1];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            PollDescriptor descriptor = new() { Descriptor = 0, Events = 1 };
            int ready = Poll(ref descriptor, 1, 100);
            if (ready < 0 && Marshal.GetLastPInvokeError() == 4) { continue; }
            if (ready < 0) { throw new IOException("Cannot read the interactive Git terminal."); }
            if (ready == 0) { continue; }
            if ((descriptor.Returned & 1) == 0) { return; } // Terminal closed or stdin reached EOF.
            long count = ReadInput(0, value, 1);
            if (count == 0 || count == 1 && value[0] == (byte)'\n') { return; }
            if (count < 0 && Marshal.GetLastPInvokeError() != 4) { throw new IOException("Cannot read the interactive Git terminal."); }
        }
    });
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { internal int Descriptor; internal short Events, Returned; }
    [DllImport("libc", EntryPoint = "read", SetLastError = true)] private static extern long ReadInput(int descriptor, [Out] byte[] value, nuint count);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollDescriptor descriptors, uint count, int timeout);
}
