using System.Text.Json;

namespace GitExtensions.Linux.Desktop;

/// <summary>
///  Reports a console close before Windows can terminate the helper during cleanup.
/// </summary>
internal sealed class WindowsInteractiveFileConsoleReporter(TextWriter writer)
{
    private readonly object _gate = new();
    private GitResult? _completed;
    private bool _reported;

    /// <summary>
    ///  Records Git's observed result after its owned processes have drained.
    /// </summary>
    internal void GitCompleted(GitResult result)
    {
        lock (_gate) { _completed = result; }
    }

    /// <summary>
    ///  Reports cancellation unless Git has already completed.
    /// </summary>
    internal void ConsoleClosing()
    {
        lock (_gate)
        {
            Report(_completed is null ? new(130, true, null) : new(_completed.ExitCode, false, null));
        }
    }

    /// <summary>
    ///  Writes at most one result when native close races with normal completion.
    /// </summary>
    internal void Report(InteractiveFileConsoleResult result)
    {
        lock (_gate)
        {
            if (_reported) { return; }
            writer.WriteLine(JsonSerializer.Serialize(result));
            writer.Flush();
            _reported = true;
        }
    }
}
