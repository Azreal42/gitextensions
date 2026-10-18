using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop.Controls;

internal readonly record struct SpellingRange(int Start, int Length);

/// <summary>The native TextBox keeps editing, selection and undo; this sibling only draws spelling marks.</summary>
internal sealed class SpellingEditorView : Grid, IDisposable
{
    private readonly Marks _marks;
    private TextPresenter? _presenter;
    private bool _disposed;
    internal TextBox Editor { get; }
    internal bool IsComposing => !string.IsNullOrEmpty(_presenter?.PreeditText);
    internal event EventHandler? CompositionChanged;

    internal SpellingEditorView(TextBox editor)
    {
        Editor = editor;
        Name = "SpellingEditorView";
        _marks = new(this) { Name = "SpellingUnderlines", IsHitTestVisible = false, ClipToBounds = true };
        Children.Add(editor);
        Children.Add(_marks);
        LayoutUpdated += UpdatePresenter;
        editor.PropertyChanged += EditorChanged;
    }

    internal void SetRanges(string checkedText, IEnumerable<SpellingRange> ranges)
    {
        _marks.CheckedText = checkedText;
        _marks.Ranges = ranges.ToArray();
        _marks.InvalidateVisual();
    }

    internal void SetIllFormedRanges(string text, bool enabled)
    {
        _marks.IllFormedText = text;
        _marks.IllFormed = enabled ? IllFormedRanges(text).ToArray() : [];
        _marks.MarkBlankSubject = enabled && text.Length > 1 && text[0] is '\r' or '\n';
        _marks.InvalidateVisual();
    }

    // Limits count UTF-16 positions like the original RichTextBox, with actual CR/LF widths retained.
    internal static IEnumerable<SpellingRange> IllFormedRanges(string text)
    {
        int start = 0, line = 0;
        while (start < text.Length)
        {
            int end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n')) { end++; }
            int limit = line switch { 0 => 50, 1 => 0, _ => 72 };
            if (end - start > limit) { yield return new(start + limit, end - start - limit); }
            if (end == text.Length) { yield break; }
            start = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
            line++;
        }
    }

    internal int? GetTextIndexAt(Point editorPoint)
    {
        if (_presenter is null || IsComposing || Editor.TranslatePoint(editorPoint, _presenter) is not Point point)
        { return null; }
        var layout = _presenter.TextLayout;
        if (_presenter.Bounds.Height < layout.Height)
        {
            double offset = _presenter.VerticalAlignment switch
            {
                VerticalAlignment.Center => (_presenter.Bounds.Height - layout.Height) / 2,
                VerticalAlignment.Bottom => _presenter.Bounds.Height - layout.Height,
                _ => 0
            };
            point = new(point.X, point.Y - offset);
        }
        return Math.Clamp(layout.HitTestPoint(point).TextPosition, 0, Editor.Text?.Length ?? 0);
    }

    internal IReadOnlyList<Rect> GetRangeRectangles(SpellingRange range)
    {
        if (_presenter is null || IsComposing || range.Start < 0 || range.Length <= 0
            || range.Start > (Editor.Text?.Length ?? 0) - range.Length
            || _presenter.TranslatePoint(default, _marks) is not Point origin) { return []; }
        var layout = _presenter.TextLayout;
        // Use the same layout/vertical offset as the pinned Avalonia TextPresenter renderer.
        // https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Controls/Presenters/TextPresenter.cs
        if (_presenter.Bounds.Height < layout.Height)
        {
            double offset = _presenter.VerticalAlignment switch
            {
                VerticalAlignment.Center => (_presenter.Bounds.Height - layout.Height) / 2,
                VerticalAlignment.Bottom => _presenter.Bounds.Height - layout.Height,
                _ => 0
            };
            origin = new(origin.X, origin.Y + offset);
        }
        return layout.HitTestTextRange(range.Start, range.Length)
            .Select(rect => rect.Translate(new Vector(origin.X, origin.Y))).ToArray();
    }

