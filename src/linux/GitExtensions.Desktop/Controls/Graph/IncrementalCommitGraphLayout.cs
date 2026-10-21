using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls.Graph;

internal sealed record PreparedHistoryGraphNode(object Item, int Lane, int Color);

/// <summary>One native graph per streamed generation. Ordering is cheap; lane caches are requested only for the viewport.</summary>
internal sealed class IncrementalCommitGraphLayout
{
    private RevisionGraph _graph;
    private readonly List<GitCommit> _received = [];
    private readonly Dictionary<string, GitCommit> _byHash = new(StringComparer.Ordinal);
    private readonly List<GitCommit> _ordered = [];
    private readonly List<object> _orderedItems = [];
    private readonly Dictionary<string, object> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<int, PreparedHistoryGraphNode> _visibleNodes = [];
    private readonly Dictionary<int, GraphNode> _visibleRealNodes = [];
    private readonly ArtificialHistoryRevision? _working, _index;
    private bool _artificialsAdmitted;
    private IReadOnlyList<string> _headAncestors = [];
    private readonly List<GraphStroke> _visibleStrokes = [];
    private int _version, _preparedVersion = -1, _preparedFirst = -1, _preparedLast = -1;
    private readonly string _head;
    internal IReadOnlyList<GitCommit> Commits => _received;
    internal IReadOnlyList<GitCommit> OrderedCommits => _ordered;
    internal int Count => _received.Count;
    internal int RowCount => _graph.Count;
    internal IReadOnlyList<object> OrderedItems => _orderedItems;
    internal bool HasPendingArtificials => _working is not null && !_artificialsAdmitted;
    internal bool IsComplete { get; private set; }
    internal RevisionGraphOptions Options => _graph.Config.Options;
    internal int CachedCount => _graph.GetCachedCount();
    internal int VisibleLaneCount { get; private set; } = 1;
    internal IReadOnlyList<GraphStroke> VisibleStrokes => _visibleStrokes;

    internal IncrementalCommitGraphLayout(bool onlyFirstParent, RevisionGraphOptions options, string? head, bool includeArtificial = false)
    {
        _head = head ?? ""; _graph = new(options) { OnlyFirstParent = onlyFirstParent, HeadId = _head };
        if (includeArtificial)
        {
            _working = new(GitCompareRevisionKind.WorkingTree, _head.Length == 0 ? null : _head); _index = new(GitCompareRevisionKind.Index, _head.Length == 0 ? null : _head);
            _byKey.Add(_working.GraphId, _working); _byKey.Add(_index.GraphId, _index);
        }
    }

    internal void Append(IReadOnlyList<GitCommit> batch)
    {
        if (IsComplete) { throw new InvalidOperationException("The revision stream has already completed."); }
        bool changed = false;
        foreach (GitCommit commit in batch)
        {
            // Commit identity is immutable within one query generation. Preserve its first published object.
            if (commit.Hash is ArtificialHistoryRevision.WorkingTreeKey or ArtificialHistoryRevision.IndexKey)
            { throw new ArgumentException("A Git commit cannot use an artificial history key.", nameof(batch)); }
            if (!_byHash.TryAdd(commit.Hash, commit)) { continue; }
            _received.Add(commit); _byKey.Add(commit.Hash, commit);
            if (HasPendingArtificials && (_head.Length == 0 || commit.Hash == _head)) { AddArtificialPair(); }
            _graph.Add(new(commit.Hash, commit.Parents.Distinct(StringComparer.Ordinal).ToArray(), commit.Subject));
            changed = true;
        }
        if (!changed) { return; }
        RefreshOrder(); InvalidateViewport();
    }

    internal void Complete(IReadOnlyList<string>? headAncestors = null)
    {
        if (IsComplete) { return; }
        _headAncestors = headAncestors?.ToArray() ?? [];
        if (HasPendingArtificials) { InsertArtificialPair(); RefreshOrder(); }
        _graph.LoadingCompleted(); IsComplete = true; InvalidateViewport();
    }

