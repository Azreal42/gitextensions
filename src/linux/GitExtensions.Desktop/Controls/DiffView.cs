using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace GitExtensions.Desktop.Controls;

public enum DiffViewMode { Auto, Unified, Combined, RangeDiff, PlainText }

public sealed class DiffView : UserControl
{
    private static readonly Regex HunkHeader = new(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant);
    private static readonly Regex CombinedHeader = new(@"^(?<ats>@{3,}) (?<parents>(?:-\d+(?:,\d+)? )+)\+(?<new>\d+)(?:,\d+)? \k<ats>(?: |$)", RegexOptions.CultureInvariant);
    private static readonly Regex ParentRange = new(@"-(?<start>\d+)(?:,\d+)?", RegexOptions.CultureInvariant);
    private static readonly Regex RangeHeader = new(@"^\s*(?:\d+|-):\s+(?:[0-9a-f]{4,64}|-+)\s+(?<change>[=<>!])\s+(?:\d+|-):\s+(?:[0-9a-f]{4,64}|-+)(?:\s|$)", RegexOptions.CultureInvariant);
    public static readonly StyledProperty<DiffViewMode> ModeProperty = AvaloniaProperty.Register<DiffView, DiffViewMode>(nameof(Mode), DiffViewMode.Auto);
    private readonly TextBox _source;
    private readonly TextEditor _editor;
    private readonly DiffNumberMargin _numbers;
    private readonly List<DiffLine> _lines = [];
    private DiffPalette _palette = DiffPalette.Light;
    private bool _updatingText;
    private bool _isPatch;
    private int _numberColumns = 1;
    private readonly List<int> _changeOffsets = [];

    public DiffView(TextBox source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        ClipToBounds = true;
        Styles.Add(new StyleInclude(new Uri("avares://GitExtensions/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        });
        _editor = new TextEditor
        {
            Name = "DiffTextEditor",
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 12,
            WordWrap = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        _editor.Options.HighlightCurrentLine = false;
        // The original diff viewer treats URLs and emails as plain, theme-colored text.
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.TextArea.SelectionCornerRadius = 0;
        _numbers = new DiffNumberMargin(this);
        _editor.TextArea.LeftMargins.Add(_numbers);
        _editor.TextArea.TextView.BackgroundRenderers.Add(new DiffBackground(this));
        _editor.TextArea.SelectionChanged += SynchronizeSelection;
        _editor.TextArea.Caret.PositionChanged += SynchronizeSelection;
        _source.PropertyChanged += SourceChanged;
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.KeyModifiers != KeyModifiers.Alt || args.Key is not (Key.Down or Key.Up)) { return; }
            if (args.Key == Key.Down) { GoToNextChange(); } else { GoToPreviousChange(); }
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
        Content = _editor;
        ApplyPalette();
        UpdateText();
    }

    public string Text => _editor.Text;
    public DiffViewMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public DiffViewMode EffectiveMode { get; private set; } = DiffViewMode.Unified;
    public bool CanStagePatch => EffectiveMode == DiffViewMode.Unified && _isPatch;
    public int SelectionStart { get => _editor.SelectionStart; set => _editor.SelectionStart = value; }
    public int SelectionLength { get => _editor.SelectionLength; set => _editor.SelectionLength = value; }
    public int CaretOffset { get => _editor.CaretOffset; set => _editor.Select(value, 0); }
    public void Select(int start, int length) => _editor.Select(start, length);
    public bool GoToNextChange() => NavigateChange(forward: true);
    public bool GoToPreviousChange() => NavigateChange(forward: false);

    internal TextEditor Editor => _editor;
    internal (int? Old, int? New) GetLineNumbers(int lineNumber)
    {
        DiffLine line = _lines[lineNumber - 1];
        return _isPatch ? (line.Old, line.New) : (null, lineNumber);
    }
    internal IReadOnlyList<int?> GetColumnNumbers(int lineNumber)
    {
        DiffLine line = _lines[lineNumber - 1];
        if (!_isPatch) { return [lineNumber]; }
        if (EffectiveMode != DiffViewMode.Combined) { return [line.Old, line.New]; }
        int?[] columns = new int?[_numberColumns];
        if (line.Parents is not null) { Array.Copy(line.Parents, columns, line.Parents.Length); }
        columns[^1] = line.New;
        return columns;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty)
        {
            ParseLines();
            _numbers.InvalidateMeasure();
            _numbers.InvalidateVisual();
            _editor.TextArea.TextView.Redraw();
        }
    }

    private bool NavigateChange(bool forward)
    {
        int? offset = forward ? _changeOffsets.Where(offset => offset > CaretOffset).Cast<int?>().FirstOrDefault()
            : _changeOffsets.Where(offset => offset < CaretOffset).Cast<int?>().LastOrDefault();
        if (offset is null) { return false; }
        Select(offset.Value, 0);
        _editor.ScrollToLine(_editor.Document.GetLineByOffset(offset.Value).LineNumber);
        return true;
    }

    private void SourceChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == TextBox.TextProperty) { UpdateText(); }
    }

