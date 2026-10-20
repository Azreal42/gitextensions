using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace GitExtensions.Linux;

/// <summary>A snapshot of a retained, explicitly admitted Git result, independent of the metadata journal.</summary>
public sealed record GitCommandCacheEntry(long Id, string Key, string DisplayString, string StandardOutput, string StandardError);

/// <summary>Process-local LRU for readers whose complete inputs have been captured by the repository.</summary>
public sealed class GitCommandCache
{
    public static GitCommandCache Shared { get; } = new();
    public const int DefaultCapacity = 50;
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;
    public const int DefaultMaximumEntryBytes = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, Item> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Item> _byId = [];
    private readonly LinkedList<Item> _insertion = new(), _recency = new();
    private readonly int _capacity, _maximumEntryBytes;
    private readonly long _maximumBytes;
    private long _nextId, _retainedBytes;

    public GitCommandCache(int capacity = DefaultCapacity, long maximumBytes = DefaultMaximumBytes, int maximumEntryBytes = DefaultMaximumEntryBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntryBytes);
        _capacity = capacity; _maximumBytes = maximumBytes; _maximumEntryBytes = maximumEntryBytes;
    }

    /// <summary>Observers run outside the lock, possibly on a worker. An observer cannot fail the Git read.</summary>
    public event Action? Changed;
    public long RetainedBytes { get { lock (_gate) { return _retainedBytes; } } }

    /// <summary>Stable insertion order; reading/inspecting an entry affects eviction, not its visible position.</summary>
    public IReadOnlyList<GitCommandCacheEntry> Snapshot()
    { lock (_gate) { return Array.AsReadOnly(_insertion.Select(item => item.Entry).ToArray()); } }

    public bool TryInspect(long id, [NotNullWhen(true)] out GitCommandCacheEntry? entry)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out Item? item)) { entry = null; return false; }
            Touch(item); entry = item.Entry; return true;
        }
    }

    /// <summary>Independent of the journal. A successful read already in flight can subsequently add its result.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_byKey.Count == 0) { return; }
            _byKey.Clear(); _byId.Clear(); _insertion.Clear(); _recency.Clear(); _retainedBytes = 0;
        }
        NotifyChanged();
    }

    // Only explicit repository readers call this. The general runner, external tools, mutation,
    // recovery and live refs/index/worktree reads retain their normal uncached execution path.
    internal async Task<GitBinaryResult> ReadThroughAsync(string identity, string key, string displayString,
        Func<CancellationToken, Task<(GitBinaryResult Result, bool Admit)>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_byKey.TryGetValue(identity, out Item? item))
            { Touch(item); return Copy(item.Result); }
        }
        (GitBinaryResult result, bool admit) = await read(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (admit && result.Success && (long)result.StandardOutput.Length + result.StandardErrorBytes.Length <= _maximumEntryBytes)
        {
            GitBinaryResult owned = Copy(result);
            string output = Encoding.UTF8.GetString(owned.StandardOutput);
            // Retain the runner's real stderr text, and the original bytes for consumers.
            GitCommandCacheEntry entry = new(0, key, displayString, output, owned.StandardError);
            long bytes = (long)owned.StandardOutput.Length + owned.StandardErrorBytes.Length
                + 2L * (identity.Length + key.Length + displayString.Length + output.Length + owned.StandardError.Length
                    + owned.Warnings.Sum(warning => (long)warning.Length));
            if (bytes <= _maximumBytes)
            {
                lock (_gate)
                {
                    if (_byKey.TryGetValue(identity, out Item? prior))
                    {
                        _retainedBytes -= prior.Bytes; prior.Result = owned; prior.Bytes = bytes;
                        prior.Entry = entry with { Id = prior.Entry.Id }; Touch(prior); _retainedBytes += bytes;
                    }
                    else
                    {
                        Item added = new(identity, entry with { Id = ++_nextId }, owned, bytes);
                        added.Insertion = _insertion.AddLast(added); added.Recency = _recency.AddLast(added);
                        _byKey.Add(identity, added); _byId.Add(added.Entry.Id, added); _retainedBytes += bytes;
                    }
                    while (_byKey.Count > _capacity || _retainedBytes > _maximumBytes) { Remove(_recency.First!.Value); }
                }
                NotifyChanged();
            }
        }
        return result;
    }

    private static GitBinaryResult Copy(GitBinaryResult result) => new(result.ExitCode, result.StandardOutput.ToArray(), result.StandardError)
    { StandardErrorBytes = result.StandardErrorBytes.ToArray(), Warnings = Array.AsReadOnly(result.Warnings.ToArray()) };

    private void Touch(Item item) { _recency.Remove(item.Recency!); item.Recency = _recency.AddLast(item); }
    private void Remove(Item item)
    {
        _byKey.Remove(item.Identity); _byId.Remove(item.Entry.Id); _insertion.Remove(item.Insertion!); _recency.Remove(item.Recency!);
        _retainedBytes -= item.Bytes;
    }
    private void NotifyChanged()
    {
        Action? observers = Changed;
        if (observers is null) { return; }
        foreach (Action observer in observers.GetInvocationList())
        { try { observer(); } catch (Exception) { /* Cache observers do not change a Git result or retain their faults. */ } }
    }
    private sealed class Item(string identity, GitCommandCacheEntry entry, GitBinaryResult result, long bytes)
    {
        internal readonly string Identity = identity;
        internal GitCommandCacheEntry Entry = entry;
        internal GitBinaryResult Result = result;
        internal long Bytes = bytes;
        internal LinkedListNode<Item>? Insertion, Recency;
    }
}
