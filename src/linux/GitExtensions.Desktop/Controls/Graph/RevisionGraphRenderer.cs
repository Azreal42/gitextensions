// Port of Graph/Rendering/GraphRenderer.cs. Keep sharing, junction and diagonal routing in sync with Windows.
using Avalonia;

namespace GitExtensions.Desktop.Controls.Graph;

internal readonly record struct GraphPathPart(Point From, Point To, Point? Control1 = null, Point? Control2 = null)
{
    internal bool IsBezier => Control1 is not null;
}

internal sealed record GraphRenderSegment(int Color, string Child, string Parent, IReadOnlyList<GraphPathPart> Path);

internal static class RevisionGraphRenderer
{
    private const int MaxLanes = RevisionGraph.MaxLanes;
    private const int _noLane = -10;
    private const int LaneLineWidth = 2;

    internal static IReadOnlyList<GraphRenderSegment> GetSegmentsForRow(RevisionGraph graph, int index,
        double laneWidth, double rowHeight, bool skipSecondarySharedSegments, IReadOnlySet<string>? highlightedIds = null)
    {
        IRevisionGraphRow? currentRow = graph.GetSegmentsForRow(index);
        if (currentRow is null) { return []; }
        IRevisionGraphRow? previousRow = graph.GetSegmentsForRow(index - 1);
        IRevisionGraphRow? nextRow = graph.GetSegmentsForRow(index + 1);
        RevisionGraphConfig config = graph.Config;
        List<GraphRenderSegment> result = [];
        foreach (RevisionGraphSegment segment in currentRow.Segments.Reverse().OrderBy(segment => segment.Child.IsRelative)
            .ThenBy(segment => highlightedIds?.Contains(segment.Child.Objectid) is true || highlightedIds?.Contains(segment.Parent.Objectid) is true))
        {
            SegmentLanesInfo lanes = GetLanesInfo(segment, previousRow, currentRow, nextRow, skipSecondarySharedSegments, config.MergeGraphLanesHavingCommonParent);
            if (!lanes.DrawFromStart && !lanes.DrawToEnd) { continue; }
            SegmentPointsInfo p = new(
                new((lanes.StartLane + .5) * laneWidth, -rowHeight / 2),
                new((lanes.CenterLane + .5) * laneWidth, rowHeight / 2),
                new((lanes.EndLane + .5) * laneWidth, rowHeight * 1.5));
            List<GraphPathPart> path = [];
            RevisionGraphSegmentRenderer renderer = new(config.Options, laneWidth, rowHeight, path);
            if (config.RenderGraphWithDiagonals)
            {
                Lazy<DiagonalSegmentInfo> previousInfo = new(() =>
                {
                    ArgumentNullException.ThrowIfNull(previousRow);
                    SegmentLanesInfo previous = GetLanesInfo(segment, graph.GetSegmentsForRow(index - 2), previousRow, currentRow, skipSecondarySharedSegments, config.MergeGraphLanesHavingCommonParent);
                    return GetDiagonalSegmentInfo(previous, config.MergeGraphLanesHavingCommonParent, laneWidth);
                });
                Lazy<DiagonalSegmentInfo> nextInfo = new(() =>
                {
                    ArgumentNullException.ThrowIfNull(nextRow);
                    SegmentLanesInfo next = GetLanesInfo(segment, currentRow, nextRow, graph.GetSegmentsForRow(index + 2), skipSecondarySharedSegments, config.MergeGraphLanesHavingCommonParent);
                    return GetDiagonalSegmentInfo(next, config.MergeGraphLanesHavingCommonParent, laneWidth);
                });
                DiagonalSegmentInfo currentInfo = GetDiagonalSegmentInfo(lanes, config.MergeGraphLanesHavingCommonParent, laneWidth);
                DrawSegmentWithDiagonals(renderer, p, previousInfo, currentInfo, nextInfo);
            }
            else { DrawSegmentCurvy(renderer, p, lanes); }
            if (path.Count > 0)
            {
                int color = segment.LaneInfo?.Color ?? RevisionGraphLaneColor.GetColorForLane(RevisionGraphLaneColor.Hash(currentRow.Revision.Objectid));
                result.Add(new(color, segment.Child.Objectid, segment.Parent.Objectid, path.ToArray()));
            }
        }
        return result;
    }