    private void UpdateText()
    {
        string text = _source.Text ?? "";
        if (_editor.Text == text && _lines.Count != 0) { return; }
        _updatingText = true;
        try
        {
            _editor.Text = text;
            ParseLines();
            _editor.Select(0, 0);
            _editor.ScrollToHome();
            _numbers.InvalidateMeasure();
            _numbers.InvalidateVisual();
            _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        finally { _updatingText = false; }
        SynchronizeSelection(this, EventArgs.Empty);
    }

    private void SynchronizeSelection(object? sender, EventArgs args)
    {
        if (_updatingText) { return; }
        _source.CaretIndex = CaretOffset;
        _source.SelectionStart = SelectionStart;
        _source.SelectionEnd = SelectionStart + SelectionLength;
        _numbers.InvalidateVisual();
    }

    private void ParseLines()
    {
        _lines.Clear();
        _changeOffsets.Clear();
        _isPatch = false;
        _numberColumns = 1;
        if (Mode == DiffViewMode.PlainText)
        {
            EffectiveMode = DiffViewMode.PlainText;
            foreach (AvaloniaEdit.Document.DocumentLine line in _editor.Document.Lines) { _lines.Add(new(DiffLineKind.Plain)); }
            return;
        }
        string[] textLines = _editor.Document.Lines.Select(line => _editor.Document.GetText(line)).ToArray();
        bool combinedDocument = textLines.Any(line => line.StartsWith("diff --cc ", StringComparison.Ordinal) || line.StartsWith("diff --combined ", StringComparison.Ordinal) || line.StartsWith("@@@", StringComparison.Ordinal));
        EffectiveMode = Mode == DiffViewMode.Auto
            ? combinedDocument ? DiffViewMode.Combined : textLines.Any(line => RangeHeader.IsMatch(line)) ? DiffViewMode.RangeDiff : DiffViewMode.Unified
            : Mode;
        // A combined patch is display-only even if its caller mistakenly requests Unified.
        if (combinedDocument && EffectiveMode == DiffViewMode.Unified) { EffectiveMode = DiffViewMode.Combined; }
        bool previousChange = false;
        bool inFile = false;
        bool inHunk = false;
        int oldLine = 0, newLine = 0;
        int[] parents = [];
        foreach (AvaloniaEdit.Document.DocumentLine line in _editor.Document.Lines)
        {
            string text = _editor.Document.GetText(line);
            if (EffectiveMode == DiffViewMode.RangeDiff)
            {
                bool commitHeader = RangeHeader.IsMatch(text);
                if (commitHeader) { _changeOffsets.Add(line.Offset); }
                DiffLineKind kind = commitHeader || text.StartsWith("    @@", StringComparison.Ordinal) ? DiffLineKind.Header
                    : text.StartsWith("    +", StringComparison.Ordinal) ? DiffLineKind.AddedStrong
                    : text.StartsWith("    -", StringComparison.Ordinal) ? DiffLineKind.RemovedStrong
                    : text.StartsWith("     +", StringComparison.Ordinal) ? DiffLineKind.Added
                    : text.StartsWith("     -", StringComparison.Ordinal) ? DiffLineKind.Removed : DiffLineKind.Context;
                _lines.Add(new(kind));
                continue;
            }
            if (text.StartsWith("diff --", StringComparison.Ordinal))
            {
                _isPatch = inFile = true;
                inHunk = false;
                parents = [];
            }

            Match combined = EffectiveMode == DiffViewMode.Combined ? CombinedHeader.Match(text) : Match.Empty;
            Match[] parentRanges = combined.Success ? ParentRange.Matches(combined.Groups["parents"].Value).Cast<Match>().ToArray() : [];
            if (combined.Success && parentRanges.Length == combined.Groups["ats"].Length - 1
                && parentRanges.All(range => int.TryParse(range.Groups["start"].Value, CultureInfo.InvariantCulture, out _))
                && int.TryParse(combined.Groups["new"].Value, CultureInfo.InvariantCulture, out newLine))
            {
                parents = parentRanges.Select(range => int.Parse(range.Groups["start"].Value, CultureInfo.InvariantCulture)).ToArray();
                _isPatch = inFile = inHunk = true;
                _numberColumns = Math.Max(_numberColumns, parents.Length + 1);
                _lines.Add(new(DiffLineKind.Header)); previousChange = false;
                continue;
            }
            if (EffectiveMode == DiffViewMode.Combined)
            {
                if (text.StartsWith("@@", StringComparison.Ordinal)) { inHunk = false; parents = []; }
                DiffLine parsed = new(inFile && !inHunk || text.StartsWith('@') || text.StartsWith('\\') ? DiffLineKind.Header : DiffLineKind.Plain);
                if (inHunk && parents.Length > 0 && text.Length >= parents.Length)
                {
                    ReadOnlySpan<char> prefix = text.AsSpan(0, parents.Length);
                    bool minus = prefix.Contains('-'), plus = prefix.Contains('+');
                    if ((!minus || !plus) && prefix.IndexOfAnyExcept(' ', '+', '-') < 0)
                    {
                        int?[] numbers = new int?[parents.Length];
                        for (int parent = 0; parent < parents.Length; parent++)
                        {
                            if (minus ? prefix[parent] == '-' : prefix[parent] != '+') { numbers[parent] = parents[parent]++; }
                        }
                        parsed = new(minus ? DiffLineKind.Removed : plus ? DiffLineKind.Added : DiffLineKind.Context, numbers[0], minus ? null : newLine++, numbers);
                    }
                }
                bool change = parsed.Kind is DiffLineKind.Removed or DiffLineKind.Added;
                if (change && !previousChange) { _changeOffsets.Add(line.Offset); }
                previousChange = change;
                _lines.Add(parsed);
                continue;
            }
            Match header = HunkHeader.Match(text);
            if (header.Success
                && int.TryParse(header.Groups["old"].Value, CultureInfo.InvariantCulture, out oldLine)
                && int.TryParse(header.Groups["new"].Value, CultureInfo.InvariantCulture, out newLine))
            {
                _isPatch = inFile = inHunk = true;
                _lines.Add(new(DiffLineKind.Header));
            }
            else if (inHunk && text.StartsWith('-'))
            {
                _lines.Add(new(DiffLineKind.Removed, oldLine++));
            }
            else if (inHunk && text.StartsWith('+'))
            {
                _lines.Add(new(DiffLineKind.Added, New: newLine++));
            }
            else if (inHunk && text.StartsWith(' '))
            {
                _lines.Add(new(DiffLineKind.Context, oldLine++, newLine++));
            }
            else
            {
                _lines.Add(new(inFile && !inHunk ? DiffLineKind.Header : DiffLineKind.Plain));
            }
            bool isChange = _lines[^1].Kind is DiffLineKind.Added or DiffLineKind.Removed;
            if (isChange && !previousChange) { _changeOffsets.Add(line.Offset); }
            previousChange = isChange;
        }
        if (_isPatch && EffectiveMode == DiffViewMode.Unified) { _numberColumns = 2; }
    }

    private void ApplyPalette()
    {
        _palette = ActualThemeVariant == ThemeVariant.Dark ? DiffPalette.Dark : DiffPalette.Light;
        _editor.Background = _palette.Background;
        _editor.Foreground = _palette.Foreground;
        _editor.TextArea.SelectionBrush = _palette.Selection;
        _editor.TextArea.SelectionForeground = _palette.Foreground;
        _editor.TextArea.CaretBrush = _palette.Foreground;
        _numbers.InvalidateVisual();
        _editor.TextArea.TextView.Redraw();
    }

    private IBrush? LineBackground(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => _palette.Added,
        DiffLineKind.Removed => _palette.Removed,
        DiffLineKind.AddedStrong => _palette.AddedStrong,
        DiffLineKind.RemovedStrong => _palette.RemovedStrong,
        DiffLineKind.Header => _palette.Header,
        _ => null
    };

    private sealed class DiffBackground(DiffView owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) { return; }
            foreach (VisualLine line in textView.VisualLines)
            {
                int index = line.FirstDocumentLine.LineNumber - 1;
                if (index >= owner._lines.Count) { continue; }
                IBrush? brush = owner.LineBackground(owner._lines[index].Kind);
                if (brush is not null)
                {
                    drawingContext.DrawRectangle(brush, null,
                        new Rect(0, line.VisualTop - textView.VerticalOffset, textView.Bounds.Width, line.Height));
                }
            }
        }
    }

    private sealed class DiffNumberMargin(DiffView owner) : AbstractMargin
    {
        private double _columnWidth;
        protected override Size MeasureOverride(Size availableSize)
        {
            int maximum = owner._isPatch ? owner._lines.Max(line => Math.Max(Math.Max(line.Old ?? 0, line.New ?? 0), line.Parents?.Max(number => number ?? 0) ?? 0)) : owner._lines.Count;
            int digits = Math.Max(1, maximum.ToString(CultureInfo.InvariantCulture).Length);
            _columnWidth = CreateText(new string('9', digits) + " ").WidthIncludingTrailingWhitespace;
            return new Size(4 + _columnWidth * owner._numberColumns, 0);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.DrawRectangle(owner._palette.Background, null, new Rect(Bounds.Size));
            if (TextView?.VisualLinesValid != true) { return; }
            foreach (VisualLine line in TextView.VisualLines)
            {
                int number = line.FirstDocumentLine.LineNumber;
                if (number > owner._lines.Count) { continue; }
                double top = line.VisualTop - TextView.VerticalOffset;
                IBrush? background = owner.LineBackground(owner._lines[number - 1].Kind);
                if (background is not null) { context.DrawRectangle(background, null, new Rect(0, top, Bounds.Width, line.Height)); }
                double textTop = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - TextView.VerticalOffset;
                bool current = owner.CaretOffset >= line.StartOffset && owner.CaretOffset <= line.LastDocumentLine.EndOffset;
                IReadOnlyList<int?> columns = owner.GetColumnNumbers(number);
                for (int column = 0; column < columns.Count; column++)
                {
                    if (columns[column] is int value) { DrawNumber(value, 4 + _columnWidth * column, textTop, current); }
                }
            }

            void DrawNumber(int number, double x, double y, bool current)
                => context.DrawText(CreateText(number.ToString(CultureInfo.InvariantCulture), current), new Point(x, y));
        }

        private FormattedText CreateText(string text, bool current = false)
            => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(owner._editor.FontFamily, weight: current ? FontWeight.Bold : FontWeight.Normal),
                owner._editor.FontSize, current ? owner._palette.Foreground : owner._palette.LineNumbers);
    }

    private enum DiffLineKind { Plain, Context, Header, Added, Removed, AddedStrong, RemovedStrong }
    private sealed record DiffLine(DiffLineKind Kind, int? Old = null, int? New = null, int?[]? Parents = null);

    // These are the existing GitUI/Themes/invariant.css and dark.css colors.
    private sealed record DiffPalette(IBrush Background, IBrush Foreground, IBrush LineNumbers, IBrush Added, IBrush Removed, IBrush Header, IBrush Selection, IBrush AddedStrong, IBrush RemovedStrong)
    {
        public static readonly DiffPalette Light = new(Brush.Parse("#ffffff"), Brush.Parse("#000000"), Brush.Parse("#6d6d6d"),
            Brush.Parse("#c8ffc8"), Brush.Parse("#ffc8c8"), Brush.Parse("#e6e6e6"), Brush.Parse("#c3c3ff"), Brush.Parse("#87ff87"), Brush.Parse("#ffa5a5"));
        public static readonly DiffPalette Dark = new(Brush.Parse("#323232"), Brush.Parse("#f0f0f0"), Brush.Parse("#969696"),
            Brush.Parse("#076641"), Brush.Parse("#6e1919"), Brush.Parse("#5f5e5e"), Brush.Parse("#00009b"), Brush.Parse("#0d7a4e"), Brush.Parse("#8a2c2c"));
    }
}
