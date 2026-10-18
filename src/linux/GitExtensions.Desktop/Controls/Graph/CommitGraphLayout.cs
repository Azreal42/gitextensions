using GitExtensions.Linux;

namespace GitExtensions.Desktop.Controls.Graph;

internal sealed record GraphNode(GitCommit Commit, int Lane, int Color);
internal sealed record GraphStroke(int Row, int CenterLane, int AdjacentLane, bool UpperHalf, int Color, string Child, string Parent, bool ContinuesOutsideHistory);

internal sealed class CommitGraphLayout
{
    private readonly RevisionGraph _graph;
    public RevisionGraphOptions Options => _graph.Config.Options;
    public IReadOnlyList<GraphNode> Nodes { get; }
    public IReadOnlyList<GraphStroke> Strokes { get; }
    public int LaneCount { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children { get; }

    private CommitGraphLayout(RevisionGraph graph, IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphStroke> strokes, int laneCount,
        IReadOnlyDictionary<string, IReadOnlyList<string>> children)
    {
        _graph = graph;
        Nodes = nodes;
        Strokes = strokes;
        LaneCount = laneCount;
        Children = children;
    }

    internal IReadOnlyList<GraphRenderSegment> RenderSegmentsForRow(int row, double laneWidth, double rowHeight, bool skipSecondarySharedSegments = true, IReadOnlySet<string>? highlightedIds = null)
        => RevisionGraphRenderer.GetSegmentsForRow(_graph, row, laneWidth, rowHeight, skipSecondarySharedSegments, highlightedIds);

    public static CommitGraphLayout Build(IReadOnlyList<GitCommit> commits, bool onlyFirstParent = false, RevisionGraphOptions? options = null)
    {
        RevisionGraph graph = new(options) { OnlyFirstParent = onlyFirstParent };
        Dictionary<string, GitCommit> byId = new(StringComparer.Ordinal);
        foreach (GitCommit commit in commits)
        {
            if (byId.TryAdd(commit.Hash, commit))
            {
                graph.Add(new LayoutRevision(commit.Hash, commit.Parents.Distinct(StringComparer.Ordinal).ToArray(), commit.Subject));
            }
        }

        graph.LoadingCompleted();
        graph.CacheTo(graph.Count, graph.Count);

        List<GraphNode> nodes = [];
        List<GraphStroke> strokes = [];
        int laneCount = 1;
        for (int index = 0; index < graph.Count; index++)
        {
            IRevisionGraphRow row = graph.GetSegmentsForRow(index) ?? throw new InvalidOperationException("Revision graph row was not cached.");
            IRevisionGraphRow? previous = graph.GetSegmentsForRow(index - 1);
            IRevisionGraphRow? next = graph.GetSegmentsForRow(index + 1);
            int nodeLane = row.GetCurrentRevisionLane();
            laneCount = Math.Max(laneCount, row.GetLaneCount());
            int nodeColor = RevisionGraphLaneColor.GetColorForLane(RevisionGraphLaneColor.Hash(row.Revision.Objectid));

            foreach (RevisionGraphSegment segment in row.Segments)
            {
                int center = row.GetLaneForSegment(segment).Index;
                if (center < 0)
                {
                    continue;
                }

                int color = segment.LaneInfo?.Color ?? nodeColor;
                if (segment.Child == row.Revision || segment.Parent == row.Revision)
                {
                    nodeColor = color;
                }

                if (segment.Child != row.Revision && previous?.GetLaneForSegment(segment).Index is int start && start >= 0)
                {
                    strokes.Add(new(index, center, start, true, color, segment.Child.Objectid, segment.Parent.Objectid, false));
                }

                if (segment.Parent != row.Revision)
                {
                    int end = next?.GetLaneForSegment(segment).Index ?? -1;
                    bool truncated = index == graph.Count - 1 && !byId.ContainsKey(segment.Parent.Objectid);
                    if (end >= 0 || truncated)
                    {
                        // A missing ancestor continues to the loaded-history boundary, never to an unrelated node.
                        strokes.Add(new(index, center, end >= 0 ? end : center, false, color, segment.Child.Objectid, segment.Parent.Objectid, truncated));
                    }
                }
            }

            nodes.Add(new(byId[row.Revision.Objectid], nodeLane, nodeColor));
        }

        // Match RevisionDataGridView.GetRevisionChildren: reverse the graph's
        // immutable stack, preserving edge insertion order rather than row order.
        Dictionary<string, IReadOnlyList<string>> children = new(StringComparer.Ordinal);
        foreach (string hash in byId.Keys)
        {
            children[hash] = graph.TryGetNode(hash, out RevisionGraphRevision? node)
                ? node.Children.Reverse().Select(child => child.Objectid).ToArray() : [];
        }
        return new(graph, nodes, strokes, laneCount, children);
    }
}