    private static SegmentLanesInfo GetLanesInfo(in RevisionGraphSegment revisionGraphSegment,
        in IRevisionGraphRow? previousRow,
        in IRevisionGraphRow currentRow,
        in IRevisionGraphRow? nextRow,
        in bool skipSecondarySharedSegments,
        in bool mergeGraphLanesHavingCommonParent,
        in Action<LaneInfo?>? setLaneInfo = null)
    {
        Lane currentLane = currentRow.GetLaneForSegment(revisionGraphSegment);

        int startLane = _noLane;
        int centerLane = _noLane;
        int endLane = _noLane;

        // Avoid drawing the same curve twice (caused aliasing artifacts, particularly when in different colors)
        if (skipSecondarySharedSegments && currentLane.Sharing == LaneSharing.Entire)
        {
            return new SegmentLanesInfo(startLane, centerLane, endLane, PrimaryEndLane: endLane, IsTheRevisionLane: false, DrawFromStart: false, DrawToEnd: false);
        }

        centerLane = currentLane.Index;
        bool isTheRevisionLane = true;
        if (revisionGraphSegment.Parent == currentRow.Revision)
        {
            // This lane ends here
            startLane = GetLaneForRow(previousRow, revisionGraphSegment);
            setLaneInfo?.Invoke(revisionGraphSegment.LaneInfo);
        }
        else
        {
            if (revisionGraphSegment.Child == currentRow.Revision)
            {
                // This lane starts here
                endLane = GetLaneForRow(nextRow, revisionGraphSegment);
                setLaneInfo?.Invoke(revisionGraphSegment.LaneInfo);
            }
            else
            {
                // This lane crosses
                startLane = GetLaneForRow(previousRow, revisionGraphSegment);
                endLane = GetLaneForRow(nextRow, revisionGraphSegment);
                isTheRevisionLane = false;
            }
        }

        int primaryEndLane = endLane;
        switch (currentLane.Sharing)
        {
            case LaneSharing.DifferentStart:
                if (mergeGraphLanesHavingCommonParent)
                {
                    if (skipSecondarySharedSegments)
                    {
                        endLane = _noLane;
                    }
                }
                else if (endLane != _noLane)
                {
                    throw new Exception($"{currentRow.Revision.Objectid}: lane {centerLane} has DifferentStart but has EndLane {endLane} (StartLane {startLane})");
                }

                break;

            case LaneSharing.DifferentEnd:
                if (startLane != _noLane)
                {
                    throw new Exception($"{currentRow.Revision.Objectid}: lane {centerLane} has DifferentEnd but has StartLane {startLane} (EndLane {endLane})");
                }

                break;
        }

        return new SegmentLanesInfo(startLane, centerLane, endLane, primaryEndLane, isTheRevisionLane,
            DrawFromStart: startLane >= 0 && centerLane >= 0 && (startLane <= MaxLanes || centerLane <= MaxLanes),
            DrawToEnd: endLane >= 0 && centerLane >= 0 && (endLane <= MaxLanes || centerLane <= MaxLanes));
    }

    private static DiagonalSegmentInfo GetDiagonalSegmentInfo(in SegmentLanesInfo currentLanes, bool mergeGraphLanesHavingCommonParent, double laneWidth)
    {
        bool drawFromStart = currentLanes.DrawFromStart;
        bool drawToEnd = currentLanes.DrawToEnd;
        bool isTheRevisionLane = currentLanes.IsTheRevisionLane;

        int startShift = currentLanes.CenterLane - currentLanes.StartLane;
        int endShift = currentLanes.EndLane - currentLanes.CenterLane;
        bool startIsDiagonal = Math.Abs(startShift) == 1;
        bool endIsDiagonal = Math.Abs(endShift) == 1;
        bool isBowOfDiagonals = startIsDiagonal && endIsDiagonal && -Math.Sign(startShift) == Math.Sign(endShift);
        double bowOffset = Math.Floor(laneWidth / 6);
        double junctionBowOffset = mergeGraphLanesHavingCommonParent ? LaneLineWidth : bowOffset;
        double horizontalOffset = isBowOfDiagonals ? -Math.Sign(startShift) * junctionBowOffset : 0;

        // Go perpendicularly through the center in order to avoid crossing independend nodes
        bool drawCenterToStartPerpendicularly = drawFromStart && (startShift == 0 || (!startIsDiagonal && !isTheRevisionLane));
        bool drawCenterToEndPerpendicularly = drawToEnd && (endShift == 0 || (!endIsDiagonal && !isTheRevisionLane));
        bool drawCenterPerpendicularly = isBowOfDiagonals;
        bool drawCenter = drawCenterPerpendicularly
            || !drawFromStart
            || !drawToEnd
            || (!drawCenterToStartPerpendicularly && !drawCenterToEndPerpendicularly);

        // handle non-straight junctions
        if (currentLanes.EndLane < 0 && currentLanes.PrimaryEndLane >= 0 && startShift != 0)
        {
            endShift = currentLanes.PrimaryEndLane - currentLanes.CenterLane;
            bool sameDirection = Math.Sign(endShift) == Math.Sign(startShift);
            if (startIsDiagonal)
            {
                int endDelta = Math.Abs(endShift);
                if (!sameDirection || endDelta > 1)
                {
                    drawCenterToEndPerpendicularly = true;
                    drawCenter = false;
                    horizontalOffset = -Math.Sign(startShift) * (endDelta != 1 || sameDirection ? LaneLineWidth / 3 : bowOffset);
                }
            }
            else if (Math.Abs(endShift) == 1)
            {
                // multi-lane crossing continued by a diagonal
                drawCenterToStartPerpendicularly = false;
                if (!sameDirection)
                {
                    // bow
                    horizontalOffset = -Math.Sign(startShift) * LaneLineWidth * 2 / 3;
                }
            }
            else
            {
                // multi-lane crossing continued by a straight or a multi-lane crossing
                drawCenterToStartPerpendicularly = false;
            }
        }

        return new DiagonalSegmentInfo(drawFromStart, drawToEnd,
            drawCenterToStartPerpendicularly, drawCenter, drawCenterPerpendicularly, drawCenterToEndPerpendicularly,
            isTheRevisionLane, horizontalOffset);
    }

