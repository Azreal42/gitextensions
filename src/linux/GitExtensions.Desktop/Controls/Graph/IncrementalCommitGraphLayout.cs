using GitExtensions.Linux;

namespace GitExtensions.Desktop.Controls.Graph;

/// <summary>One native graph per streamed generation. Ordering is cheap; lane caches are requested only for the viewport.</summary>
internal sealed class IncrementalCommitGraphLayout
{
    private RevisionGraph _graph;
    private readonly List<GitCommit> _received = [];
    private readonly Dictionary<string, GitCommit> _byHash = new(StringComparer.Ordinal);
    private readonly List<GitCommit> _ordered = [];
    private readonly Dictionary<int, GraphNode> _visibleNodes = [];
    private readonly List<GraphStroke> _visibleStrokes = [];
    private int _version, _preparedVersion = -1, _preparedFirst = -1, _preparedLast = -1;
    private readonly string _head;
    internal IReadOnlyList<GitCommit> Commits => _received;
    internal IReadOnlyList<GitCommit> OrderedCommits => _ordered;
    internal int Count => _received.Count;
    internal bool IsComplete { get; private set; }
    internal RevisionGraphOptions Options => _graph.Config.Options;
    internal int CachedCount => _graph.GetCachedCount();
    internal int VisibleLaneCount { get; private set; } = 1;
    internal IReadOnlyList<GraphStroke> VisibleStrokes => _visibleStrokes;

    internal IncrementalCommitGraphLayout(bool onlyFirstParent, RevisionGraphOptions options, string? head)
    { _head = head ?? ""; _graph = new(options) { OnlyFirstParent = onlyFirstParent, HeadId = _head }; }

    internal void Append(IReadOnlyList<GitCommit> batch)
    {
        if (IsComplete) { throw new InvalidOperationException("The revision stream has already completed."); }
        bool changed = false;
        foreach (GitCommit commit in batch)
        {
            // Commit identity is immutable within one query generation. Preserve its first published object.
            if (!_byHash.TryAdd(commit.Hash, commit)) { continue; }
            _received.Add(commit);
            _graph.Add(new(commit.Hash, commit.Parents.Distinct(StringComparer.Ordinal).ToArray(), commit.Subject));
            changed = true;
        }
        if (!changed) { return; }
        // GetNodeForRow forces the native ordered-node cache through Count-1 without preparing any lane rows.
        _graph.GetNodeForRow(Count - 1);
        _ordered.Clear();
        for (int index = 0; index < Count; index++)
        { _ordered.Add(_byHash[_graph.GetNodeForRow(index)!.Objectid]); }
        InvalidateViewport();
    }

    internal void Complete()
    {
        if (IsComplete) { return; }
        _graph.LoadingCompleted(); IsComplete = true; InvalidateViewport();
    }

    internal void Reconfigure(bool onlyFirstParent, RevisionGraphOptions options)
    {
        // A deliberate settings change can replace the model once; receiving a batch never does.
        _graph = new(options) { OnlyFirstParent = onlyFirstParent, HeadId = _head };
        foreach (GitCommit commit in _received)
        { _graph.Add(new(commit.Hash, commit.Parents.Distinct(StringComparer.Ordinal).ToArray(), commit.Subject)); }
        if (IsComplete) { _graph.LoadingCompleted(); }
        _ordered.Clear();
        if (Count > 0) { _graph.GetNodeForRow(Count - 1); }
        for (int index = 0; index < Count; index++) { _ordered.Add(_byHash[_graph.GetNodeForRow(index)!.Objectid]); }
        InvalidateViewport();
    }

    internal IReadOnlyList<string> GetChildren(string hash)
        => _graph.TryGetNode(hash, out RevisionGraphRevision? node) ? node.Children.Reverse().Select(child => child.Objectid).ToArray() : [];

    internal void PrepareViewport(int first, int last, CancellationToken token = default)
    {
        if (Count == 0 || last < first) { return; }
        first = Math.Clamp(first, 0, Count - 1); last = Math.Clamp(last, first, Count - 1);
        if (_preparedVersion == _version && _preparedFirst == first && _preparedLast == last) { return; }
        // Same native policy as RevisionDataGridView: cache the visible page and the following page.
        // CacheTo itself supplies the algorithm's straightening/look-ahead. Never spin waiting for future input.
        int lastToCache = (int)Math.Min(Count - 1L, (long)last + (last - first + 1) + 2);
        _graph.CacheTo(first, lastToCache, token);
        int stable = _graph.GetCachedCount();
        if (stable <= last && _graph.GetSegmentsForRow(first) is null)
        {
            // Extending an existing cache can cross Add's invalid-from-score marker.
            // Windows rechecks CacheTo/GetCachedCount; consume that dirty cache once.
            // A live tail without enough look-ahead is still allowed to remain unavailable.
            _graph.CacheTo(first, lastToCache, token);
            stable = _graph.GetCachedCount();
        }
        _visibleNodes.Clear(); _visibleStrokes.Clear(); VisibleLaneCount = 1;
        for (int index = first; index <= last && index < stable; index++)
        {
            token.ThrowIfCancellationRequested();
            IRevisionGraphRow? row = _graph.GetSegmentsForRow(index);
            if (row is null) { continue; }
            int lane = row.GetCurrentRevisionLane();
            int color = RevisionGraphLaneColor.GetColorForLane(RevisionGraphLaneColor.Hash(row.Revision.Objectid));
            VisibleLaneCount = Math.Max(VisibleLaneCount, row.GetLaneCount());
            foreach (RevisionGraphSegment segment in row.Segments)
            {
                int center = row.GetLaneForSegment(segment).Index;
                if (center < 0) { continue; }
                int segmentColor = segment.LaneInfo?.Color ?? color;
                if (segment.Child == row.Revision || segment.Parent == row.Revision) { color = segmentColor; }
                // A live tail is not a limited-history boundary. Only the final terminal snapshot gets the marker.
                if (IsComplete && index == Count - 1 && segment.Parent != row.Revision && !_byHash.ContainsKey(segment.Parent.Objectid))
                { _visibleStrokes.Add(new(index, center, center, false, segmentColor, segment.Child.Objectid, segment.Parent.Objectid, true)); }
            }
            _visibleNodes.Add(index, new(_byHash[row.Revision.Objectid], lane, color));
        }
        _preparedVersion = _version; _preparedFirst = first; _preparedLast = last;
    }

    internal GraphNode? GetPreparedNode(int row) => _visibleNodes.GetValueOrDefault(row);
    internal IReadOnlyList<GraphRenderSegment> RenderSegmentsForRow(int row, double laneWidth, double rowHeight,
        bool skipSecondarySharedSegments, IReadOnlySet<string>? highlightedIds)
        => _visibleNodes.ContainsKey(row) ? RevisionGraphRenderer.GetSegmentsForRow(_graph, row, laneWidth, rowHeight,
            skipSecondarySharedSegments, highlightedIds) : [];

    private void InvalidateViewport()
    { _version++; _preparedVersion = -1; _visibleNodes.Clear(); _visibleStrokes.Clear(); }
}
