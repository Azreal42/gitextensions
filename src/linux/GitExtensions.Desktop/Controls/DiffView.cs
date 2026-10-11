using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace GitExtensions.Desktop.Controls;

public sealed class DiffView : UserControl
{
    private static readonly Regex HunkHeader = new(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant);
    private readonly TextBox _source;
    private readonly TextEditor _editor;
    private readonly DiffNumberMargin _numbers;
    private readonly List<DiffLine> _lines = [];
    private DiffPalette _palette = DiffPalette.Light;
    private bool _updatingText;
    private bool _isPatch;

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
        _editor.TextArea.SelectionCornerRadius = 0;
        _numbers = new DiffNumberMargin(this);
        _editor.TextArea.LeftMargins.Add(_numbers);
        _editor.TextArea.TextView.BackgroundRenderers.Add(new DiffBackground(this));
        _editor.TextArea.SelectionChanged += SynchronizeSelection;
        _editor.TextArea.Caret.PositionChanged += SynchronizeSelection;
        _source.PropertyChanged += SourceChanged;
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
        Content = _editor;
        ApplyPalette();
        UpdateText();
    }

    public string Text => _editor.Text;
    public int SelectionStart { get => _editor.SelectionStart; set => _editor.SelectionStart = value; }
    public int SelectionLength { get => _editor.SelectionLength; set => _editor.SelectionLength = value; }
    public int CaretOffset { get => _editor.CaretOffset; set => _editor.Select(value, 0); }
    public void Select(int start, int length) => _editor.Select(start, length);

    internal TextEditor Editor => _editor;
    internal (int? Old, int? New) GetLineNumbers(int lineNumber)
    {
        DiffLine line = _lines[lineNumber - 1];
        return _isPatch ? (line.Old, line.New) : (null, lineNumber);
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
        _isPatch = false;
        bool inFile = false;
        bool inHunk = false;
        int oldLine = 0, newLine = 0;
        foreach (AvaloniaEdit.Document.DocumentLine line in _editor.Document.Lines)
        {
            string text = _editor.Document.GetText(line);
            if (text.StartsWith("diff --", StringComparison.Ordinal))
            {
                _isPatch = inFile = true;
                inHunk = false;
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
        }
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
            int maximum = owner._isPatch
                ? owner._lines.Max(line => Math.Max(line.Old ?? 0, line.New ?? 0))
                : owner._lines.Count;
            int digits = Math.Max(1, maximum.ToString(CultureInfo.InvariantCulture).Length);
            _columnWidth = CreateText(new string('9', digits) + " ").Width;
            return new Size(4 + _columnWidth * (owner._isPatch ? 2 : 1), 0);
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
                (int? oldNumber, int? newNumber) = owner.GetLineNumbers(number);
                double textTop = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - TextView.VerticalOffset;
                bool current = owner.CaretOffset >= line.StartOffset && owner.CaretOffset <= line.LastDocumentLine.EndOffset;
                if (oldNumber is int oldValue) { DrawNumber(oldValue, 4, textTop, current); }
                if (newNumber is int newValue) { DrawNumber(newValue, 4 + (owner._isPatch ? _columnWidth : 0), textTop, current); }
            }

            void DrawNumber(int number, double x, double y, bool current)
                => context.DrawText(CreateText(number.ToString(CultureInfo.InvariantCulture), current), new Point(x, y));
        }

        private FormattedText CreateText(string text, bool current = false)
            => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(owner._editor.FontFamily, weight: current ? FontWeight.Bold : FontWeight.Normal),
                owner._editor.FontSize, current ? owner._palette.Foreground : owner._palette.LineNumbers);
    }

    private enum DiffLineKind { Plain, Context, Header, Added, Removed }
    private sealed record DiffLine(DiffLineKind Kind, int? Old = null, int? New = null);

    // These are the existing GitUI/Themes/invariant.css and dark.css colors.
    private sealed record DiffPalette(IBrush Background, IBrush Foreground, IBrush LineNumbers, IBrush Added, IBrush Removed, IBrush Header, IBrush Selection)
    {
        public static readonly DiffPalette Light = new(Brush.Parse("#ffffff"), Brush.Parse("#000000"), Brush.Parse("#6d6d6d"),
            Brush.Parse("#c8ffc8"), Brush.Parse("#ffc8c8"), Brush.Parse("#e6e6e6"), Brush.Parse("#c3c3ff"));
        public static readonly DiffPalette Dark = new(Brush.Parse("#323232"), Brush.Parse("#f0f0f0"), Brush.Parse("#969696"),
            Brush.Parse("#076641"), Brush.Parse("#6e1919"), Brush.Parse("#5f5e5e"), Brush.Parse("#00009b"));
    }
}