    internal void Reconfigure(bool onlyFirstParent, RevisionGraphOptions options)
    {
        // A deliberate settings change can replace the model once; receiving a batch never does.
        _graph = new(options) { OnlyFirstParent = onlyFirstParent, HeadId = _head };
        _artificialsAdmitted = false;
        foreach (GitCommit commit in _received)
        {
            if (HasPendingArtificials && (_head.Length == 0 || commit.Hash == _head)) { AddArtificialPair(); }
            _graph.Add(new(commit.Hash, commit.Parents.Distinct(StringComparer.Ordinal).ToArray(), commit.Subject));
        }
        if (IsComplete)
        {
            if (HasPendingArtificials) { InsertArtificialPair(); }
            _graph.LoadingCompleted();
        }
        RefreshOrder(); InvalidateViewport();
    }

    private static LayoutRevision Layout(ArtificialHistoryRevision row) => new(row.GraphId, row.ParentIds, row.Subject);
    private void AddArtificialPair()
    {
        _graph.Add(Layout(_working!)); _graph.Add(Layout(_index!)); _artificialsAdmitted = true;
    }
    private void InsertArtificialPair()
    {
        // Insert selects the first visible ancestor only for position. Index's true parent remains the captured HEAD.
        // The native fallback intentionally creates only the WT→Index segment, not a fictional Index→ancestor edge.
        _graph.Insert(Layout(_working!), Layout(_index!), _headAncestors); _artificialsAdmitted = true;
    }
    private void RefreshOrder()
    {
        _ordered.Clear(); _orderedItems.Clear();
        if (RowCount > 0) { _graph.GetNodeForRow(RowCount - 1); }
        for (int row = 0; row < RowCount; row++)
        {
            object item = _byKey[_graph.GetNodeForRow(row)!.Objectid]; _orderedItems.Add(item);
            if (item is GitCommit commit) { _ordered.Add(commit); }
        }
    }

    internal bool IsRelative(string key) => _graph.TryGetNode(key, out RevisionGraphRevision? node) && node.IsRelative;

    internal IReadOnlyList<string> GetChildren(string hash)
        => _graph.TryGetNode(hash, out RevisionGraphRevision? node) ? node.Children.Reverse().Select(child => child.Objectid).ToArray() : [];

    internal void PrepareViewport(int first, int last, CancellationToken token = default)
    {
        if (RowCount == 0 || last < first) { return; }
        first = Math.Clamp(first, 0, RowCount - 1); last = Math.Clamp(last, first, RowCount - 1);
        if (_preparedVersion == _version && _preparedFirst == first && _preparedLast == last) { return; }
        // Same native policy as RevisionDataGridView: cache the visible page and the following page.
        // CacheTo itself supplies the algorithm's straightening/look-ahead. Never spin waiting for future input.
        int lastToCache = (int)Math.Min(RowCount - 1L, (long)last + (last - first + 1) + 2);
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
        _visibleNodes.Clear(); _visibleRealNodes.Clear(); _visibleStrokes.Clear(); VisibleLaneCount = 1;
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
                if (IsComplete && index == RowCount - 1 && segment.Parent != row.Revision && !_graph.Contains(segment.Parent.Objectid))
                { _visibleStrokes.Add(new(index, center, center, false, segmentColor, segment.Child.Objectid, segment.Parent.Objectid, true)); }
            }
            object item = _byKey[row.Revision.Objectid];
            _visibleNodes.Add(index, new(item, lane, color));
            if (item is GitCommit commit) { _visibleRealNodes.Add(index, new(commit, lane, color)); }
        }
        _preparedVersion = _version; _preparedFirst = first; _preparedLast = last;
    }

    internal PreparedHistoryGraphNode? GetPreparedItemNode(int row) => _visibleNodes.GetValueOrDefault(row);
    internal GraphNode? GetPreparedNode(int row) => _visibleRealNodes.GetValueOrDefault(row);
    internal IReadOnlyList<GraphRenderSegment> RenderSegmentsForRow(int row, double laneWidth, double rowHeight,
        bool skipSecondarySharedSegments, IReadOnlySet<string>? highlightedIds)
        => _visibleNodes.ContainsKey(row) ? RevisionGraphRenderer.GetSegmentsForRow(_graph, row, laneWidth, rowHeight,
            skipSecondarySharedSegments, highlightedIds) : [];

    private void InvalidateViewport()
    { _version++; _preparedVersion = -1; _visibleNodes.Clear(); _visibleRealNodes.Clear(); _visibleStrokes.Clear(); }
}
