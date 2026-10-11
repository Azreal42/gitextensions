using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GitExtensions.Linux.Plugins;
using StatisticsPieChartModel = GitExtensions.Linux.Plugins.StatisticsPieChart;

namespace GitExtensions.Desktop.Controls;

/// <summary>The original -30-degree, 20%-thick projected pie with gradual side shading and darker edges.</summary>
public sealed class StatisticsPieChart : Control
{
    public const double InitialAngle = -30;
    public const double SliceRelativeHeight = 0.20;
    private StatisticsPieChartModel? _model;
    private readonly List<(int Index, Geometry Top, IReadOnlyList<Geometry> Sides)> _geometry = [];
    private int _highlighted = -1;
    public event EventHandler<StatisticsPieSlice>? SliceSelected;

    public StatisticsPieChart()
    {
        ClipToBounds = true;
        PointerMoved += (_, args) => Highlight(args.GetPosition(this));
        PointerExited += (_, _) => { _highlighted = -1; ToolTip.SetTip(this, null); InvalidateVisual(); };
        PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            int index = SliceAt(args.GetPosition(this));
            if (index >= 0 && _model is not null) SliceSelected?.Invoke(this, _model.Slices[index]);
        };
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    // Aliased in the using below to distinguish the framework-free model from this control.
    public StatisticsPieChartModel? Chart
    {
        get => _model;
        set { _model = value; _highlighted = -1; InvalidateVisual(); }
    }
    public Func<StatisticsPieSlice, string>? ToolTipFormatter { get; set; }
    public int HighlightedSlice => _highlighted;
    public double RenderedThickness { get; private set; }

    public int SliceAt(Point point)
    {
        for (int i = _geometry.Count - 1; i >= 0; i--)
        {
            var slice = _geometry[i];
            if (slice.Top.FillContains(point) || slice.Sides.Any(side => side.FillContains(point))) return slice.Index;
        }
        return -1;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _geometry.Clear();
        double size = Math.Min(Bounds.Width, Bounds.Height) - 20;
        if (_model is null || _model.Slices.Sum(slice => slice.Value) <= 0 || size <= 0) { RenderedThickness = 0; return; }
        // Upstream SetPieStyle makes the control square and leaves 10-pixel margins, without fit-to-bounds or slice displacement.
        double thickness = size * SliceRelativeHeight, radiusX = size / 2, radiusY = size * (1 - SliceRelativeHeight) / 2;
        RenderedThickness = thickness;
        Point center = new(10 + radiusX, 10 + radiusY);
        double angle = (InitialAngle + 360) % 360;
        long total = _model.Slices.Sum(slice => slice.Value);
        for (int index = 0; index < _model.Slices.Count; index++)
        {
            StatisticsPieSlice slice = _model.Slices[index];
            double sweep = (double)slice.Value / total * 360;
            if (sweep <= 0) continue;
            Geometry top = Top(center, radiusX, radiusY, angle, sweep);
            List<Geometry> sides = [];
            // Only the front half of the outer wall is visible. Wrapped sectors can intersect two copies of that interval.
            for (int turn = -1; turn <= 2; turn++)
            {
                double start = Math.Max(angle, turn * 360), end = Math.Min(angle + sweep, turn * 360 + 180);
                if (end > start) sides.Add(Side(center, radiusX, radiusY, thickness, start, end - start));
            }
            _geometry.Add((index, top, sides));
            angle += sweep;
        }
        foreach (var item in _geometry)
        {
            Color color = SurfaceColor(item.Index);
            Pen edge = new(new SolidColorBrush(StatisticsChartColors.CorrectLightness(color, -0.3)), 1);
            LinearGradientBrush shade = new()
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new(StatisticsChartColors.CorrectLightness(color, -0.15), 0), new(color, 0.1),
                    new(StatisticsChartColors.CorrectLightness(color, -0.3), 1)
                }
            };
            foreach (Geometry side in item.Sides) context.DrawGeometry(shade, edge, side);
        }
        foreach (var item in _geometry)
        {
            Color color = SurfaceColor(item.Index);
            context.DrawGeometry(new SolidColorBrush(color), new Pen(new SolidColorBrush(StatisticsChartColors.CorrectLightness(color, -0.3)), 1), item.Top);
        }
    }

    private Color SurfaceColor(int index)
    {
        Color color = Color.Parse(_model!.Slices[index].Color);
        // FormGitStatistics adapts its palette, and PieChart3D adapts each slice again; retain both transformations.
        color = StatisticsChartColors.Adapt(StatisticsChartColors.Adapt(color, this), this);
        return index == _highlighted ? StatisticsChartColors.CorrectLightness(color, 0.3) : color;
    }
    private void Highlight(Point point)
    {
        int index = SliceAt(point);
        if (_highlighted == index) return;
        _highlighted = index;
        ToolTip.SetTip(this, index >= 0 && _model is not null ? ToolTipFormatter?.Invoke(_model.Slices[index])
            ?? $"{_model.Slices[index].Value:N0} {_model.Slices[index].Label} ({_model.Slices[index].Fraction:P1})" : null);
        InvalidateVisual();
    }
    private static Geometry Top(Point center, double rx, double ry, double start, double sweep)
    {
        StreamGeometry geometry = new();
        using StreamGeometryContext path = geometry.Open();
        path.BeginFigure(center, true);
        path.LineTo(OnEllipse(center, rx, ry, start));
        Arc(path, center, rx, ry, start, sweep);
        path.EndFigure(true);
        return geometry;
    }
    private static Geometry Side(Point center, double rx, double ry, double height, double start, double sweep)
    {
        StreamGeometry geometry = new();
        using StreamGeometryContext path = geometry.Open();
        path.BeginFigure(OnEllipse(center, rx, ry, start), true);
        Arc(path, center, rx, ry, start, sweep);
        Point lower = new(center.X, center.Y + height);
        path.LineTo(OnEllipse(lower, rx, ry, start + sweep));
        Arc(path, lower, rx, ry, start + sweep, -sweep);
        path.EndFigure(true);
        return geometry;
    }
    private static Point OnEllipse(Point center, double rx, double ry, double degrees)
        => new(center.X + rx * Math.Cos(degrees * Math.PI / 180), center.Y + ry * Math.Sin(degrees * Math.PI / 180));
    private static void Arc(StreamGeometryContext path, Point center, double rx, double ry, double start, double sweep)
    {
        int sections = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / 90));
        double step = sweep / sections * Math.PI / 180, angle = start * Math.PI / 180;
        for (int section = 0; section < sections; section++)
        {
            double end = angle + step, k = 4d / 3 * Math.Tan(step / 4);
            Point first = new(center.X + rx * (Math.Cos(angle) - k * Math.Sin(angle)), center.Y + ry * (Math.Sin(angle) + k * Math.Cos(angle)));
            Point second = new(center.X + rx * (Math.Cos(end) + k * Math.Sin(end)), center.Y + ry * (Math.Sin(end) - k * Math.Cos(end)));
            path.CubicBezierTo(first, second, new Point(center.X + rx * Math.Cos(end), center.Y + ry * Math.Sin(end)));
            angle = end;
        }
    }
}
