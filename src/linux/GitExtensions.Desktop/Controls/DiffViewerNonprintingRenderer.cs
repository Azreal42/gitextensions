// Selected whitespace elements adapt AvaloniaEdit's SingleCharacterElementGenerator.
// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
// of the Software, and to permit persons to whom the Software is furnished to
// do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using GitExtensions.Linux.Desktop;
using LogicalDirection = AvaloniaEdit.Document.LogicalDirection;

namespace GitExtensions.Desktop.Controls;

public sealed record DiffViewerNonprintingPalette(IBrush SpaceTab, IBrush EndOfLine);

/// <summary>Original FileViewer whitespace markers, without changing document text or line delimiters.</summary>
public sealed class DiffViewerNonprintingRenderer : IDisposable
{
    private readonly TextEditor _editor;
    private readonly TextEditorOptions _options;
    private readonly EndOfLineGenerator _endOfLine;
    private readonly LongLineEndOfLineTransformer _longLineEndOfLine;
    private readonly LongLineWhitespaceRenderer _longLines;
    private readonly bool _previousSpaces, _previousTabs, _previousEndOfLine;
    private readonly string _previousSpaceGlyph, _previousTabGlyph;
    private readonly IBrush _previousBrush;
    private bool _disposed;

    public DiffViewerNonprintingRenderer(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        _options = editor.Options;
        _previousSpaces = _options.ShowSpaces;
        _previousTabs = _options.ShowTabs;
        _previousEndOfLine = _options.ShowEndOfLine;
        _previousSpaceGlyph = _options.ShowSpacesGlyph;
        _previousTabGlyph = _options.ShowTabsGlyph;
        _previousBrush = editor.TextArea.TextView.NonPrintableCharacterBrush;
        _endOfLine = new(editor);
        _longLineEndOfLine = new(_endOfLine);
        _longLines = new(editor);
        // The built-in marker draws its own pre-shaped TextLine, so changing
        // its outer run's selection foreground cannot recolor the glyph. Take
        // priority only for selected whitespace; all other short-line markers
        // continue through the built-in generator.
        editor.TextArea.TextView.ElementGenerators.Insert(0, _endOfLine);
        editor.TextArea.TextView.LineTransformers.Add(_longLineEndOfLine);
        editor.TextArea.TextView.BackgroundRenderers.Add(_longLines);
    }

    public bool IsEnabled { get; private set; }

    // A bounded diagnostic for the viewport-only fallback, independent of the
    // document length. Short lines continue through the native generators.
    internal int LastViewportCharacterCount => _longLines.LastViewportCharacterCount;
    internal int LastViewportMarkerCount => _longLines.LastViewportMarkerCount;

    /// <summary>
    /// DefaultLight uses the untransformed DefaultHighlightingStrategy colors.
    /// ThemeModule's dark overrides use the system-color HighlightColor constructor
    /// (Adaptable=false), so ThemeBasedHighlighting.Transform leaves ControlDarkDark intact.
    /// </summary>
    public static DiffViewerNonprintingPalette CreateOriginalPalette(bool dark)
    {
        if (dark)
        {
            IBrush systemMarker = Brush.Parse(ClassicStyles.OriginalColors(true)["ControlDarkDark"]);
            return new(systemMarker, systemMarker);
        }
        // externals/ICSharpCode.TextEditor/.../DefaultHighlightingStrategy.cs, environment colors.
        return new(Brush.Parse("#B6B6C0"), Brush.Parse("#CACAD2"));
    }

