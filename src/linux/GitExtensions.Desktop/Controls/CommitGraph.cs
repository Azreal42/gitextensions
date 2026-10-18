using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

public sealed class CommitGraph : Control
{
    public const double RowHeight = 24;
    public static readonly StyledProperty<double> LineHeightProperty = AvaloniaProperty.Register<CommitGraph, double>(nameof(LineHeight), RowHeight,
        validate: value => double.IsFinite(value) && value > 0);
    public static readonly StyledProperty<double> LaneWidthProperty = AvaloniaProperty.Register<CommitGraph, double>(nameof(LaneWidth), 16,
        validate: value => double.IsFinite(value) && value > 0);
    public static readonly StyledProperty<bool> ShowReferenceLabelsProperty = AvaloniaProperty.Register<CommitGraph, bool>(nameof(ShowReferenceLabels), true);
    public static readonly StyledProperty<bool> SelectionFocusedProperty = AvaloniaProperty.Register<CommitGraph, bool>(nameof(SelectionFocused), true);
    public static readonly StyledProperty<string?> AuthoredEmailProperty = AvaloniaProperty.Register<CommitGraph, string?>(nameof(AuthoredEmail));
    public static readonly StyledProperty<bool> DrawAlternateBackgroundProperty = AvaloniaProperty.Register<CommitGraph, bool>(nameof(DrawAlternateBackground));
    private double LeftInset => ShowReferenceLabels ? 12 : 2;
    public double LineHeight { get => GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
    public double LaneWidth { get => GetValue(LaneWidthProperty); set => SetValue(LaneWidthProperty, value); }
    public bool ShowReferenceLabels { get => GetValue(ShowReferenceLabelsProperty); set => SetValue(ShowReferenceLabelsProperty, value); }
    public bool SelectionFocused { get => GetValue(SelectionFocusedProperty); set => SetValue(SelectionFocusedProperty, value); }
    public string? AuthoredEmail { get => GetValue(AuthoredEmailProperty); set => SetValue(AuthoredEmailProperty, value); }
    public bool DrawAlternateBackground { get => GetValue(DrawAlternateBackgroundProperty); set => SetValue(DrawAlternateBackgroundProperty, value); }
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
    private bool _onlyFirstParent;
    private RevisionGraphOptions _options = RevisionGraphOptions.Default;
    private IReadOnlyList<GitReference> _references = [];
    private CommitGraphLayout _layout = CommitGraphLayout.Build([]);
    private Dictionary<string, GitReference[]> _refsByHash = new(StringComparer.Ordinal);
    private double _verticalOffset;
    private readonly ResolvedDesktopFont _application = DesktopFonts.For(DesktopFontRole.Application);
    private string? _currentHead;
    private IReadOnlyCollection<string> _selectedHashes = [];
    private HashSet<string> _selectedHashSet = new(StringComparer.Ordinal);
    private IReadOnlySet<string>? _relatedHashes;
    internal IReadOnlySet<string>? RelatedHashes
    {
        get => _relatedHashes;
        set { if (ReferenceEquals(_relatedHashes, value)) { return; } _relatedHashes = value; InvalidateVisual(); }
    }

    public CommitGraph()
    {
        LineHeight = DesktopFontMetrics.GraphRowHeight(DesktopFonts.Active);
        ClipToBounds = true;
        PointerMoved += ShowCommitTooltip;
        PointerExited += (_, _) => ToolTip.SetTip(this, null);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<GitCommit> Commits
    {
        get => _commits;
        set
        {
            _commits = value ?? [];
            _layout = CommitGraphLayout.Build(_commits, _onlyFirstParent, _options);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    internal RevisionGraphOptions Options
    {
        get => _options;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_options == value) { return; }
            _options = value;
            _layout = CommitGraphLayout.Build(_commits, _onlyFirstParent, value);
            InvalidateMeasure(); InvalidateVisual();
        }
    }

    internal bool OnlyFirstParent
    {
        get => _onlyFirstParent;
        set
        {
            if (_onlyFirstParent == value) { return; }
            _onlyFirstParent = value;
            _layout = CommitGraphLayout.Build(_commits, value, _options);
            InvalidateMeasure(); InvalidateVisual();
        }
    }

    internal IReadOnlyList<string> GetChildren(string hash) => _layout.Children.GetValueOrDefault(hash) ?? [];

    public IReadOnlyList<GitReference> References
    {
        get => _references;
        set
        {
            _references = value ?? [];
            _refsByHash = _references.Where(reference => reference.RevisionHash is not null).GroupBy(reference => reference.RevisionHash!, StringComparer.Ordinal)
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

    public IReadOnlyCollection<string> SelectedHashes
    {
        get => _selectedHashes;
        set
        {
            _selectedHashes = value?.ToArray() ?? [];
            _selectedHashSet = new(_selectedHashes, StringComparer.Ordinal);
            InvalidateVisual();
        }
    }

    public double VerticalOffset
    {
        get => _verticalOffset;
        set { _verticalOffset = Math.Max(0, value); if (!ShowReferenceLabels) { InvalidateMeasure(); } InvalidateVisual(); }
    }

    // A non-topological input may be reordered by the upstream layout algorithm.
    public IReadOnlyList<GitCommit> OrderedCommits => _layout.Nodes.Select(node => node.Commit).ToArray();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineHeightProperty || change.Property == LaneWidthProperty || change.Property == ShowReferenceLabelsProperty)
        { InvalidateMeasure(); InvalidateVisual(); }
        if (change.Property == SelectionFocusedProperty || change.Property == AuthoredEmailProperty || change.Property == DrawAlternateBackgroundProperty)
        { InvalidateVisual(); }
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(ShowReferenceLabels
            ? Math.Max(72, LeftInset * 2 + _layout.LaneCount * LaneWidth) + (_references.Count > 0 ? 110 : 0)
            : LeftInset * 2 + VisibleLaneCount(availableSize.Height) * LaneWidth,
            _layout.Nodes.Count * LineHeight);

    private int VisibleLaneCount(double height)
    {
        int first = Math.Min(_layout.Nodes.Count, (int)(_verticalOffset / LineHeight));
        int last = double.IsFinite(height) ? Math.Min(_layout.Nodes.Count - 1, first + (int)Math.Ceiling(height / LineHeight)) : _layout.Nodes.Count - 1;
        int maximum = 0;
        for (int row = first; row <= last; row++) { maximum = Math.Max(maximum, _layout.Nodes[row].Lane); }
        foreach (GraphStroke stroke in _layout.Strokes)
        {
            if (stroke.Row >= first && stroke.Row <= last) { maximum = Math.Max(maximum, Math.Max(stroke.CenterLane, stroke.AdjacentLane)); }
        }
        return maximum + 1;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        bool dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        IBrush[] palette = dark ? DarkPalette : LightPalette;
        int firstRow = Math.Max(0, (int)(_verticalOffset / LineHeight));
        int lastRow = Math.Min(_layout.Nodes.Count - 1, (int)((_verticalOffset + Bounds.Height) / LineHeight));

        // Legacy graph hosts remain transparent unless they opt into revision-grid row state.
        if (DrawAlternateBackground || AuthoredEmail is not null || _selectedHashSet.Count > 0)
        {
            for (int row = firstRow; row <= lastRow; row++)
            {
                GitCommit commit = _layout.Nodes[row].Commit;
                IBrush background = RevisionHistoryRow.BackgroundFor(commit, _selectedHashSet.Contains(commit.Hash), SelectionFocused,
                    AuthoredEmail, row, DrawAlternateBackground, dark);
                context.FillRectangle(background, new Rect(0, row * LineHeight - _verticalOffset, Bounds.Width, LineHeight));
            }
        }

        for (int row = firstRow; row <= lastRow; row++)
        {
            double top = row * LineHeight - _verticalOffset;
            // Windows paints each row into its own clipped cell. Neighbouring
            // centers and tangent decisions are shared, so the curves meet exactly.
            using (context.PushClip(new Rect(0, top, Bounds.Width, LineHeight)))
            {
                foreach (GraphRenderSegment segment in _layout.RenderSegmentsForRow(row, LaneWidth, LineHeight, _relatedHashes is null, _relatedHashes))
                {
                    StreamGeometry geometry = new();
                    using (StreamGeometryContext path = geometry.Open())
                    {
                        Point Translate(Point point) => new(point.X + LeftInset, point.Y + top);
                        foreach (GraphPathPart part in segment.Path)
                        {
                            path.BeginFigure(Translate(part.From), false);
                            if (part.IsBezier) { path.CubicBezierTo(Translate(part.Control1!.Value), Translate(part.Control2!.Value), Translate(part.To)); }
                            else { path.LineTo(Translate(part.To)); }
                            path.EndFigure(false);
                        }
                    }
                    IBrush brush = _relatedHashes is not null && !_relatedHashes.Contains(segment.Child) && !_relatedHashes.Contains(segment.Parent)
                        ? Brushes.Gray : palette[segment.Color];
                    context.DrawGeometry(null, new Pen(brush, 2), geometry);
                }
            }
        }

        // The limited log can end before an edge's ancestor is loaded. Preserve
        // its explicit continuation to the history boundary without inventing a node.
        foreach (GraphStroke stroke in _layout.Strokes.Where(stroke => stroke.ContinuesOutsideHistory && stroke.Row >= firstRow && stroke.Row <= lastRow))
        {
            double centerY = (stroke.Row + .5) * LineHeight - _verticalOffset;
            double boundaryY = centerY + LineHeight / 2;
            double x = X(stroke.CenterLane);
            IBrush brush = _relatedHashes is not null && !_relatedHashes.Contains(stroke.Child) && !_relatedHashes.Contains(stroke.Parent)
                ? Brushes.Gray : palette[stroke.Color];
            context.DrawLine(new Pen(brush, 2), new Point(x, centerY), new Point(x, boundaryY));
            context.DrawEllipse(brush, null, new Point(x, boundaryY - 3), 2, 2);
        }

        for (int row = firstRow; row <= lastRow; row++)
        {
            GraphNode node = _layout.Nodes[row];
            Point center = new(X(node.Lane), (row + 0.5) * LineHeight - _verticalOffset);
            IBrush brush = _relatedHashes is not null && !_relatedHashes.Contains(node.Commit.Hash) ? Brushes.Gray : palette[node.Color];
            _refsByHash.TryGetValue(node.Commit.Hash, out GitReference[]? refs);
            bool isHead = node.Commit.Hash == _currentHead || refs?.Any(reference => reference.IsCurrent) is true;
            Pen? outline = isHead ? new Pen(dark ? Brushes.White : Brushes.Black, 2) : null;

            if (refs is { Length: > 0 })
            {
                context.DrawRectangle(brush, outline, new Rect(center.X - 5, center.Y - 5, 10, 10), 1, 1);
                if (!ShowReferenceLabels) { continue; }
                double x = LeftInset * 2 + _layout.LaneCount * LaneWidth;
                foreach (GitReference reference in refs.OrderByDescending(reference => reference.IsCurrent).ThenBy(reference => reference.Name, StringComparer.Ordinal))
                {
                    Color color = ReferenceColor(reference, dark);
                    IBrush badgeBrush = new SolidColorBrush(color);
                    FormattedText text = new(reference.ShortName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        DesktopFontMetrics.Typeface(_application, reference.IsCurrent), _application.SizeInDips, badgeBrush);
                    double width = Math.Min(105, text.Width + 10);
                    double available = Bounds.Width - x - 2;
                    if (available < 16) { break; }
                    width = Math.Min(width, available);
                    double badgeHeight = Math.Max(16, Math.Ceiling(text.Height) + 2);
                    Rect badge = new(x, center.Y - badgeHeight / 2, width, badgeHeight);
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

    private double X(int lane) => LeftInset + (lane + 0.5) * LaneWidth;

    internal static Color ReferenceColor(GitReference reference, bool dark)
    {
        if (reference.IsRemote && reference.RemoteColor is { Length: > 0 } configured && Color.TryParse(configured, out Color color)) { return color; }
        return Color.Parse(reference.IsBranch ? dark ? "#7FE28A" : "#008000"
            : reference.IsRemote ? dark ? "#fd9797" : "#8b0009"
            : reference.IsTag ? dark ? "#40BAF7" : "#00008b"
            : dark ? "#cfb3b3" : "#808080");
    }

    private void ShowCommitTooltip(object? sender, PointerEventArgs args)
    {
        int row = (int)((args.GetPosition(this).Y + _verticalOffset) / LineHeight);
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
