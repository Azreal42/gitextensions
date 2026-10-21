namespace GitExtensions.Linux;

/// <summary>A completed streamed Git operation. Only the bounded stderr prefix is retained; stdout belongs to the caller's sink.</summary>
public sealed record GitStreamResult(int ExitCode, string StandardError)
{
    public bool Success => ExitCode == 0;
    public long StandardOutputByteCount { get; init; }
    public byte[] StandardErrorBytes { get; init; } = [];
    public bool StandardErrorTruncated { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>One operation's sequential stdout sink and a fixed 64-KiB stderr-prefix capture.</summary>
internal sealed class GitStreamCapture(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> standardOutput)
{
    private const int ErrorLimit = 64 * 1024;
    private readonly byte[] _error = new byte[ErrorLimit];
    private int _errorLength;
    private bool _errorTruncated;
    private long _outputByteCount;

    internal async Task CopyStandardOutputAsync(Stream source, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) { return; }
            token.ThrowIfCancellationRequested();
            _outputByteCount = checked(_outputByteCount + count);
            // The next read cannot reuse this memory until the one callback has genuinely returned.
            await standardOutput(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    internal async Task CopyStandardErrorAsync(Stream source)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            // Continue draining after the prefix is full, and after cancellation stops the native writer.
            int count = await source.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) { return; }
            int retained = Math.Min(count, ErrorLimit - _errorLength);
            if (retained > 0) { buffer.AsSpan(0, retained).CopyTo(_error.AsSpan(_errorLength)); _errorLength += retained; }
            _errorTruncated |= retained < count;
        }
    }

    internal GitStreamResult Complete(int exitCode)
    {
        byte[] error = _error.AsSpan(0, _errorLength).ToArray();
        List<string> warnings = [];
        string text = GitOutputText.Utf8(error, "captured stderr prefix", warnings);
        if (_errorTruncated)
        { warnings.Add("Git stderr was truncated after 65536 bytes; additional bytes were drained and discarded, not retained."); }
        return new(exitCode, text)
        {
            StandardOutputByteCount = _outputByteCount, StandardErrorBytes = error,
            StandardErrorTruncated = _errorTruncated, Warnings = warnings.AsReadOnly()
        };
    }
}