    private static void DrawSegmentCurvy(RevisionGraphSegmentRenderer segmentRenderer, in SegmentPointsInfo p, in SegmentLanesInfo lanes)
    {
        if (lanes.DrawFromStart)
        {
            segmentRenderer.DrawTo(p.Start);
        }

        segmentRenderer.DrawTo(p.Center);

        if (lanes.DrawToEnd)
        {
            segmentRenderer.DrawTo(p.End);
        }
    }

    private static void DrawSegmentWithDiagonals(RevisionGraphSegmentRenderer segmentRenderer,
        in SegmentPointsInfo p,
        in Lazy<DiagonalSegmentInfo> previousSegmentInfo,
        in DiagonalSegmentInfo current,
        in Lazy<DiagonalSegmentInfo> nextSegmentInfo)
    {
        double halfPerpendicularHeight = Math.Floor(segmentRenderer.RowHeight / 6);

        if (current.DrawFromStart)
        {
            DiagonalSegmentInfo previous = previousSegmentInfo.Value;
            double startX = p.Start.X + previous.HorizontalOffset;
            if (previous.DrawCenterToEndPerpendicularly)
            {
                segmentRenderer.DrawTo(startX, p.Start.Y + halfPerpendicularHeight);
            }
            else if (previous.DrawCenter)
            {
                segmentRenderer.DrawTo(startX, p.Start.Y, previous.DrawCenterPerpendicularly);
            }
            else
            {
                segmentRenderer.DrawTo(startX, p.Start.Y - halfPerpendicularHeight);
            }
        }

        double centerX = p.Center.X + current.HorizontalOffset;

        if (current.DrawCenterToStartPerpendicularly)
        {
            segmentRenderer.DrawTo(centerX, p.Center.Y - halfPerpendicularHeight);
        }

        if (current.DrawCenter)
        {
            segmentRenderer.DrawTo(centerX, p.Center.Y, current.DrawCenterPerpendicularly);
        }

        if (current.DrawCenterToEndPerpendicularly)
        {
            segmentRenderer.DrawTo(centerX, p.Center.Y + halfPerpendicularHeight);
        }

        if (current.DrawToEnd)
        {
            DiagonalSegmentInfo next = nextSegmentInfo.Value;
            double endX = p.End.X + next.HorizontalOffset;
            if (next.DrawCenterToStartPerpendicularly)
            {
                segmentRenderer.DrawTo(endX, p.End.Y - halfPerpendicularHeight);
            }
            else if (next.DrawCenter)
            {
                segmentRenderer.DrawTo(endX, p.End.Y, next.DrawCenterPerpendicularly);
            }
            else
            {
                segmentRenderer.DrawTo(endX, p.End.Y + halfPerpendicularHeight);
            }
        }
    }

    private static int GetLaneForRow(IRevisionGraphRow? row, RevisionGraphSegment revisionGraphRevision)
    {
        if (row is not null)
        {
            int lane = row.GetLaneForSegment(revisionGraphRevision).Index;
            if (lane >= 0)
            {
                return lane;
            }
        }

        return _noLane;
    }
    private readonly record struct SegmentPointsInfo(Point Start, Point Center, Point End);
    private readonly record struct SegmentLanesInfo(int StartLane, int CenterLane, int EndLane, int PrimaryEndLane,
        bool IsTheRevisionLane, bool DrawFromStart, bool DrawToEnd);
    private readonly record struct DiagonalSegmentInfo(bool DrawFromStart, bool DrawToEnd,
        bool DrawCenterToStartPerpendicularly, bool DrawCenter, bool DrawCenterPerpendicularly,
        bool DrawCenterToEndPerpendicularly, bool IsTheRevisionLane, double HorizontalOffset);
}
