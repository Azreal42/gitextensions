using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Desktop.Controls;

/// <summary>Native rendering of ImpactControl's weekly logarithmic ribbons, labels, hover selection and Bezier transitions.</summary>
public sealed class GitImpactGraph : Control
{
    private GitImpactReport? _report;
    private GitImpactLayout? _layout;
    private readonly Dictionary<string, Geometry> _paths = new(StringComparer.Ordinal);
    public event EventHandler? SelectedAuthorChanged;
    public string SelectedAuthor { get; private set; } = "";
    public GitImpactLayout? LayoutModel => _layout;

    public GitImpactGraph()
    {
        ClipToBounds = true;
        PointerMoved += (_, args) => TrySelectAuthor(args.GetPosition(this));
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }
    public GitImpactReport? Report
    {
        get => _report;
        set
        {
            _report = value;
            _layout = null;
            _paths.Clear();
            SelectedAuthor = "";
            SelectedAuthorChanged?.Invoke(this, EventArgs.Empty);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }
    public Color AuthorColor(string author)
    {
        GitImpactAuthor? data = _report?.Authors.FirstOrDefault(item => item.Name == author);
        return data is null ? Colors.Transparent : StatisticsChartColors.Adapt(Color.Parse(data.Color), this);
    }
    public bool TrySelectAuthor(Point point)
    {
        EnsureLayout();
        if (_report is null) return false;
        for (int i = _report.DrawOrder.Count - 1; i >= 0; i--)
        {
            string author = _report.DrawOrder[i];
            if (!_paths.TryGetValue(author, out Geometry? path) || !path.FillContains(point)) continue;
            if (SelectedAuthor == author) return false;
            SelectedAuthor = author;
            InvalidateVisual();
            SelectedAuthorChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        return false;
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = _report?.CreateLayout(0).Width ?? 0;
        return new(Math.Max(60, width), double.IsFinite(availableSize.Height) ? availableSize.Height : 400);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(StatisticsChartColors.Brush(this, "Editor", "#ffffff"), null, new Rect(Bounds.Size));
        EnsureLayout();
        if (_layout is null || _report is null) return;
        foreach (string author in _layout.DrawOrder.Where(author => author != SelectedAuthor).Append(SelectedAuthor))
        {
            if (!_paths.TryGetValue(author, out Geometry? geometry)) continue;
            IBrush fill = new SolidColorBrush(AuthorColor(author));
            Pen? pen = author == SelectedAuthor ? new Pen(StatisticsChartColors.Brush(this, "Text", "#000000"), 2) : null;
            context.DrawGeometry(fill, pen, geometry);
        }
        foreach (GitImpactArea area in _layout.Areas)
        {
            foreach (GitImpactBlock block in area.Blocks.Where(block => block.ShowChangedLinesLabel))
            {
                FormattedText text = Text(block.Data.ChangedLines.ToString(CultureInfo.CurrentCulture), 10 * 96d / 72, Brushes.White);
                Point center = new(block.Bounds.X + block.Bounds.Width / 2, block.Bounds.Y + block.Bounds.Height / 2);
                context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
            }
        }
        foreach (GitImpactWeekLabel label in _layout.WeekLabels)
        {
            FormattedText text = Text(label.Week.ToShortDateString(), 8 * 96d / 72, Brushes.Gray);
            context.DrawText(text, new Point(label.Center.X - text.Width / 2, label.Center.Y + text.Height / 2));
        }
    }
    private static FormattedText Text(string value, double size, IBrush brush)
        => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Arial, DejaVu Sans"), size, brush);
    private void EnsureLayout()
    {
        if (_report is null) return;
        double height = Math.Max(0, Bounds.Height);
        if (_layout is not null && _layout.Height == height) return;
        _layout = _report.CreateLayout(height);
        _paths.Clear();
        foreach (GitImpactArea area in _layout.Areas)
        {
            StreamGeometry geometry = new();
            using (StreamGeometryContext path = geometry.Open())
            {
                path.BeginFigure(new Point(area.Start.X, area.Start.Y), true);
                foreach (GitImpactSegment segment in area.Segments)
                {
                    Point end = new(segment.End.X, segment.End.Y);
                    if (segment.Control1 is GitImpactPoint first && segment.Control2 is GitImpactPoint second)
                        path.CubicBezierTo(new Point(first.X, first.Y), new Point(second.X, second.Y), end);
                    else path.LineTo(end);
                }
                path.EndFigure(true);
            }
            _paths.Add(area.Author, geometry);
        }
    }
}
