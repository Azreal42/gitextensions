using System.Text;
using System.Threading.Channels;
using Porta.Pty;

namespace GitExtensions.Linux.Desktop;

/// <summary>A window-owned PTY. All terminal bytes are UTF-8; no shell commands are synthesized.</summary>
internal sealed class EmbeddedTerminalSession : IAsyncDisposable
{
    private readonly IPtyConnection _connection;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _writer;
    private bool _disposed;

    internal EmbeddedTerminalSession(IPtyConnection connection)
    {
        _connection = connection;
        connection.ProcessExited += ProcessExited;
        if (connection.WaitForExit(0)) { _exited.TrySetResult(connection.ExitCode); }
        _writer = Task.Run(WriteInputAsync);
    }

    internal int ProcessId => _connection.Pid;

    internal static PtyOptions CreateOptions(string directory, GitExecutionEnvironment environment, int columns, int rows)
    {
        Dictionary<string, string> variables = environment.EnvironmentVariables
            .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";
        variables["TERM_PROGRAM"] = "GitExtensions";
        // A GUI started by Finder often has no locale. Preserve every explicit user locale.
        if (!variables.Keys.Any(key => key == "LANG" || key == "LC_ALL" || key == "LC_CTYPE"))
        { variables["LANG"] = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8"; }
        string shell;
        if (OperatingSystem.IsWindows())
        {
            shell = WindowsGitShellResolver.FindInteractiveShell(environment, nativeConsole: true)
                ?? throw new FileNotFoundException("Git Bash is unavailable. Choose its tools directory in Git settings.");
            variables["CHERE_INVOKING"] = "1";
        }
        else
        {
            string? preferred = environment.GetVariable("SHELL");
            shell = preferred is { Length: > 0 } && Path.IsPathFullyQualified(preferred) && File.Exists(preferred)
                ? preferred : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
        }
        return new PtyOptions
        {
            App = shell, CommandLine = ["-l", "-i"], Cwd = Path.GetFullPath(directory), Name = "xterm-256color",
            Cols = Math.Max(2, columns), Rows = Math.Max(1, rows), Environment = variables,
            UseAsyncIo = true
        };
    }

    internal static async Task<EmbeddedTerminalSession> StartAsync(PtyOptions options, CancellationToken cancellation)
    {
        IPtyConnection connection = await Task.Run(() => PtyProvider.SpawnAsync(options, cancellation), cancellation).ConfigureAwait(false);
        if (cancellation.IsCancellationRequested)
        {
            await Task.Run(connection.Dispose).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }
        return new(connection);
    }

    internal void Send(byte[] bytes)
    {
        if (!_disposed && !_exited.Task.IsCompleted) { _input.Writer.TryWrite(bytes.ToArray()); }
    }

    internal void Resize(int columns, int rows)
    {
        if (!_disposed && !_exited.Task.IsCompleted) { _connection.Resize(Math.Max(2, columns), Math.Max(1, rows)); }
    }

    internal async Task<int> RunAsync(Func<string, CancellationToken, Task> output, CancellationToken cancellation)
    {
        using CancellationTokenSource reading = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        Task read = ReadOutputAsync(output, reading.Token);
        Task exit = _exited.Task.WaitAsync(cancellation);
        try
        {
            Task finished = await Task.WhenAny(read, exit, _writer).ConfigureAwait(false);
            if (finished == _writer) { await _writer.ConfigureAwait(false); }
            if (finished == read) { await read.ConfigureAwait(false); }
            await exit.ConfigureAwait(false);
            // Drain final output, without waiting forever on a background child holding the slave open.
            try { await read.WaitAsync(TimeSpan.FromMilliseconds(500), cancellation).ConfigureAwait(false); }
            catch (TimeoutException) { reading.Cancel(); }
            return await _exited.Task.ConfigureAwait(false);
        }
        finally
        {
            reading.Cancel();
            try { await read.ConfigureAwait(false); }
            catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        }
    }

    private async Task ReadOutputAsync(Func<string, CancellationToken, Task> output, CancellationToken cancellation)
    {
        // StreamReader retains incomplete UTF-8 sequences between reads (accents, CJK and emoji).
        using StreamReader reader = new(_connection.ReaderStream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        char[] buffer = new char[8192];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false);
            if (count == 0) { return; }
            await output(new string(buffer, 0, count), cancellation).ConfigureAwait(false);
        }
    }

    private async Task WriteInputAsync()
    {
        await foreach (byte[] bytes in _input.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
        {
            await _connection.WriterStream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
            await _connection.WriterStream.FlushAsync(_stop.Token).ConfigureAwait(false);
        }
    }

    private void ProcessExited(object? sender, PtyExitedEventArgs args) => _exited.TrySetResult(args.ExitCode);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        _input.Writer.TryComplete(); _stop.Cancel();
        _connection.ProcessExited -= ProcessExited;
        // Native hangup/termination may briefly wait; never do it on the Avalonia dispatcher.
        await Task.Run(_connection.Dispose).ConfigureAwait(false);
        try { await _writer.ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        _stop.Dispose();
    }
}
