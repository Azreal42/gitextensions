using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;

namespace GitExtensions.Desktop.Controls;

public sealed class CommitGraph : Control
{
    public const double RowHeight = 24;
    private const double LaneWidth = 16;
    private const double LeftInset = 12;
    // Matches GraphBranch1..7 in GitUI/Themes/invariant.css and dark.css; slot 8 is intentionally unused upstream.
    private static readonly IBrush[] LightPalette =
    [
        Brush.Parse("#f064a0"), Brush.Parse("#78b4e6"), Brush.Parse("#24c221"), Brush.Parse("#a078f0"),
        Brush.Parse("#dd3228"), Brush.Parse("#1ac6a6"), Brush.Parse("#e7b00f")
    ];
    private static readonly IBrush[] DarkPalette =
    [
        Brush.Parse("#db5b93"), Brush.Parse("#6fa7d4"), Brush.Parse("#1da31b"), Brush.Parse("#8a67cf"),
        Brush.Parse("#c02a22"), Brush.Parse("#17aa8f"), Brush.Parse("#ca9b0d")
    ];

    private IReadOnlyList<GitCommit> _commits = [];
    private IReadOnlyList<GitReference> _references = [];
    private CommitGraphLayout _layout = CommitGraphLayout.Build([]);
    private Dictionary<string, GitReference[]> _refsByHash = new(StringComparer.Ordinal);
    private double _verticalOffset;
    private string? _currentHead;

    public CommitGraph()
    {
        ClipToBounds = true;
        PointerMoved += ShowCommitTooltip;
        PointerExited += (_, _) => ToolTip.SetTip(this, null);
    }

    public IReadOnlyList<GitCommit> Commits
    {
        get => _commits;
        set
        {
            _commits = value ?? [];
            _layout = CommitGraphLayout.Build(_commits);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public IReadOnlyList<GitReference> References
    {
        get => _references;
        set
        {
            _references = value ?? [];
            _refsByHash = _references.GroupBy(reference => reference.Hash, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public string? CurrentHead
    {
        get => _currentHead;
        set { _currentHead = value; InvalidateVisual(); }
    }

    public double VerticalOffset
    {
        get => _verticalOffset;
        set { _verticalOffset = Math.Max(0, value); InvalidateVisual(); }
    }

    // A non-topological input may be reordered by the upstream layout algorithm.
    public IReadOnlyList<GitCommit> OrderedCommits => _layout.Nodes.Select(node => node.Commit).ToArray();

    protected override Size MeasureOverride(Size availableSize)
        => new(Math.Max(72, LeftInset * 2 + _layout.LaneCount * LaneWidth) + (_references.Count > 0 ? 110 : 0), _layout.Nodes.Count * RowHeight);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        bool dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        IBrush[] palette = dark ? DarkPalette : LightPalette;
        int firstRow = Math.Max(0, (int)(_verticalOffset / RowHeight));
        int lastRow = Math.Min(_layout.Nodes.Count - 1, (int)((_verticalOffset + Bounds.Height) / RowHeight));

        foreach (GraphStroke stroke in _layout.Strokes)
        {
            if (stroke.Row < firstRow || stroke.Row > lastRow)
            {
                continue;
            }

            double centerY = (stroke.Row + 0.5) * RowHeight - _verticalOffset;
            double boundaryY = centerY + (stroke.UpperHalf ? -1 : 1) * RowHeight / 2;
            double centerX = X(stroke.CenterLane);
            double boundaryX = (centerX + X(stroke.AdjacentLane)) / 2;
            StreamGeometry geometry = new();
            using (StreamGeometryContext path = geometry.Open())
            {
                path.BeginFigure(new Point(centerX, centerY), false);
                path.CubicBezierTo(new Point(centerX, (centerY + boundaryY) / 2),
                    new Point(boundaryX, (centerY + boundaryY) / 2), new Point(boundaryX, boundaryY));
                path.EndFigure(false);
            }

            context.DrawGeometry(null, new Pen(palette[stroke.Color], 2), geometry);
            if (stroke.ContinuesOutsideHistory)
            {
                context.DrawEllipse(palette[stroke.Color], null, new Point(boundaryX, boundaryY - 3), 2, 2);
            }
        }

        for (int row = firstRow; row <= lastRow; row++)
        {
            GraphNode node = _layout.Nodes[row];
            Point center = new(X(node.Lane), (row + 0.5) * RowHeight - _verticalOffset);
            IBrush brush = palette[node.Color];
            _refsByHash.TryGetValue(node.Commit.Hash, out GitReference[]? refs);
            bool isHead = node.Commit.Hash == _currentHead || refs?.Any(reference => reference.IsCurrent) is true;
            Pen? outline = isHead ? new Pen(dark ? Brushes.White : Brushes.Black, 2) : null;

            if (refs is { Length: > 0 })
            {
                context.DrawRectangle(brush, outline, new Rect(center.X - 5, center.Y - 5, 10, 10), 1, 1);
                double x = LeftInset * 2 + _layout.LaneCount * LaneWidth;
                foreach (GitReference reference in refs.OrderByDescending(reference => reference.IsCurrent).ThenBy(reference => reference.Name, StringComparer.Ordinal))
                {
                    Color color = ReferenceColor(reference, dark);
                    IBrush badgeBrush = new SolidColorBrush(color);
                    FormattedText text = new(reference.ShortName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface(FontFamily.Default), 11, badgeBrush);
                    double width = Math.Min(105, text.Width + 10);
                    double available = Bounds.Width - x - 2;
                    if (available < 16) { break; }
                    width = Math.Min(width, available);
                    Rect badge = new(x, center.Y - 8, width, 16);
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B)), new Pen(badgeBrush, 1), badge, 3, 3);
                    using (context.PushClip(badge.Deflate(3)))
                    {
                        context.DrawText(text, new Point(x + 4, center.Y - text.Height / 2));
                    }

                    x += width + 4;
                }
            }
            else
            {
                context.DrawEllipse(brush, outline, center, node.Commit.Parents.Count > 1 ? 6 : 4, node.Commit.Parents.Count > 1 ? 6 : 4);
            }
        }
    }

    private static double X(int lane) => LeftInset + (lane + 0.5) * LaneWidth;

    private static Color ReferenceColor(GitReference reference, bool dark)
        => Color.Parse(reference.IsBranch ? dark ? "#7FE28A" : "#008000"
            : reference.IsRemote ? dark ? "#fd9797" : "#8b0009"
            : reference.IsTag ? dark ? "#40BAF7" : "#00008b"
            : dark ? "#cfb3b3" : "#808080");

    private void ShowCommitTooltip(object? sender, PointerEventArgs args)
    {
        int row = (int)((args.GetPosition(this).Y + _verticalOffset) / RowHeight);
        if (row < 0 || row >= _layout.Nodes.Count)
        {
            ToolTip.SetTip(this, null);
            return;
        }

        GitCommit commit = _layout.Nodes[row].Commit;
        string references = _refsByHash.TryGetValue(commit.Hash, out GitReference[]? refs)
            ? string.Join(", ", refs.Select(reference => reference.ShortName)) + "\n" : "";
        ToolTip.SetTip(this, $"{references}{commit.Hash}\n{commit.Subject}\n{string.Join("\n", commit.Parents)}");
    }
}
