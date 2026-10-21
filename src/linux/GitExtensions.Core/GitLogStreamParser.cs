using System.Globalization;
using System.Text;

namespace GitExtensions.Linux;

/// <summary>
/// Canonical eight-field log protocol. Memory belongs to one unfinished field/record and one
/// batch, never the complete stdout. UTF-8 and NUL boundaries may cross arbitrary pipe reads.
/// </summary>
internal sealed class GitLogStreamParser
{
    private const long BatchTextByteThreshold = 1024 * 1024;
    private readonly GitProcessTextDecoder _decoder = new();
    private readonly string[] _fields = new string[8];
    private readonly StringBuilder _field = new();
    private readonly List<GitCommit> _batch = [];
    private readonly int _batchSize;
    private readonly Func<IReadOnlyList<GitCommit>, CancellationToken, ValueTask> _onBatch;
    private int _fieldIndex, _target;
    private long _recordCharacters, _batchTextBytes;
    private bool _completed;

    internal GitLogStreamParser(int batchSize, Func<IReadOnlyList<GitCommit>, CancellationToken, ValueTask> onBatch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentNullException.ThrowIfNull(onBatch);
        _batchSize = batchSize; _target = Math.Min(100, batchSize); _onBatch = onBatch;
    }

    internal long CommitCount { get; private set; }
    internal bool HadEscapedBytes => _decoder.HadEscapedBytes;

    internal async ValueTask ReadAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (_completed) { throw new InvalidOperationException("The Git history stream is already complete."); }
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(4096, bytes.Length - offset);
            string characters = _decoder.Append(bytes.Span.Slice(offset, length));
            offset += length;
            await ReadCharactersAsync(characters, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed) { throw new InvalidOperationException("The Git history stream is already complete."); }
        cancellationToken.ThrowIfCancellationRequested();
        string tail = _decoder.Append(ReadOnlySpan<byte>.Empty, flush: true);
        await ReadCharactersAsync(tail, cancellationToken).ConfigureAwait(false);
        if (_fieldIndex != 0 || _field.Length != 0)
        { throw new InvalidDataException("Incomplete NUL-terminated Git history record."); }
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _completed = true;
    }

    private async ValueTask ReadCharactersAsync(string characters, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < characters.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int separator = characters.IndexOf('\0', offset);
            int length = (separator < 0 ? characters.Length : separator) - offset;
            _field.Append(characters, offset, length);
            _recordCharacters = checked(_recordCharacters + length);
            if (separator < 0) { return; }
            _fields[_fieldIndex++] = _field.ToString(); _field.Clear(); offset = separator + 1;
            if (_fieldIndex != _fields.Length) { continue; }
            GitCommit commit = ReadCommit();
            _fieldIndex = 0; Array.Clear(_fields);
            _batch.Add(commit);
            _batchTextBytes = checked(_batchTextBytes + _recordCharacters * sizeof(char));
            _recordCharacters = 0;
            // The byte threshold is retained UTF-16 text, not an arbitrary message limit.
            // A single large record stays whole and is delivered as soon as it completes.
            if (_batch.Count >= _target || _batchTextBytes >= BatchTextByteThreshold)
            { await FlushAsync(cancellationToken).ConfigureAwait(false); }
        }
    }

    private GitCommit ReadCommit()
    {
        string[] parents = _fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!IsObjectName(_fields[0]) || parents.Any(parent => !IsObjectName(parent)))
        { throw new InvalidDataException("Git history options altered the canonical commit identities."); }
        if (!DateTimeOffset.TryParse(_fields[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset authored))
        { throw new InvalidDataException("Invalid authored date in the Git history stream."); }
        return new(_fields[0], parents, _fields[2], _fields[3], authored, _fields[5], _fields[6]) { FullMessage = _fields[7] };
    }

    private static bool IsObjectName(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_batch.Count == 0) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        // Give the callback an independent immutable list: no later parse mutates a retained batch.
        IReadOnlyList<GitCommit> batch = Array.AsReadOnly(_batch.ToArray());
        _batch.Clear(); _batchTextBytes = 0; _target = _batchSize;
        await _onBatch(batch, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        CommitCount = checked(CommitCount + batch.Count);
    }
}
