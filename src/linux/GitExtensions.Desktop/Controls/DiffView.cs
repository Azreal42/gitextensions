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
using AvaloniaEdit.Document;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;

namespace GitExtensions.Desktop.Controls;

public enum DiffViewMode { Auto, Unified, Combined, RangeDiff, PlainText, Difftastic }

public sealed class DiffView : UserControl, IDisposable
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
    private GitDiffDocument? _document;
    private GitDifftasticText? _difftastic;
    private GitDiffColorPalette _colorPalette = null!;
    private IReadOnlyList<GitDiffColorSpan> _ansiSpans = [];
    private IReadOnlyList<GitDiffColorSpan> _inlineSpans = [];
    private bool _settingDocument;
    private bool _equivalentDisplay = true;
    private bool _difftasticAvailable;
    private bool _disposed;
    private bool UsesGitColoring => _document?.UseGitColoring == true || _ansiSpans.Count > 0
        || _document?.EffectiveAppearance is GitDiffAppearance.GitWordDiff or GitDiffAppearance.Difftastic;
    private DiffViewerPreferences _preferences;
    public DiffViewerAppearanceMenu AppearanceMenu { get; }
    public event EventHandler? DisplayOptionsChanged;

    public DiffView(TextBox source, DiffViewerPreferencesStore? preferencesStore = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        AppearanceMenu = new(preferencesStore);
        _preferences = AppearanceMenu.Preferences;
        AppearanceMenu.Changed += PreferencesChanged;
        AppearanceMenu.Attach(this);
        ContextMenu = new ContextMenu { ItemsSource = AppearanceMenu.CreateContextMenuItems() };
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
        _editor.TextArea.TextView.LineTransformers.Add(new DiffForeground(this));
        _editor.TextArea.SelectionChanged += SynchronizeSelection;
        _editor.TextArea.Caret.PositionChanged += SynchronizeSelection;
        _source.PropertyChanged += SourceChanged;
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.KeyModifiers != KeyModifiers.Alt || args.Key is not (Key.Down or Key.Up)) { return; }
            if (args.Key == Key.Down) { GoToNextChange(); } else { GoToPreviousChange(); }
            args.Handled = true;
        }, RoutingStrategies.Tunnel);
        ActualThemeVariantChanged += ThemeChanged;
        Content = _editor;
        ApplyPalette();
        UpdateText();
    }

    public string Text => _editor.Text;
    public string CanonicalText => _source.Text ?? "";
    public DiffViewMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public DiffViewMode EffectiveMode { get; private set; } = DiffViewMode.Unified;
    public bool CanStagePatch => EffectiveMode == DiffViewMode.Unified && _isPatch && _equivalentDisplay
        && (_document is null || _document.CanStageEquivalent && _document.EffectiveAppearance == GitDiffAppearance.Patch);
    public GitDiffDisplayOptions DisplayOptions => new(_preferences.UseGitColoring, _preferences.ReverseGitColoring,
        (GitDiffAppearance)_preferences.Appearance, ViewerWidth: (int)Math.Max(0, Bounds.Width));
    internal IReadOnlyList<GitDiffColorSpan> AnsiSpans => _ansiSpans;
    internal IReadOnlyList<GitDiffColorSpan> InlineSpans => _inlineSpans;
    internal int RightColumnStart => _difftastic?.RightColumnStart ?? 0;

    public async Task ConfigureRepositoryAsync(GitRepository repository, CancellationToken token = default)
    {
        _difftasticAvailable = await repository.IsDifftasticAvailableAsync(token);
        UpdateAppearanceContext();
    }

    public void ShowDocument(GitDiffDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _settingDocument = true;
        try { _source.Text = document.Patch; }
        finally { _settingDocument = false; }
        UpdateText();
    }
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
        if (EffectiveMode == DiffViewMode.Difftastic) { return (line.Old, line.New); }
        return _isPatch ? (line.Old, line.New) : (null, lineNumber);
    }
    internal IReadOnlyList<int?> GetColumnNumbers(int lineNumber)
    {
        DiffLine line = _lines[lineNumber - 1];
        if (EffectiveMode == DiffViewMode.Difftastic) { return [line.Old, line.New]; }
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
            if (Mode == DiffViewMode.PlainText) { _document = null; UpdateText(); }
            ParseLines();
            BuildInlineSpans();
            UpdateAppearanceContext();
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
        if (args.Property == TextBox.TextProperty && !_settingDocument) { _document = null; UpdateText(); }
    }

    private void UpdateText()
    {
        string canonical = CanonicalText;
        bool parseAnsi = Mode != DiffViewMode.PlainText && _document is not null
            && (_document.UseGitColoring || _document.Display != _document.Patch || _document.EffectiveAppearance != GitDiffAppearance.Patch);
        GitColoredText colored = parseAnsi ? GitAnsiColorParser.Parse(_document!.Display, _colorPalette,
            themeColors: _document.EffectiveAppearance == GitDiffAppearance.Difftastic && _preferences.ReverseGitColoring) : new(canonical, []);
        _difftastic = _document?.EffectiveAppearance == GitDiffAppearance.Difftastic && Mode != DiffViewMode.PlainText
            ? GitDifftasticFormatter.Format(colored, _document.DifftasticWidth > 0 ? _document.DifftasticWidth : 80, _preferences.ReverseGitColoring) : null;
        if (_difftastic is not null) { colored = _difftastic.Content; }
        _ansiSpans = colored.Spans;
        _equivalentDisplay = colored.Text == canonical;
        _updatingText = true;
        try
        {
            _editor.Text = colored.Text;
            ParseLines();
            BuildInlineSpans();
            UpdateAppearanceContext();
            _editor.Select(0, 0);
            _editor.ScrollToHome();
            _numbers.InvalidateMeasure();
            _numbers.InvalidateVisual();
            _editor.TextArea.TextView.Redraw();
        }
        finally { _updatingText = false; }
        SynchronizeSelection(this, EventArgs.Empty);
    }

    private void SynchronizeSelection(object? sender, EventArgs args)
    {
        if (_updatingText) { return; }
        _source.CaretIndex = _equivalentDisplay ? CaretOffset : 0;
        _source.SelectionStart = _equivalentDisplay ? SelectionStart : 0;
        _source.SelectionEnd = _equivalentDisplay ? SelectionStart + SelectionLength : 0;
        _numbers.InvalidateVisual();
    }

    private void ParseLines()
    {
        _lines.Clear();
        _changeOffsets.Clear();
        _isPatch = false;
        _numberColumns = 1;
        if (_difftastic is not null)
        {
            EffectiveMode = DiffViewMode.Difftastic;
            _numberColumns = 2;
            _numbers.IsVisible = true;
            foreach (GitDifftasticLine line in _difftastic.Lines)
            {
                _lines.Add(new(line.Kind switch
                {
                    GitDiffLineKind.Added => DiffLineKind.Added, GitDiffLineKind.Removed => DiffLineKind.Removed,
                    GitDiffLineKind.Header => DiffLineKind.Header, _ => DiffLineKind.Context
                }, line.OldLine, line.NewLine));
                if (line.IsChanged) { _changeOffsets.Add(line.Offset); }
            }
            while (_lines.Count < _editor.Document.LineCount) { _lines.Add(new(DiffLineKind.Plain)); }
            return;
        }
        if (Mode == DiffViewMode.PlainText)
        {
            EffectiveMode = DiffViewMode.PlainText;
            _numbers.IsVisible = true;
            foreach (AvaloniaEdit.Document.DocumentLine line in _editor.Document.Lines) { _lines.Add(new(DiffLineKind.Plain)); }
            return;
        }
        _numbers.IsVisible = true;
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
            else if (inHunk && _document?.EffectiveAppearance == GitDiffAppearance.GitWordDiff)
            {
                GitDiffColorSpan[] spans = SpansForLine(line).ToArray();
                bool Exclusive(int slot) => spans.Length == 1 && spans[0].Offset <= line.Offset + text.Length - text.TrimStart().Length
                    && spans[0].Offset + spans[0].Length >= line.EndOffset - 3 && ColorMatches(spans[0], slot);
                if (Exclusive(1)) { _lines.Add(new(DiffLineKind.Removed, oldLine++)); }
                else if (Exclusive(2)) { _lines.Add(new(DiffLineKind.Added, New: newLine++)); }
                else { _lines.Add(new(DiffLineKind.Context, oldLine++, newLine++)); }
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
        _colorPalette = DiffViewerColors.ForTheme(ActualThemeVariant == ThemeVariant.Dark);
        _palette = ActualThemeVariant == ThemeVariant.Dark ? DiffPalette.Dark : DiffPalette.Light;
        _editor.Background = _palette.Background;
        _editor.Foreground = _palette.Foreground;
        _editor.TextArea.SelectionBrush = _palette.Selection;
        _editor.TextArea.SelectionForeground = _palette.Foreground;
        _editor.TextArea.CaretBrush = _palette.Foreground;
        _numbers.InvalidateVisual();
        _editor.TextArea.TextView.Redraw();
    }

    private void ThemeChanged(object? sender, EventArgs args)
    {
        int start = SelectionStart, length = SelectionLength;
        ApplyPalette();
        UpdateText();
        _editor.Select(Math.Min(start, Text.Length), Math.Min(length, Text.Length - Math.Min(start, Text.Length)));
    }

    private void PreferencesChanged(object? sender, EventArgs args)
    {
        DiffViewerPreferences previous = _preferences;
        _preferences = AppearanceMenu.Preferences;
        BuildInlineSpans();
        _editor.TextArea.TextView.Redraw();
        if (previous.UseGitColoring != _preferences.UseGitColoring || previous.ReverseGitColoring != _preferences.ReverseGitColoring
            || previous.Appearance != _preferences.Appearance) { DisplayOptionsChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void UpdateAppearanceContext() => AppearanceMenu.SetContext(Mode != DiffViewMode.PlainText
        && EffectiveMode is not (DiffViewMode.Combined or DiffViewMode.RangeDiff)
        && (_isPatch || _document?.EffectiveAppearance == GitDiffAppearance.Difftastic), _difftasticAvailable);

    private IEnumerable<GitDiffColorSpan> SpansForLine(DocumentLine line)
        => _ansiSpans.Where(span => span.Offset < line.EndOffset && span.Offset + span.Length > line.Offset);
    private bool ColorMatches(GitDiffColorSpan span, int slot) => _preferences.ReverseGitColoring
        ? span.Background == _colorPalette.Backgrounds[slot] : span.Foreground == _colorPalette.Foregrounds[slot];

    private void BuildInlineSpans()
    {
        if (Mode == DiffViewMode.PlainText || _document?.EffectiveAppearance is GitDiffAppearance.GitWordDiff or GitDiffAppearance.Difftastic)
        { _inlineSpans = []; return; }
        GitDiffLineDescriptor[] descriptors = _editor.Document.Lines.Select((line, index) =>
        {
            DiffLine parsed = _lines[index];
            GitDiffLineKind kind = parsed.Kind switch
            {
                DiffLineKind.Added or DiffLineKind.AddedStrong => GitDiffLineKind.Added,
                DiffLineKind.Removed or DiffLineKind.RemovedStrong => GitDiffLineKind.Removed,
                DiffLineKind.Context => GitDiffLineKind.Context, _ => GitDiffLineKind.Header
            };
            GitDiffColorSpan[] colors = SpansForLine(line).ToArray();
            int slot = kind == GitDiffLineKind.Removed ? 1 : 2;
            bool moved = kind is GitDiffLineKind.Added or GitDiffLineKind.Removed && colors.Length > 0
                && !ColorMatches(colors[0], slot)
                && (colors.Length <= 1 || !ColorMatches(colors[^1], slot)
                    || Text.AsSpan(colors[^1].Offset, Math.Min(colors[^1].Length, Text.Length - colors[^1].Offset)).Trim().Length == 0)
                && (colors.Length <= 2 || !colors[1..^1].All(span => ColorMatches(span, slot)));
            int prefix = EffectiveMode == DiffViewMode.Combined ? _numberColumns - 1
                : EffectiveMode == DiffViewMode.RangeDiff ? parsed.Kind is DiffLineKind.AddedStrong or DiffLineKind.RemovedStrong ? 5 : 6 : 1;
            return new GitDiffLineDescriptor(line.Offset, line.Length, kind, prefix, moved);
        }).ToArray();
        _inlineSpans = GitDiffInlineHighlighter.Highlight(Text, descriptors, _colorPalette,
            dimBackground: !UsesGitColoring || _preferences.ReverseGitColoring);
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _source.PropertyChanged -= SourceChanged;
        ActualThemeVariantChanged -= ThemeChanged;
        AppearanceMenu.Changed -= PreferencesChanged;
        AppearanceMenu.Dispose();
        _editor.TextArea.SelectionChanged -= SynchronizeSelection;
        _editor.TextArea.Caret.PositionChanged -= SynchronizeSelection;
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

    private IBrush? MarginBackground(DocumentLine line)
    {
        if (!UsesGitColoring) { return LineBackground(_lines[line.LineNumber - 1].Kind); }
        GitDiffColor? color = SpansForLine(line).FirstOrDefault(span => span.Offset <= line.Offset)?.Background;
        return color is GitDiffColor value ? DiffViewerColors.Brush(value) : null;
    }

    private sealed class DiffForeground(DiffView owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (GitDiffColorSpan span in owner._ansiSpans.Concat(owner._inlineSpans))
            {
                int start = Math.Max(line.Offset, span.Offset), end = Math.Min(line.EndOffset, span.Offset + span.Length);
                if (start < end && span.Foreground is GitDiffColor color)
                {
                    ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(DiffViewerColors.Brush(color)));
                }
            }
        }
    }

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
                IBrush? brush = !owner.UsesGitColoring ? owner.LineBackground(owner._lines[index].Kind) : null;
                if (brush is not null)
                {
                    drawingContext.DrawRectangle(brush, null,
                        new Rect(0, line.VisualTop - textView.VerticalOffset, textView.Bounds.Width, line.Height));
                }
            }
            foreach (GitDiffColorSpan span in owner._ansiSpans.Concat(owner._inlineSpans).OrderBy(span => span.IsAnchor))
            {
                if (span.IsAnchor && span.Foreground is GitDiffColor foreground)
                {
                    DocumentLine line = owner._editor.Document.GetLineByOffset(span.Offset);
                    if (textView.GetVisualLine(line.LineNumber) is not VisualLine visual) { continue; }
                    Point top = textView.GetVisualPosition(new TextViewPosition(line.LineNumber, span.Offset - line.Offset + 1), VisualYPosition.TextTop) - textView.ScrollOffset;
                    drawingContext.DrawRectangle(DiffViewerColors.Brush(foreground), null,
                        new Rect(Math.Round(top.X), Math.Round(top.Y), 1, Math.Ceiling(visual.Height)));
                }
                else if (span.Background is GitDiffColor color && span.Length > 0)
                {
                    TextSegment segment = new() { StartOffset = span.Offset, Length = span.Length };
                    foreach (Rect rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    { drawingContext.DrawRectangle(DiffViewerColors.Brush(color), null, rectangle); }
                }
            }
            if (owner.RightColumnStart > 0)
            {
                FormattedText character = new(" ", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(owner._editor.FontFamily), owner._editor.FontSize, owner._palette.Foreground);
                double x = Math.Round(character.WidthIncludingTrailingWhitespace * owner.RightColumnStart - textView.HorizontalOffset) + 0.5;
                drawingContext.DrawLine(new Pen(owner._palette.LineNumbers, 1), new Point(x, 0), new Point(x, textView.Bounds.Height));
            }
        }
    }

    private sealed class DiffNumberMargin(DiffView owner) : AbstractMargin
    {
        private double _columnWidth;
        protected override Size MeasureOverride(Size availableSize)
        {
            int maximum = owner._isPatch || owner.EffectiveMode == DiffViewMode.Difftastic
                ? owner._lines.Max(line => Math.Max(Math.Max(line.Old ?? 0, line.New ?? 0), line.Parents?.Max(number => number ?? 0) ?? 0)) : owner._lines.Count;
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
                IBrush? background = owner.MarginBackground(line.FirstDocumentLine);
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
