using System.Buffers;
using System.Globalization;
using System.Text;

namespace GitExtensions.Linux;

public enum GitProcessOutputStream { StandardOutput, StandardError }

/// <summary>An owned byte snapshot from one pipe. Order is preserved within each pipe; pipes have no shared ordering guarantee.</summary>
public sealed record GitProcessOutput(GitProcessOutputStream Stream, ReadOnlyMemory<byte> Bytes);

/// <summary>Incremental UTF-8 projection matching GitOutputText, with incomplete characters held until another chunk or Flush.</summary>
public sealed class GitProcessTextDecoder
{
    private byte[] _pending = [];
    /// <summary>Whether any byte has required the existing lossless \xNN text projection.</summary>
    public bool HadEscapedBytes { get; private set; }

    public string Append(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        byte[] input = new byte[_pending.Length + bytes.Length];
        _pending.CopyTo(input, 0); bytes.CopyTo(input.AsSpan(_pending.Length)); _pending = [];
        ReadOnlySpan<byte> remaining = input; StringBuilder text = new();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf8(remaining, out Rune rune, out int consumed);
            if (status == OperationStatus.Done) { text.Append(rune.ToString()); remaining = remaining[consumed..]; }
            else if (status == OperationStatus.NeedMoreData && !flush) { _pending = remaining.ToArray(); break; }
            else { HadEscapedBytes = true; text.Append("\\x").Append(remaining[0].ToString("X2", CultureInfo.InvariantCulture)); remaining = remaining[1..]; }
        }
        return text.ToString();
    }
}
