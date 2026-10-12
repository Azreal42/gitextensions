using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using GitExtensions.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DiffViewerNonprintingLongLineTests
{
    [AvaloniaTest]
    public void Length2999KeepsNativeWhitespaceRunsAndCompleteCrLf() => VerifyThreshold(2999);

    [AvaloniaTest]
    public void Length3000KeepsNativeWhitespaceRunsAndCompleteCrLf() => VerifyThreshold(3000);

    [AvaloniaTest]
    public void Length3001UsesViewportFallbackAndCompleteCrLf() => VerifyThreshold(3001);

    [AvaloniaTest]
    public void ShortSelectedWhitespaceUsesSelectionForegroundAndKeepsNativeTabStops()
    {
        const string text = "a \tb\r\nlast";
        using Fixture fixture = new(text);
        double tabStop = Position(fixture, 3).X;
        foreach (bool dark in new[] { false, true })
        {
            fixture.SetTheme(dark); Apply(fixture, enabled: true, dark);
            fixture.Editor.Select(1, 2); fixture.Editor.TextArea.Focus(); fixture.Pump();
            Assert.That(fixture.Editor.SelectedText, Is.EqualTo(" \t"), "Capture must observe the actual native selection.");
            Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(3));
            using (WriteableBitmap selected = Capture(fixture))
            {
                Save(selected, $"short-{(dark ? "Dark" : "Light")}-selection");
                Color color = ((ISolidColorBrush)fixture.Editor.TextArea.SelectionForeground).Color;
                AssertMarkerPixels(fixture, selected, 1, "·", color);
                AssertMarkerPixels(fixture, selected, 2, "»", color);
                Color unselectedColor = Color.Parse(dark ? "#5A5A5A" : "#B6B6C0");
                Assert.That(MarkerPixelCount(fixture, selected, 1, "·", unselectedColor), Is.Zero);
                Assert.That(MarkerPixelCount(fixture, selected, 2, "»", unselectedColor), Is.Zero);
                Assert.That(fixture.Editor.SelectedText, Is.EqualTo(" \t"));
                Assert.That(fixture.Editor.SelectionStart, Is.EqualTo(1));
                Assert.That(fixture.Editor.SelectionLength, Is.EqualTo(2));
                Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(3));
                Assert.That(Position(fixture, 3).X, Is.EqualTo(tabStop).Within(0.01));
            }
            fixture.Editor.Select(3, 0); fixture.Pump();
            using (WriteableBitmap unselected = Capture(fixture))
            {
                Color color = Color.Parse(dark ? "#5A5A5A" : "#B6B6C0");
                AssertMarkerPixels(fixture, unselected, 1, "·", color);
                AssertMarkerPixels(fixture, unselected, 2, "»", color);
                Assert.That(Position(fixture, 3).X, Is.EqualTo(tabStop).Within(0.01));
                Save(unselected, $"short-{(dark ? "Dark" : "Light")}-unselected");
            }
            Press(fixture, PhysicalKey.ArrowLeft); Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(2));
            Press(fixture, PhysicalKey.ArrowRight); Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(3));
            Assert.That(fixture.Editor.Text, Is.EqualTo(text));
            Assert.That(fixture.DocumentChanges, Is.Zero);
            Assert.That(fixture.Renderer.LastViewportCharacterCount, Is.Zero);
        }
    }

    [AvaloniaTest]
    public void HundredThousandCharacterLinePaintsVisibleTabsSpacesAndEolInBothThemes()
    {
        string text = LongText();
        using Fixture fixture = new(text);
        var document = fixture.Editor.Document;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        foreach (bool dark in new[] { false, true })
        {
            fixture.SetTheme(dark);
            Apply(fixture, enabled: true, dark);
            foreach (int offset in new[] { 10, 50_010, 99_994 })
            {
                fixture.ScrollNear(offset);
                using WriteableBitmap frame = Capture(fixture);
                Color color = Color.Parse(dark ? "#5A5A5A" : "#B6B6C0");
                AssertMarkerPixels(fixture, frame, offset, "·", color);
                AssertMarkerPixels(fixture, frame, offset + 1, "»", color);
                Assert.That(fixture.Renderer.LastViewportCharacterCount, Is.InRange(1, 350), "Drawing must scan the visible slice, not all 100000 UTF-16 units.");
                Assert.That(fixture.Renderer.LastViewportMarkerCount, Is.GreaterThanOrEqualTo(2));
                Assert.That(fixture.Editor.Document, Is.SameAs(document));
                Assert.That(Encoding.UTF8.GetBytes(fixture.Editor.Text), Is.EqualTo(bytes));
                Save(frame, $"100000-{(dark ? "Dark" : "Light")}-{offset}");
            }
            fixture.ScrollToEnd();
            AssertCompleteEol(fixture, dark, $"100000-{(dark ? "Dark" : "Light")}-end");

            fixture.Editor.Select(50_010, 2); fixture.Editor.TextArea.Focus(); fixture.Pump(); fixture.ScrollNear(50_010);
            Assert.That(fixture.Editor.SelectedText, Is.EqualTo(" \t"), "Capture must observe the actual native selection.");
            using (WriteableBitmap selected = Capture(fixture))
            {
                Save(selected, $"100000-{(dark ? "Dark" : "Light")}-selection");
                Color selectedForeground = ((ISolidColorBrush)fixture.Editor.TextArea.SelectionForeground).Color;
                AssertMarkerPixels(fixture, selected, 50_010, "·", selectedForeground);
                AssertMarkerPixels(fixture, selected, 50_011, "»", selectedForeground);
                // The exclusive selection end remains in the marker palette.
                AssertMarkerPixels(fixture, selected, 50_012, "·", Color.Parse(dark ? "#5A5A5A" : "#B6B6C0"));
                Assert.That(fixture.Editor.SelectedText, Is.EqualTo(" \t"));
            }
            fixture.Editor.Select(0, 0); fixture.Pump();
        }

        // The glyph cache must follow the actual editor font, including zoom.
        fixture.Editor.FontSize = 24;
        Apply(fixture, enabled: true, dark: true);
        fixture.ScrollNear(50_010);
        using (WriteableBitmap zoomed = Capture(fixture))
        {
            AssertMarkerPixels(fixture, zoomed, 50_010, "·", Color.Parse("#5A5A5A"));
            AssertMarkerPixels(fixture, zoomed, 50_011, "»", Color.Parse("#5A5A5A"));
            Assert.That(fixture.Renderer.LastViewportCharacterCount, Is.InRange(1, 350));
            Save(zoomed, "100000-Dark-zoomed");
        }
        Assert.That(fixture.DocumentChanges, Is.Zero);
    }

    [AvaloniaTest]
    public async Task LongLineTogglesKeepDocumentTabStopsSelectionClipboardAndNativeCaretNavigation()
    {
        string text = LongText();
        using Fixture fixture = new(text);
        var document = fixture.Editor.Document;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        double tabStop = Position(fixture, 50_012).X;
        fixture.Editor.Select(49_998, 18); fixture.Editor.TextArea.Focus(); fixture.Pump();
        int start = fixture.Editor.SelectionStart, length = fixture.Editor.SelectionLength, caret = fixture.Editor.CaretOffset;
        string selected = fixture.Editor.SelectedText;
        var clipboard = fixture.Window.Clipboard!;
        string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            fixture.Editor.Copy();
            string? baseline = await ClipboardExtensions.TryGetTextAsync(clipboard);
            Assert.That(baseline, Is.EqualTo(selected.ReplaceLineEndings(Environment.NewLine)));
            foreach (bool dark in new[] { false, true })
            {
                fixture.SetTheme(dark);
                foreach (bool enabled in new[] { true, false })
                {
                    Apply(fixture, enabled, dark);
                    using WriteableBitmap frame = Capture(fixture);
                    Assert.That(Position(fixture, 50_012).X, Is.EqualTo(tabStop).Within(0.01), "The » overlay must not move the native tab stop.");
                    Assert.That(fixture.Editor.Document, Is.SameAs(document));
                    Assert.That(Encoding.UTF8.GetBytes(fixture.Editor.Text), Is.EqualTo(bytes));
                    Assert.That(fixture.Editor.SelectionStart, Is.EqualTo(start));
                    Assert.That(fixture.Editor.SelectionLength, Is.EqualTo(length));
                    Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(caret));
                    Assert.That(fixture.Editor.SelectedText, Is.EqualTo(selected));
                    fixture.Editor.Copy();
                    Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo(baseline));
                    if (!enabled)
                    {
                        Assert.That(fixture.Renderer.LastViewportCharacterCount, Is.Zero);
                        Assert.That(fixture.Renderer.LastViewportMarkerCount, Is.Zero);
                    }
                }
            }

            fixture.Editor.Select(99_995, 0);
            Apply(fixture, enabled: true, dark: true);
            Press(fixture, PhysicalKey.End);
            Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(100_000));
            fixture.ScrollToEnd();
            AssertCompleteEol(fixture, dark: true, "100000-caret-end");
            Press(fixture, PhysicalKey.ArrowRight);
            Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(100_002), "Right crosses the CRLF once, with no stop inside the label.");
            Press(fixture, PhysicalKey.ArrowLeft);
            Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(100_000));
            Press(fixture, PhysicalKey.ArrowLeft);
            Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(99_999));
            Press(fixture, PhysicalKey.Home);
            Assert.That(fixture.Editor.CaretOffset, Is.Zero);
            Press(fixture, PhysicalKey.End);
            Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(100_000));
            Assert.That(fixture.DocumentChanges, Is.Zero);
            Assert.That(Encoding.UTF8.GetBytes(fixture.Editor.Text), Is.EqualTo(bytes));

            fixture.Renderer.Dispose(); fixture.Pump();
            Assert.That(fixture.Editor.TextArea.TextView.BackgroundRenderers.Count, Is.EqualTo(fixture.NativeBackgroundRendererCount));
            Assert.That(fixture.Editor.TextArea.TextView.LineTransformers.Count, Is.EqualTo(fixture.NativeTransformerCount));
            Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(100_000));
            Assert.That(fixture.Editor.Document, Is.SameAs(document));
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    private static void VerifyThreshold(int length)
    {
        string text = "a \tb" + new string('x', length - 4) + "\r\nlast";
        using Fixture fixture = new(text);
        var document = fixture.Editor.Document;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        foreach (bool dark in new[] { false, true })
        {
            fixture.SetTheme(dark);
            Apply(fixture, enabled: false, dark);
            fixture.ScrollNear(1);
            using WriteableBitmap baseline = Capture(fixture);
            int originalColored = MarkerPixelCount(fixture, baseline, 1, "·", Color.Parse(dark ? "#5A5A5A" : "#B6B6C0"));
            Apply(fixture, enabled: true, dark);
            fixture.ScrollNear(1);
            using (WriteableBitmap visible = Capture(fixture))
            {
                Color expected = Color.Parse(dark ? "#5A5A5A" : "#B6B6C0");
                AssertMarkerPixels(fixture, visible, 1, "·", expected);
                AssertMarkerPixels(fixture, visible, 2, "»", expected);
                Assert.That(MarkerPixelCount(fixture, visible, 1, "·", expected), Is.GreaterThan(originalColored));
                Assert.That(fixture.Renderer.LastViewportCharacterCount, length > 3000 ? Is.InRange(1, 350) : Is.Zero);
                Save(visible, $"{length}-{(dark ? "Dark" : "Light")}-start");
            }
            Assert.That(fixture.Editor.Document.GetLineByNumber(1).Length, Is.EqualTo(length));
            Assert.That(fixture.Editor.Document.GetLineByNumber(1).DelimiterLength, Is.EqualTo(2));
            // All three sizes use the identical zero-length EOL element; only
            // 3001 lacks native generated whitespace elements.
            VisualLine visual = fixture.Editor.TextArea.TextView.VisualLines.First();
            Assert.That(visual.TextLines.SelectMany(line => line.TextRuns).OfType<FormattedTextRun>().Count(run => run.Element.DocumentLength == 1), length > 3000 ? Is.Zero : Is.EqualTo(1));
            fixture.Editor.Select(length, 0); fixture.Pump(); fixture.ScrollToEnd();
            AssertCompleteEol(fixture, dark, $"{length}-{(dark ? "Dark" : "Light")}-end");
            Press(fixture, PhysicalKey.End); Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(length));
            Press(fixture, PhysicalKey.ArrowRight); Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(length + 2));
            Press(fixture, PhysicalKey.ArrowLeft); Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(length));
            Assert.That(fixture.Editor.Document, Is.SameAs(document));
            Assert.That(Encoding.UTF8.GetBytes(fixture.Editor.Text), Is.EqualTo(bytes));
            Assert.That(fixture.DocumentChanges, Is.Zero);
        }
    }

    private static string LongText()
    {
        char[] body = new string('x', 100_000).ToCharArray();
        foreach (int offset in new[] { 10, 50_010, 99_994 })
        { body[offset] = ' '; body[offset + 1] = '\t'; body[offset + 2] = ' '; }
        body[50_000] = 'α'; body[50_001] = '\uD83E'; body[50_002] = '\uDD8A';
        return new string(body) + "\r\nlast";
    }

    private static void Apply(Fixture fixture, bool enabled, bool dark)
    {
        DiffViewerNonprintingPalette palette = DiffViewerNonprintingRenderer.CreateOriginalPalette(dark);
        fixture.Renderer.Apply(enabled, palette.SpaceTab, palette.EndOfLine); fixture.Pump();
    }

    private static Point Position(Fixture fixture, int offset)
        => fixture.Editor.TextArea.TextView.GetVisualPosition(new(fixture.Editor.Document.GetLocation(offset)), VisualYPosition.TextTop);

    private static FormattedText Glyph(Fixture fixture, string text, Color color)
        => new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(fixture.Editor.FontFamily, fixture.Editor.FontStyle, fixture.Editor.FontWeight, fixture.Editor.FontStretch), fixture.Editor.FontSize, new SolidColorBrush(color));

    private static int MarkerPixelCount(Fixture fixture, WriteableBitmap frame, int offset, string text, Color color)
    {
        TextView view = fixture.Editor.TextArea.TextView;
        Point origin = view.TranslatePoint(Position(fixture, offset) - view.ScrollOffset, fixture.Window)!.Value;
        FormattedText glyph = Glyph(fixture, text, color);
        bool selected = fixture.Editor.TextArea.Selection.Segments.Any(segment => segment.StartOffset <= offset && offset < segment.EndOffset);
        Color background = ((ISolidColorBrush)(selected ? fixture.Editor.TextArea.SelectionBrush : fixture.Editor.Background)!).Color;
        return Pixels(frame, new Rect(origin, new Size(glyph.WidthIncludingTrailingWhitespace, view.DefaultLineHeight)), color, background);
    }

    private static void AssertMarkerPixels(Fixture fixture, WriteableBitmap frame, int offset, string text, Color color)
        => Assert.That(MarkerPixelCount(fixture, frame, offset, text, color), Is.GreaterThan(1), $"The actual {text} marker at document offset {offset} must be visible in its original brush.");

    private static void AssertCompleteEol(Fixture fixture, bool dark, string captureName)
    {
        fixture.Pump();
        TextView view = fixture.Editor.TextArea.TextView;
        VisualLine line = view.VisualLines.First(visual => visual.FirstDocumentLine.LineNumber == 1);
        FormattedTextRun run = line.TextLines.SelectMany(textLine => textLine.TextRuns).OfType<FormattedTextRun>().Single(item => item.Element.DocumentLength == 0);
        TextLine shaped = typeof(FormattedTextElement).GetField("TextLine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(run.Element) as TextLine
            ?? throw new AssertionException("No shaped EOL marker.");
        Assert.That(string.Concat(shaped.TextRuns.Where(item => !item.Text.IsEmpty).Select(item => item.Text.ToString())), Is.EqualTo("\\r\\n"));
        Assert.That(run.Element.RelativeTextOffset, Is.EqualTo(line.FirstDocumentLine.Length));
        Assert.That(run.Element.DocumentLength, Is.Zero);
        Assert.That(shaped.TextRuns.First(item => !item.Text.IsEmpty).Properties!.FontRenderingEmSize, Is.EqualTo(fixture.Editor.FontSize));
        Color color = Color.Parse(dark ? "#5A5A5A" : "#CACAD2");
        Point origin = line.GetVisualPosition(run.Element.VisualColumn, VisualYPosition.TextTop) - view.ScrollOffset;
        Assert.That(origin.X, Is.GreaterThanOrEqualTo(0));
        Assert.That(origin.X + shaped.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(view.Bounds.Width + 0.01), "The native scroll extent must expose the whole label at maximum horizontal scroll.");
        using WriteableBitmap frame = Capture(fixture);
        Point windowOrigin = view.TranslatePoint(origin, fixture.Window)!.Value;
        double halfWidth = shaped.WidthIncludingTrailingWhitespace / 2;
        Assert.That(Pixels(frame, new Rect(windowOrigin, new Size(halfWidth, line.Height)), color), Is.GreaterThan(1), "The \\r half must be visible.");
        Assert.That(Pixels(frame, new Rect(windowOrigin + new Vector(halfWidth, 0), new Size(halfWidth, line.Height)), color), Is.GreaterThan(1), "The \\n half must be visible.");
        Save(frame, captureName);
    }

    private static int Pixels(WriteableBitmap frame, Rect bounds, Color color, Color? background = null)
    {
        using var pixels = frame.Lock();
        byte[] data = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, data, 0, data.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888;
        int count = 0;
        for (int y = Math.Max(0, (int)Math.Floor(bounds.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(bounds.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(bounds.Right)); x++)
            {
                int index = y * pixels.RowBytes + x * 4;
                int r = data[index + (bgra ? 2 : 0)], g = data[index + 1], b = data[index + (bgra ? 0 : 2)];
                if (background is Color bg)
                {
                    // A thin diagonal glyph can have no fully opaque pixels.
                    // Require pixels on the expected foreground/background
                    // blend with at least 20% ink, rather than accepting a
                    // neighboring character or demanding a particular phase.
                    double dr = color.R - bg.R, dg = color.G - bg.G, db = color.B - bg.B;
                    double norm = dr * dr + dg * dg + db * db;
                    double alpha = norm == 0 ? 0 : ((r - bg.R) * dr + (g - bg.G) * dg + (b - bg.B) * db) / norm;
                    if (alpha >= .2 && alpha <= 1.02 && Math.Abs(r - (bg.R + alpha * dr)) <= 3
                        && Math.Abs(g - (bg.G + alpha * dg)) <= 3 && Math.Abs(b - (bg.B + alpha * db)) <= 3) { count++; }
                }
                else if (Math.Abs(r - color.R) < 8 && Math.Abs(g - color.G) < 8 && Math.Abs(b - color.B) < 8) { count++; }
            }
        }
        return count;
    }

    private static WriteableBitmap Capture(Fixture fixture)
    {
        fixture.Pump();
        return fixture.Window.CaptureRenderedFrame() ?? throw new AssertionException("No long-line renderer frame.");
    }


    private static void Save(WriteableBitmap frame, string name)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "diff-nonprinting-long-" + name + ".png");
        frame.Save(path); TestContext.AddTestAttachment(path);
    }

    private static void Press(Fixture fixture, PhysicalKey key)
    {
        fixture.Editor.TextArea.Focus();
        fixture.Window.KeyPressQwerty(key, RawInputModifiers.None);
        fixture.Window.KeyReleaseQwerty(key, RawInputModifiers.None);
        fixture.Pump();
    }

    private sealed class Fixture : IDisposable
    {
        internal TextEditor Editor { get; } = new()
        {
            IsReadOnly = true, WordWrap = false, FontFamily = new FontFamily("DejaVu Sans Mono"), FontSize = 18,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        internal Window Window { get; }
        internal DiffViewerNonprintingRenderer Renderer { get; }
        internal int NativeBackgroundRendererCount { get; }
        internal int NativeTransformerCount { get; }
        internal int DocumentChanges { get; private set; }
        internal Fixture(string text)
        {
            Editor.Styles.Add(new StyleInclude(new Uri("avares://GitExtensions/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
            Editor.Options.EnableHyperlinks = false; Editor.Options.EnableEmailHyperlinks = false;
            Editor.Text = text; Editor.Document.Changed += (_, _) => DocumentChanges++;
            NativeBackgroundRendererCount = Editor.TextArea.TextView.BackgroundRenderers.Count;
            NativeTransformerCount = Editor.TextArea.TextView.LineTransformers.Count;
            Renderer = new(Editor);
            Window = new() { Width = 680, Height = 270, Content = Editor };
            SetTheme(false); Window.Show(); Pump();
        }
        internal void SetTheme(bool dark)
        {
            Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Editor.Background = Brush.Parse(dark ? "#323232" : "#FFFFFF");
            Editor.Foreground = Brush.Parse(dark ? "#F0F0F0" : "#000000");
            Editor.TextArea.SelectionForeground = Editor.Foreground;
            Editor.TextArea.SelectionBrush = Brush.Parse(dark ? "#4169A1" : "#B8D6FF");
        }
        internal void ScrollNear(int offset)
        {
            Pump();
            var scroll = (IScrollable)Editor.TextArea.TextView;
            scroll.Offset = new Vector(Math.Clamp(Position(this, offset).X - 80, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)), 0);
            Pump();
        }
        internal void ScrollToEnd()
        {
            Pump();
            var scroll = (IScrollable)Editor.TextArea.TextView;
            scroll.Offset = new Vector(Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width), 0);
            Pump();
        }
        internal void Pump() { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); Editor.TextArea.TextView.EnsureVisualLines(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        public void Dispose() { Renderer.Dispose(); Window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