    public void Apply(bool enabled, IBrush spaceTabBrush, IBrush eolBrush, bool eolAsGlyph = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(spaceTabBrush);
        ArgumentNullException.ThrowIfNull(eolBrush);
        _endOfLine.Enabled = enabled;
        _endOfLine.Brush = eolBrush;
        _endOfLine.AsGlyph = eolAsGlyph;
        _longLines.Enabled = enabled;
        _longLines.Brush = spaceTabBrush;
        _options.ShowSpacesGlyph = "·";
        _options.ShowTabsGlyph = "»";
        _options.ShowSpaces = enabled;
        _options.ShowTabs = enabled;
        // The native EOL run is constructed after all line transformers and uses
        // the space/tab brush. Generate only its zero-document-length replacement
        // to keep the original distinct EOL brush and full editor font size.
        _options.ShowEndOfLine = false;
        _editor.TextArea.TextView.NonPrintableCharacterBrush = spaceTabBrush;
        IsEnabled = enabled;
        _editor.TextArea.TextView.Redraw();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        IsEnabled = false;
        _editor.TextArea.TextView.ElementGenerators.Remove(_endOfLine);
        _editor.TextArea.TextView.LineTransformers.Remove(_longLineEndOfLine);
        _editor.TextArea.TextView.BackgroundRenderers.Remove(_longLines);
        _options.ShowSpaces = _previousSpaces;
        _options.ShowTabs = _previousTabs;
        _options.ShowEndOfLine = _previousEndOfLine;
        _options.ShowSpacesGlyph = _previousSpaceGlyph;
        _options.ShowTabsGlyph = _previousTabGlyph;
        _editor.TextArea.TextView.NonPrintableCharacterBrush = _previousBrush;
        _editor.TextArea.TextView.Redraw();
    }

    private sealed class EndOfLineGenerator(TextEditor editor) : VisualLineElementGenerator
    {
        public bool Enabled { get; set; }
        public bool AsGlyph { get; set; }
        public IBrush Brush { get; set; } = Brushes.Transparent;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            DocumentLine line = CurrentContext.VisualLine.LastDocumentLine;
            if (!Enabled) { return -1; }
            int interested = line.DelimiterLength > 0 && startOffset <= line.EndOffset ? line.EndOffset : -1;
            if (editor.TextArea.SelectionForeground is not null)
            {
                // TextArea.Selection already redraws only the old/new changed
                // selection segments; do not add a full-document redraw hook.
                foreach (var segment in editor.TextArea.Selection.Segments)
                {
                    int start = Math.Max(startOffset, Math.Max(line.Offset, segment.StartOffset));
                    int end = Math.Min(line.EndOffset, segment.EndOffset);
                    if (interested >= 0) { end = Math.Min(end, interested); }
                    for (int offset = start; offset < end; offset++)
                    {
                        if (CurrentContext.Document.GetCharAt(offset) is ' ' or '\t') { interested = offset; break; }
                    }
                }
            }
            return interested;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            DocumentLine line = CurrentContext.VisualLine.LastDocumentLine;
            if (offset == line.EndOffset) { return CreateElement(CurrentContext, line); }
            VisualLineElementTextRunProperties properties = new(CurrentContext.GlobalTextRunProperties);
            properties.SetForegroundBrush(editor.TextArea.SelectionForeground);
            bool tab = CurrentContext.Document.GetCharAt(offset) == '\t';
            TextLine glyph = FormattedTextElement.PrepareText(TextFormatter.Current, tab ? "»" : "·", properties);
            return tab ? new SelectedTabElement(glyph) : new SelectedSpaceElement(glyph);
        }

