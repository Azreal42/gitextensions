namespace GitExtensions.Linux.Desktop.Avatars;

/// <summary>Windows MRU capacity; deliberately no clock or expiration.</summary>
internal sealed class AvatarMemoryCache
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, (AvatarPng Image, LinkedListNode<string> Node)> _images = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = [];
    internal AvatarMemoryCache(int capacity = 200)
    { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity); _capacity = capacity; }
    internal int Count { get { lock (_gate) { return _images.Count; } } }
    internal bool TryGet(string key, out AvatarPng? image)
    {
        lock (_gate)
        {
            if (!_images.TryGetValue(key, out var entry)) { image = null; return false; }
            _recent.Remove(entry.Node); _recent.AddFirst(entry.Node); image = entry.Image; return true;
        }
    }
    internal void Add(string key, AvatarPng image)
    {
        lock (_gate)
        {
            if (_images.Remove(key, out var previous)) { _recent.Remove(previous.Node); }
            _images.Add(key, (image, _recent.AddFirst(key)));
            while (_images.Count > _capacity)
            { string last = _recent.Last!.Value; _recent.RemoveLast(); _images.Remove(last); }
        }
    }
    internal void Clear() { lock (_gate) { _images.Clear(); _recent.Clear(); } }
}
