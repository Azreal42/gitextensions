using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Localization;

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
    private static readonly IBrush NonRelativeLight = Brush.Parse("#d3d3d3");
    private static readonly IBrush NonRelativeDark = Brush.Parse("#707070");
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private bool _drawNonRelativesGray;
    internal bool DrawNonRelativesGray
    {
        get => _drawNonRelativesGray;
        set { if (_drawNonRelativesGray == value) { return; } _drawNonRelativesGray = value; InvalidateVisual(); }
    }
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
    private IncrementalCommitGraphLayout? _incrementalLayout;
    internal event Action? IncrementalOrderChanged;
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
            _incrementalLayout = null;
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
            if (_incrementalLayout is not null)
            { _incrementalLayout.Reconfigure(_onlyFirstParent, value); IncrementalOrderChanged?.Invoke(); }
            else { _layout = CommitGraphLayout.Build(_commits, _onlyFirstParent, value); }
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
            if (_incrementalLayout is not null)
            { _incrementalLayout.Reconfigure(value, _options); IncrementalOrderChanged?.Invoke(); }
            else { _layout = CommitGraphLayout.Build(_commits, value, _options); }
            InvalidateMeasure(); InvalidateVisual();
        }
    }

    internal IReadOnlyList<string> GetChildren(string hash)
        => _incrementalLayout?.GetChildren(hash) ?? _layout.Children.GetValueOrDefault(hash) ?? [];

    internal void BeginIncrementalData(string? head, bool onlyFirstParent = false, bool includeArtificial = false, IReadOnlyList<object>? retainedItems = null)
    {
        _onlyFirstParent = onlyFirstParent;
        _incrementalLayout = new(onlyFirstParent, _options, head, includeArtificial, retainedItems);
        _commits = _incrementalLayout.Commits;
        CurrentHead = head;
        InvalidateMeasure(); InvalidateVisual();
    }

    internal void AppendCommits(IReadOnlyList<GitCommit> batch)
    {
        if (_incrementalLayout is null) { throw new InvalidOperationException("No revision stream is active."); }
        _incrementalLayout.Append(batch); InvalidateMeasure(); InvalidateVisual();
    }

    internal void CompleteCommits(IReadOnlyList<string>? headAncestors = null)
    {
        if (_incrementalLayout is null) { throw new InvalidOperationException("No revision stream is active."); }
        _incrementalLayout.Complete(headAncestors); InvalidateMeasure(); InvalidateVisual();
    }

    internal bool HasPendingArtificials => _incrementalLayout?.HasPendingArtificials == true;
    internal bool IsRelativeKey(string key) => _relatedHashes?.Contains(key) ?? _incrementalLayout?.IsRelative(key) ?? true;
    internal IReadOnlyList<object> OrderedItems => _incrementalLayout?.OrderedItems ?? _layout.Nodes.Select(node => (object)node.Commit).ToArray();

    private int RenderCount => _incrementalLayout?.RowCount ?? _layout.Nodes.Count;
    private int RenderLaneCount => _incrementalLayout?.VisibleLaneCount ?? _layout.LaneCount;
    private object ItemForRow(int row) => _incrementalLayout is { } incremental ? incremental.OrderedItems[row] : _layout.Nodes[row].Commit;
    private PreparedHistoryGraphNode? NodeForRow(int row) => _incrementalLayout is { } incremental ? incremental.GetPreparedItemNode(row)
        : new(_layout.Nodes[row].Commit, _layout.Nodes[row].Lane, _layout.Nodes[row].Color);
    private IReadOnlyList<GraphStroke> RenderStrokes => _incrementalLayout?.VisibleStrokes ?? _layout.Strokes;
    private IReadOnlyList<GraphRenderSegment> RenderSegmentsForRow(int row)
        => _incrementalLayout is { } incremental
            ? incremental.RenderSegmentsForRow(row, LaneWidth, LineHeight, _relatedHashes is null, _relatedHashes)
            : _layout.RenderSegmentsForRow(row, LaneWidth, LineHeight, _relatedHashes is null, _relatedHashes);

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
    public IReadOnlyList<GitCommit> OrderedCommits => _incrementalLayout?.OrderedCommits ?? _layout.Nodes.Select(node => node.Commit).ToArray();

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
            ? Math.Max(72, LeftInset * 2 + RenderLaneCount * LaneWidth) + (_references.Count > 0 ? 110 : 0)
            : LeftInset * 2 + VisibleLaneCount(availableSize.Height) * LaneWidth,
            RenderCount * LineHeight);

    private int VisibleLaneCount(double height)
    {
        if (_incrementalLayout is not null) { return _incrementalLayout.VisibleLaneCount; }
        int first = Math.Min(RenderCount, (int)(_verticalOffset / LineHeight));
        int last = double.IsFinite(height) ? Math.Min(RenderCount - 1, first + (int)Math.Ceiling(height / LineHeight)) : RenderCount - 1;
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
        // A scene update can visit a hidden/zero-sized control. It must not request streamed lanes.
        if (_incrementalLayout is not null && (!IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0)) { return; }
        base.Render(context);
        bool dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        IBrush[] palette = dark ? DarkPalette : LightPalette;
        int firstRow = Math.Max(0, (int)(_verticalOffset / LineHeight));
        int lastRow = Math.Min(RenderCount - 1, (int)((_verticalOffset + Bounds.Height) / LineHeight));
        _incrementalLayout?.PrepareViewport(firstRow, lastRow);

        // Legacy graph hosts remain transparent unless they opt into revision-grid row state.
        if (DrawAlternateBackground || AuthoredEmail is not null || _selectedHashSet.Count > 0)
        {
            for (int row = firstRow; row <= lastRow; row++)
            {
                object item = ItemForRow(row);
                IBrush background = RevisionHistoryRow.BackgroundFor(item as GitCommit, _selectedHashSet.Contains(HistoryRevisionItems.Key(item)), SelectionFocused,
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
                foreach (GraphRenderSegment segment in RenderSegmentsForRow(row))
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
                    IBrush brush = RenderBrush(segment.Color, segment.Child, segment.Parent, dark, palette);
                    context.DrawGeometry(null, new Pen(brush, 2), geometry);
                }
            }
        }

        // The limited log can end before an edge's ancestor is loaded. Preserve
        // its explicit continuation to the history boundary without inventing a node.
        foreach (GraphStroke stroke in RenderStrokes.Where(stroke => stroke.ContinuesOutsideHistory && stroke.Row >= firstRow && stroke.Row <= lastRow))
        {
            double centerY = (stroke.Row + .5) * LineHeight - _verticalOffset;
            double boundaryY = centerY + LineHeight / 2;
            double x = X(stroke.CenterLane);
            IBrush brush = RenderBrush(stroke.Color, stroke.Child, stroke.Parent, dark, palette);
            context.DrawLine(new Pen(brush, 2), new Point(x, centerY), new Point(x, boundaryY));
            context.DrawEllipse(brush, null, new Point(x, boundaryY - 3), 2, 2);
        }

        for (int row = firstRow; row <= lastRow; row++)
        {
            PreparedHistoryGraphNode? node = NodeForRow(row);
            if (node is null) { continue; }
            Point center = new(X(node.Lane), (row + 0.5) * LineHeight - _verticalOffset);
            string key = HistoryRevisionItems.Key(node.Item);
            IBrush brush = RenderBrush(node.Color, key, null, dark, palette);
            if (node.Item is ArtificialHistoryRevision)
            {
                // No references or HEAD outline: Windows draws the artificial node as a 10px circle.
                context.DrawEllipse(brush, null, center, 5, 5); continue;
            }
            GitCommit commit = (GitCommit)node.Item;
            _refsByHash.TryGetValue(commit.Hash, out GitReference[]? refs);
            bool isHead = commit.Hash == _currentHead || refs?.Any(reference => reference.IsCurrent) is true;
            Pen? outline = isHead ? new Pen(dark ? Brushes.White : Brushes.Black, 2) : null;

            if (refs is { Length: > 0 })
            {
                context.DrawRectangle(brush, outline, new Rect(center.X - 5, center.Y - 5, 10, 10), 1, 1);
                if (!ShowReferenceLabels) { continue; }
                double x = LeftInset * 2 + RenderLaneCount * LaneWidth;
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
                context.DrawEllipse(brush, outline, center, commit.Parents.Count > 1 ? 6 : 4, commit.Parents.Count > 1 ? 6 : 4);
            }
        }
    }

    private IBrush RenderBrush(int color, string child, string? parent, bool dark, IBrush[] palette)
    {
        // Explicit branch/selection highlighting retains the host's existing priority and palette.
        if (_relatedHashes is not null)
        { return !_relatedHashes.Contains(child) && (parent is null || !_relatedHashes.Contains(parent)) ? Brushes.Gray : palette[color]; }
        // Windows styles each segment from its CHILD's native relation flag, not from the parent's visibility.
        if (_incrementalLayout is not null && DrawNonRelativesGray && !_incrementalLayout.IsRelative(child))
        { return dark ? NonRelativeDark : NonRelativeLight; }
        return palette[color];
    }

    private double X(int lane) => LeftInset + (lane + 0.5) * LaneWidth;

    internal static Color ReferenceColor(GitReference? reference, bool dark)
    {
        if (reference is { IsRemote: true, RemoteColor: { Length: > 0 } configured } && Color.TryParse(configured, out Color color)) { return color; }
        return Color.Parse(reference?.IsBranch == true ? dark ? "#7FE28A" : "#008000"
            : reference?.IsRemote == true ? dark ? "#fd9797" : "#8b0009"
            : reference?.IsTag == true ? dark ? "#40BAF7" : "#00008b"
            : dark ? "#cfb3b3" : "#808080");
    }

    private void ShowCommitTooltip(object? sender, PointerEventArgs args)
    {
        int row = (int)((args.GetPosition(this).Y + _verticalOffset) / LineHeight);
        if (row < 0 || row >= RenderCount)
        {
            ToolTip.SetTip(this, null);
            return;
        }

        object item = ItemForRow(row);
        if (item is ArtificialHistoryRevision artificial)
        { ToolTip.SetTip(this, _translations.Translate(artificial.Subject)); return; }
        GitCommit commit = (GitCommit)item;
        string references = _refsByHash.TryGetValue(commit.Hash, out GitReference[]? refs)
            ? string.Join(", ", refs.Select(reference => reference.ShortName)) + "\n" : "";
        ToolTip.SetTip(this, $"{references}{commit.Hash}\n{commit.Subject}\n{string.Join("\n", commit.Parents)}");
    }
}