    /// <summary>The native presenter supplies the completion anchor, including wrapping and scroll offset.</summary>
    internal Rect? GetCaretRectangle(int index)
    {
        if (_presenter is null || IsComposing || index < 0 || index > (Editor.Text?.Length ?? 0)
            || _presenter.TranslatePoint(default, Editor) is not Point origin) { return null; }
        var layout = _presenter.TextLayout;
        if (_presenter.Bounds.Height < layout.Height)
        {
            origin = new(origin.X, origin.Y + (_presenter.VerticalAlignment switch
            {
                VerticalAlignment.Center => (_presenter.Bounds.Height - layout.Height) / 2,
                VerticalAlignment.Bottom => _presenter.Bounds.Height - layout.Height,
                _ => 0
            }));
        }
        Rect character = layout.HitTestTextPosition(index);
        Rect caret = new(origin.X + character.X, origin.Y + character.Y, 1, character.Height);
        Rect clip = new(Editor.Bounds.Size);
        if (_presenter.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault() is { } viewport
            && viewport.TranslatePoint(default, Editor) is Point start)
        { clip = clip.Intersect(new Rect(start, viewport.Bounds.Size)); }
        return caret.Height > 0 && clip.Intersects(caret) ? caret : null;
    }

    private void UpdatePresenter(object? sender, EventArgs args)
    {
        if (_disposed) { return; }
        TextPresenter? actual = Editor.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (!ReferenceEquals(actual, _presenter))
        {
            if (_presenter is not null) { _presenter.PropertyChanged -= PresenterChanged; }
            _presenter = actual;
            if (_presenter is not null) { _presenter.PropertyChanged += PresenterChanged; }
        }
        // The viewport, wrapping and effective font can change without new spelling work.
        _marks.InvalidateVisual();
    }
    private void PresenterChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        _marks.InvalidateVisual();
        if (args.Property == TextPresenter.PreeditTextProperty) { CompositionChanged?.Invoke(this, EventArgs.Empty); }
    }
    private void EditorChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    { if (args.Property == TextBox.TextProperty) { _marks.InvalidateVisual(); } }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        LayoutUpdated -= UpdatePresenter;
        Editor.PropertyChanged -= EditorChanged;
        if (_presenter is not null) { _presenter.PropertyChanged -= PresenterChanged; }
        _presenter = null;
    }

    private sealed class Marks(SpellingEditorView owner) : Control
    {
        internal string CheckedText = "";
        internal SpellingRange[] Ranges = [];
        internal string IllFormedText = "";
        internal SpellingRange[] IllFormed = [];
        internal bool MarkBlankSubject;
        public override void Render(DrawingContext context)
        {
            if (owner._disposed || owner.IsComposing) { return; }
            Rect clip = new(Bounds.Size);
            if (owner._presenter?.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault() is { } viewport
                && viewport.TranslatePoint(default, this) is Point start)
            { clip = clip.Intersect(new Rect(start, viewport.Bounds.Size)); }
            if (clip.Width <= 0 || clip.Height <= 0) { return; }
            using (context.PushClip(clip))
            {
                if (IllFormedText == (owner.Editor.Text ?? ""))
                {
                    IBrush highlight = new SolidColorBrush(Color.FromArgb(90, 255, 220, 0));
                    foreach (SpellingRange range in IllFormed)
                    foreach (Rect rect in owner.GetRangeRectangles(range))
                    { context.DrawRectangle(highlight, null, rect); }
                    Rect first = MarkBlankSubject ? owner.GetRangeRectangles(new(0, 1)).FirstOrDefault() : default;
                    if (first.Height > 0)
                    { context.DrawRectangle(highlight, null, new Rect(clip.Left, first.Top, clip.Width, first.Height)); }
                }
                if (CheckedText != (owner.Editor.Text ?? "")) { return; }
                Pen pen = new(Brushes.Red, 1);
                foreach (SpellingRange range in Ranges)
                foreach (Rect rect in owner.GetRangeRectangles(range))
                {
                    double y = rect.Bottom - 2;
                    for (double x = rect.Left; x < rect.Right; x += 4)
                    {
                        double middle = Math.Min(x + 2, rect.Right);
                        context.DrawLine(pen, new Point(x, y), new Point(middle, y + 1));
                        if (middle < rect.Right)
                        { context.DrawLine(pen, new Point(middle, y + 1), new Point(Math.Min(x + 4, rect.Right), y)); }
                    }
                }
            }
        }
    }
}
