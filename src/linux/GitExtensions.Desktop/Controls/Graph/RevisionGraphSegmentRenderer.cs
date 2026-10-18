// Port of Graph/Rendering/SegmentRenderer.cs. Avalonia records the same line/Bezier operations before painting a row.
using Avalonia;

namespace GitExtensions.Desktop.Controls.Graph;

internal sealed class RevisionGraphSegmentRenderer(RevisionGraphOptions options, double laneWidth, double rowHeight, List<GraphPathPart> path)
{
    private bool _fromPerpendicularly = true;
    private Point? _fromPoint;
    internal double RowHeight => rowHeight;

    internal void DrawTo(double x, double y, bool toPerpendicularly = true) => DrawTo(new Point(x, y), toPerpendicularly);
    internal void DrawTo(Point toPoint, bool toPerpendicularly = true)
    {
        try
        {
            if (_fromPoint is Point from) { Draw(from, toPoint, _fromPerpendicularly, toPerpendicularly); }
        }
        finally { _fromPoint = toPoint; _fromPerpendicularly = toPerpendicularly; }
    }

    private void Draw(Point fromPoint, Point toPoint, bool fromPerpendicularly, bool toPerpendicularly)
    {
        if (fromPoint.X == toPoint.X) { DrawLine(fromPoint, toPoint); return; }
        // Same subpixel compensation as Windows' antialiased segment renderer.
        const double antiAliasOffset = -1d / 8d;
        Point e0 = new(antiAliasOffset + fromPoint.X, fromPoint.Y);
        Point e1 = new(antiAliasOffset + toPoint.X, toPoint.Y);
        double height = toPoint.Y - fromPoint.Y;
        double width = toPoint.X - fromPoint.X;
        bool singleLane = Math.Abs(width) <= laneWidth;
        Vector cellShift = new(Math.Sign(width) * laneWidth, rowHeight);
        if (!fromPerpendicularly && !toPerpendicularly && singleLane) { DrawLine(e0, e1); return; }
        Point c0 = e0, c1 = e1;
        const double diagonalFractionCurve = 1d / 4d;
        double perpendicularOffset = diagonalFractionCurve * cellShift.Y;
        if (fromPerpendicularly && toPerpendicularly)
        {
            if (options.RenderGraphWithDiagonals && singleLane)
            {
                c0 = new(c0.X, c0.Y + perpendicularOffset);
                c1 = new(c1.X, c1.Y - perpendicularOffset);
                Point mid = new((e0.X + e1.X) / 2, (e0.Y + e1.Y) / 2);
                Vector shift = diagonalFractionCurve * cellShift;
                DrawBezier(e0, c0, mid - shift, mid);
                DrawBezier(e1, c1, mid + shift, mid);
                return;
            }
            c0 = new(c0.X, (fromPoint.Y + toPoint.Y) / 2);
            c1 = new(c1.X, (fromPoint.Y + toPoint.Y) / 2);
        }
        else if (singleLane)
        {
            double diagonalFractionStraight = height < cellShift.Y ? 2d / 5d : 1d / 2d;
            if (fromPerpendicularly)
            {
                MoveDrawDiagonallyFrom(ref e1, out _, -diagonalFractionStraight);
                c1 = e1 - diagonalFractionCurve * cellShift;
                c0 = new(c0.X, c0.Y + perpendicularOffset);
            }
            else
            {
                MoveDrawDiagonallyFrom(ref e0, out _, diagonalFractionStraight);
                c0 = e0 + diagonalFractionCurve * cellShift;
                c1 = new(c1.X, c1.Y - perpendicularOffset);
            }
        }
        else
        {
            const double diagonalFractionStraight = 1d / 6d;
            if (fromPerpendicularly) { c0 = new(c0.X, c0.Y + perpendicularOffset); }
            else { MoveDrawDiagonallyFrom(ref e0, out c0, diagonalFractionStraight); }
            if (toPerpendicularly) { c1 = new(c1.X, c1.Y - perpendicularOffset); }
            else { MoveDrawDiagonallyFrom(ref e1, out c1, -diagonalFractionStraight); }
        }
        DrawBezier(e0, c0, c1, e1);
        return;

        void MoveDrawDiagonallyFrom(ref Point start, out Point bezierCenter, double fraction)
        {
            Vector shift = fraction * cellShift;
            Point end = start + shift;
            DrawLine(start, end); start = end; bezierCenter = end + shift;
        }
    }

    private void DrawLine(Point from, Point to) => path.Add(new(from, to));
    private void DrawBezier(Point from, Point c0, Point c1, Point to) => path.Add(new(from, to, c0, c1));
}