        public EndOfLineElement CreateElement(ITextRunConstructionContext context, DocumentLine line, bool transformed = false)
        {
            string text = line.DelimiterLength == 2 ? AsGlyph ? "¤" : "\\r\\n"
                : context.Document.GetCharAt(line.EndOffset) == '\r' ? AsGlyph ? "«" : "\\r"
                : AsGlyph ? "¶" : "\\n";
            VisualLineElementTextRunProperties properties = new(context.GlobalTextRunProperties);
            properties.SetForegroundBrush(Brush);
            TextLine marker = FormattedTextElement.PrepareText(TextFormatter.Current, text, properties);
            return new EndOfLineElement(marker, transformed ? properties : null);
        }
    }

    // The native SingleCharacterElementGenerator's elements are internal.
    // These selected variants use its public rendering contract exactly: a
    // space consumes one text position; a tab has a zero-width drawable glyph
    // followed by the real tab run which performs the native indentation.
    private sealed class SelectedSpaceElement(TextLine text) : FormattedTextElement(text, 1)
    {
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
            => mode is CaretPositioningMode.Normal or CaretPositioningMode.EveryCodepoint
                ? base.GetNextCaretPosition(visualColumn, direction, mode) : -1;
        public override bool IsWhitespace(int visualColumn) => true;
    }

    private sealed class SelectedTabElement(TextLine text) : VisualLineElement(2, 1)
    {
        internal readonly TextLine Text = text;
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
            => startVisualColumn == VisualColumn ? new SelectedTabGlyphRun(this, TextRunProperties)
                : startVisualColumn == VisualColumn + 1 ? new TextCharacters("\t".AsMemory(), TextRunProperties)
                : throw new ArgumentOutOfRangeException(nameof(startVisualColumn));
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
            => mode is CaretPositioningMode.Normal or CaretPositioningMode.EveryCodepoint
                ? base.GetNextCaretPosition(visualColumn, direction, mode) : -1;
        public override bool IsWhitespace(int visualColumn) => true;
    }

    private sealed class SelectedTabGlyphRun(SelectedTabElement element, TextRunProperties properties) : DrawableTextRun
    {
        private readonly SelectedTabElement _element = element;
        public override TextRunProperties Properties { get; } = properties;
        public override double Baseline => _element.Text.Baseline;
        public override Size Size => default;
        public override void Draw(DrawingContext drawingContext, Point origin) => _element.Text.Draw(drawingContext, origin);
    }

    private sealed class LongLineEndOfLineTransformer(EndOfLineGenerator generator) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (!generator.Enabled || line.Length <= VisualLine.LENGTH_LIMIT || line.DelimiterLength == 0) { return; }
            // AvaloniaEdit skips *all* generators above LENGTH_LIMIT, but still
            // runs transformers. Append exactly the usual zero-length EOL
            // element so the scroll extent includes its complete label. This
            // changes neither the text nor the native text/tab elements.
            VisualLine visualLine = CurrentContext.VisualLine;
            visualLine.ReplaceElement(visualLine.Elements.Count, 0, generator.CreateElement(CurrentContext, line, transformed: true));
        }
    }

    private sealed class LongLineWhitespaceRenderer(TextEditor editor) : IBackgroundRenderer
    {
        private Typeface _typeface;
        private double _fontSize;
        private IBrush? _cachedBrush;
        private IBrush? _cachedSelectionBrush;
        private CultureInfo? _culture;
        private FormattedText? _space, _tab, _selectedSpace, _selectedTab;

        public KnownLayer Layer => KnownLayer.Text;
        public bool Enabled { get; set; }
        public IBrush Brush { get; set; } = Brushes.Transparent;
        public int LastViewportCharacterCount { get; private set; }
        public int LastViewportMarkerCount { get; private set; }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            LastViewportCharacterCount = 0;
            LastViewportMarkerCount = 0;
            if (!Enabled || !textView.VisualLinesValid || textView.Document is null) { return; }

            EnsureGlyphs();
            double margin = Math.Max(_space!.WidthIncludingTrailingWhitespace, _tab!.WidthIncludingTrailingWhitespace);
            Vector scroll = textView.ScrollOffset;
            using (drawingContext.PushClip(new Rect(textView.Bounds.Size)))
            {
                foreach (VisualLine visualLine in textView.VisualLines)
                {
                    DocumentLine line = visualLine.FirstDocumentLine;
                    if (line.Length <= VisualLine.LENGTH_LIMIT) { continue; }

                    double top = visualLine.VisualTop - scroll.Y;
                    int startColumn = 0;
                    foreach (TextLine textLine in visualLine.TextLines)
                    {
                        double height = Math.Max(textLine.Height, textView.DefaultLineHeight);
                        if (top >= textView.Bounds.Height) { break; }
                        if (top + height > 0 && startColumn < line.Length)
                        {
                            // The skipped native generators leave document
                            // columns identical to UTF-16 text columns. Native
                            // hit testing also accounts for tab stops, shaping,
                            // horizontal scrolling and wrapped text lines.
                            int first = Math.Clamp(HitColumn(textLine, scroll.X - margin) - 1, startColumn, Math.Min(line.Length, startColumn + textLine.Length));
                            int last = Math.Clamp(HitColumn(textLine, scroll.X + textView.Bounds.Width + margin) + 1, first, Math.Min(line.Length, startColumn + textLine.Length));
                            double baseline = top + (height - textLine.Height) / 2 + textLine.Baseline;
                            for (int column = first; column < last; column++)
                            {
                                LastViewportCharacterCount++;
                                char value = textView.Document.GetCharAt(line.Offset + column);
                                FormattedText? glyph = value == ' ' ? _space : value == '\t' ? _tab : null;
                                if (glyph is null) { continue; }
                                int offset = line.Offset + column;
                                if (_selectedSpace is not null && editor.TextArea.Selection.Segments.Any(segment => offset >= segment.StartOffset && offset < segment.EndOffset))
                                { glyph = value == ' ' ? _selectedSpace : _selectedTab!; }
                                double x = textLine.GetDistanceFromCharacterHit(new CharacterHit(column)) - scroll.X;
                                if (x + glyph.WidthIncludingTrailingWhitespace <= 0 || x >= textView.Bounds.Width) { continue; }
                                drawingContext.DrawText(glyph, new Point(x, baseline - glyph.Baseline));
                                LastViewportMarkerCount++;
                            }
                        }
                        top += height;
                        startColumn += textLine.Length;
                    }
                }
            }
        }

        private static int HitColumn(TextLine line, double distance)
        {
            CharacterHit hit = line.GetCharacterHitFromDistance(distance);
            return hit.FirstCharacterIndex + hit.TrailingLength;
        }

        private void EnsureGlyphs()
        {
            Typeface typeface = new(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch);
            CultureInfo culture = CultureInfo.CurrentCulture;
            if (_space is not null && _typeface.Equals(typeface) && _fontSize == editor.FontSize
                && ReferenceEquals(_cachedBrush, Brush) && ReferenceEquals(_cachedSelectionBrush, editor.TextArea.SelectionForeground)
                && Equals(_culture, culture)) { return; }
            _typeface = typeface;
            _fontSize = editor.FontSize;
            _cachedBrush = Brush;
            _cachedSelectionBrush = editor.TextArea.SelectionForeground;
            _culture = culture;
            _space = new("·", culture, FlowDirection.LeftToRight, typeface, _fontSize, Brush);
            _tab = new("»", culture, FlowDirection.LeftToRight, typeface, _fontSize, Brush);
            _selectedSpace = _cachedSelectionBrush is null ? null : new("·", culture, FlowDirection.LeftToRight, typeface, _fontSize, _cachedSelectionBrush);
            _selectedTab = _cachedSelectionBrush is null ? null : new("»", culture, FlowDirection.LeftToRight, typeface, _fontSize, _cachedSelectionBrush);
        }
    }

    private sealed class EndOfLineElement(TextLine text, TextRunProperties? transformedProperties = null) : FormattedTextElement(text, 0)
    {
        // ReplaceElement inserts a transformed element after the native
        // property initialization phase. Supply its prepared properties through
        // the official TextRun factory instead of accessing package internals.
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
            => transformedProperties is null ? base.CreateTextRun(startVisualColumn, context) : new FormattedTextRun(this, transformedProperties);
        // A visual marker has no document position after its anchor. Without
        // these overrides an empty line or End/Right would gain a second stop.
        public override bool HandlesLineBorders => true;
        public override int GetVisualColumn(int relativeTextOffset) => VisualColumn;
        public override int GetRelativeOffset(int visualColumn) => RelativeTextOffset;
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
            => direction == LogicalDirection.Forward ? visualColumn < VisualColumn ? VisualColumn : -1
                : visualColumn > VisualColumn ? VisualColumn : -1;
        public override bool IsWhitespace(int visualColumn) => true;
    }
}
