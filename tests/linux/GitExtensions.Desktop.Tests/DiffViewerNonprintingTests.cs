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
public sealed class DiffViewerNonprintingTests
{
    [AvaloniaTest]
    public void RealTextRunsDistinguishCrLfCrLfSpacesAndTabsWithoutConsumingDelimiters()
    {
        const string text = "a b\tc\rd e\nf g\r\nlast";
        using Fixture fixture = new(text);
        var palette = DiffViewerNonprintingRenderer.CreateOriginalPalette(false);
        fixture.Renderer.Apply(true, palette.SpaceTab, palette.EndOfLine); fixture.Pump();

        Assert.That(Markers(fixture, 1).Select(marker => marker.Text), Is.EqualTo(new[] { "·", "»", "\\r" }));
        Assert.That(Markers(fixture, 2).Select(marker => marker.Text), Is.EqualTo(new[] { "·", "\\n" }));
        Assert.That(Markers(fixture, 3).Select(marker => marker.Text), Is.EqualTo(new[] { "·", "\\r\\n" }));
        Assert.That(Markers(fixture, 4), Is.Empty, "An unterminated EOF must not acquire an EOL marker.");
        foreach (int number in new[] { 1, 2, 3 })
        {
            Marker eol = Markers(fixture, number).Last();
            Assert.That(eol.Element.DocumentLength, Is.Zero);
            Assert.That(eol.Element.RelativeTextOffset, Is.EqualTo(fixture.Editor.Document.GetLineByNumber(number).Length));
            Assert.That(eol.FontSize, Is.EqualTo(fixture.Editor.FontSize), "Original EOL markers use the editor font, without the native EOL's -2 size adjustment.");
        }
        Assert.That(fixture.Editor.Document.GetLineByNumber(3).DelimiterLength, Is.EqualTo(2));
        Assert.That(fixture.Editor.Document.LineCount, Is.EqualTo(4));
        Assert.That(fixture.Editor.Text, Is.EqualTo(text));
        Assert.That(fixture.DocumentChanges, Is.Zero);
    }

    [AvaloniaTest]
    public void OptionalOriginalEolGlyphsAndEmptyLinesHaveOneAnchoredMarkerEach()
    {
        using Fixture fixture = new("\r\n\r\n\r\n");
        var palette = DiffViewerNonprintingRenderer.CreateOriginalPalette(false);
        fixture.Renderer.Apply(true, palette.SpaceTab, palette.EndOfLine, eolAsGlyph: true); fixture.Pump();
        for (int number = 1; number <= 3; number++)
        {
            Marker marker = Markers(fixture, number).Single();
            Assert.That(marker.Text, Is.EqualTo("¤"));
            Assert.That(marker.Element.DocumentLength, Is.Zero);
            Assert.That(marker.Element.VisualColumn, Is.Zero);
        }
        Assert.That(Markers(fixture, 4), Is.Empty);
        fixture.Editor.Text = "a\rb\nc\r\n"; fixture.Pump();
        Assert.That(Enumerable.Range(1, 3).Select(number => Markers(fixture, number).Single().Text), Is.EqualTo(new[] { "«", "¶", "¤" }));
    }

    [AvaloniaTest]
    public void ArrowHomeEndAndTabStopsKeepTheOriginalDocumentPositions()
    {
        const string text = "a\t b\r\n\r\nc\rd\nend";
        using Fixture fixture = new(text);
        int[] expected = [0, 1, 2, 3, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15];
        int[] baseline = Walk(fixture, PhysicalKey.ArrowRight, 0);
        Assert.That(baseline, Is.EqualTo(expected));
        Point tabStop = fixture.Editor.TextArea.TextView.GetVisualPosition(new(fixture.Editor.Document.GetLocation(2)), VisualYPosition.TextTop);
        var palette = DiffViewerNonprintingRenderer.CreateOriginalPalette(false);
        foreach (bool asGlyph in new[] { false, true })
        {
            fixture.Renderer.Apply(true, palette.SpaceTab, palette.EndOfLine, asGlyph); fixture.Pump();
            Assert.That(Walk(fixture, PhysicalKey.ArrowRight, 0), Is.EqualTo(expected), "Right must cross a CRLF once and never stop inside its rendered label.");
            Assert.That(Walk(fixture, PhysicalKey.ArrowLeft, text.Length), Is.EqualTo(expected.Reverse()));
            Point visibleTabStop = fixture.Editor.TextArea.TextView.GetVisualPosition(new(fixture.Editor.Document.GetLocation(2)), VisualYPosition.TextTop);
            Assert.That(visibleTabStop.X, Is.EqualTo(tabStop.X).Within(0.01), "The » overlay must not change tab indentation.");
            fixture.Editor.Select(1, 0); Press(fixture, PhysicalKey.End);
            Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(4));
            Press(fixture, PhysicalKey.Home); Assert.That(fixture.Editor.CaretOffset, Is.Zero);
            fixture.Editor.Select(6, 0); Press(fixture, PhysicalKey.End);
            Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(6), "End on an empty CRLF line stays at its single document anchor.");
            Press(fixture, PhysicalKey.Home); Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(6));
            Press(fixture, PhysicalKey.ArrowRight); Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(8));
            Press(fixture, PhysicalKey.ArrowLeft); Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(6));
        }
        Assert.That(fixture.Editor.Text, Is.EqualTo(text));
        Assert.That(fixture.DocumentChanges, Is.Zero);
    }

    [AvaloniaTest]
    public async Task ToggleAndBothPalettesPreserveUnicodeSelectionDocumentAndActualClipboardBytes()
    {
        const string text = "α 🦊\tvalue\r\n next\nlast\r";
        using Fixture fixture = new(text);
        var document = fixture.Editor.Document;
        fixture.Editor.Select(2, text.Length - 3); fixture.Pump();
        string selected = fixture.Editor.SelectedText;
        int start = fixture.Editor.SelectionStart, length = fixture.Editor.SelectionLength, caret = fixture.Editor.CaretOffset;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var clipboard = fixture.Window.Clipboard!;
        string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            fixture.Editor.Copy();
            string? baselineCopy = await ClipboardExtensions.TryGetTextAsync(clipboard);
            Assert.That(baselineCopy, Is.EqualTo(selected.ReplaceLineEndings(Environment.NewLine)), "The native editor normalizes copied line endings for its platform before any markers are enabled.");
            foreach (bool dark in new[] { false, true })
            {
                fixture.SetTheme(dark);
                var palette = DiffViewerNonprintingRenderer.CreateOriginalPalette(dark);
                foreach (bool enabled in new[] { true, false })
                {
                    fixture.Renderer.Apply(enabled, palette.SpaceTab, palette.EndOfLine); fixture.Pump();
                    Assert.That(fixture.Editor.Document, Is.SameAs(document));
                    Assert.That(Encoding.UTF8.GetBytes(fixture.Editor.Text), Is.EqualTo(bytes));
                    Assert.That(fixture.Editor.SelectionStart, Is.EqualTo(start));
                    Assert.That(fixture.Editor.SelectionLength, Is.EqualTo(length));
                    Assert.That(fixture.Editor.CaretOffset, Is.EqualTo(caret));
                    Assert.That(fixture.Editor.SelectedText, Is.EqualTo(selected));
                    fixture.Editor.Copy();
                    Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo(baselineCopy), "Toggling markers must preserve the native copy operation byte for byte.");
                }
            }
            Assert.That(fixture.DocumentChanges, Is.Zero);
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    [AvaloniaTest]
    public void ActualMarkerTextRunsAndPixelsUseDistinctOriginalLightBrushesAndSystemDarkBrush()
    {
        using Fixture fixture = new("a b\tc\r\n");
        foreach (bool dark in new[] { false, true })
        {
            fixture.SetTheme(dark);
            var palette = DiffViewerNonprintingRenderer.CreateOriginalPalette(dark);
            fixture.Renderer.Apply(true, palette.SpaceTab, palette.EndOfLine); fixture.Pump();
            Marker[] markers = Markers(fixture, 1).ToArray();
            Assert.That(markers.Select(marker => marker.Text), Is.EqualTo(new[] { "·", "»", "\\r\\n" }));
            Color expectedSpace = Color.Parse(dark ? "#5A5A5A" : "#B6B6C0");
            Color expectedEol = Color.Parse(dark ? "#5A5A5A" : "#CACAD2");
            Assert.That(((ISolidColorBrush)markers[0].Brush!).Color, Is.EqualTo(expectedSpace));
            Assert.That(((ISolidColorBrush)markers[1].Brush!).Color, Is.EqualTo(expectedSpace));
            Assert.That(((ISolidColorBrush)markers[2].Brush!).Color, Is.EqualTo(expectedEol));
            using WriteableBitmap frame = fixture.Window.CaptureRenderedFrame() ?? throw new AssertionException("No whitespace renderer frame.");
            for (int index = 0; index < markers.Length; index++)
            {
                Marker marker = markers[index];
                VisualLine line = fixture.Editor.TextArea.TextView.GetOrConstructVisualLine(fixture.Editor.Document.GetLineByNumber(1));
                Point origin = line.GetVisualPosition(marker.Element.VisualColumn, VisualYPosition.TextTop);
                origin = fixture.Editor.TextArea.TextView.TranslatePoint(origin, fixture.Window)!.Value;
                AssertPixels(frame, new(origin, new Size(marker.Line.WidthIncludingTrailingWhitespace + 1, line.Height)), index < 2 ? expectedSpace : expectedEol);
            }
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "diff-nonprinting-" + (dark ? "Dark" : "Light") + ".png");
            frame.Save(path); TestContext.AddTestAttachment(path);
        }
    }

    [AvaloniaTest]
    public void DisableAndDisposeRemoveMarkersAndRestoreOwnedNativeOptions()
    {
        using Fixture fixture = new("a b\tc\r\n");
        string initialSpace = fixture.Editor.Options.ShowSpacesGlyph, initialTab = fixture.Editor.Options.ShowTabsGlyph;
        int count = fixture.Editor.TextArea.TextView.ElementGenerators.Count;
        var palette = DiffViewerNonprintingRenderer.CreateOriginalPalette(false);
        fixture.Renderer.Apply(true, palette.SpaceTab, palette.EndOfLine); fixture.Pump();
        Assert.That(Markers(fixture, 1), Has.Count.EqualTo(3));
        fixture.Renderer.Apply(false, palette.SpaceTab, palette.EndOfLine); fixture.Pump();
        Assert.That(Markers(fixture, 1), Is.Empty);
        fixture.Renderer.Dispose(); fixture.Pump();
        Assert.That(fixture.Editor.Options.ShowSpacesGlyph, Is.EqualTo(initialSpace));
        Assert.That(fixture.Editor.Options.ShowTabsGlyph, Is.EqualTo(initialTab));
        Assert.That(fixture.Editor.TextArea.TextView.ElementGenerators.Count, Is.EqualTo(count - 1));
        Assert.That(fixture.Renderer.IsEnabled, Is.False);
        Assert.Throws<ObjectDisposedException>(() => fixture.Renderer.Apply(true, palette.SpaceTab, palette.EndOfLine));
    }

    private sealed record Marker(string Text, VisualLineElement Element, TextLine Line, IBrush? Brush, double FontSize);

    private static IReadOnlyList<Marker> Markers(Fixture fixture, int number)
    {
        fixture.Pump();
        VisualLine line = fixture.Editor.TextArea.TextView.GetOrConstructVisualLine(fixture.Editor.Document.GetLineByNumber(number));
        List<Marker> result = [];
        foreach (TextRun run in line.TextLines.SelectMany(textLine => textLine.TextRuns))
        {
            VisualLineElement? element = run is FormattedTextRun formatted ? formatted.Element
                : run.GetType().GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(run) as VisualLineElement;
            if (element is null) { continue; }
            // Public TextRuns expose the real drawable markers, but the pinned
            // AvaloniaEdit stores their shaped TextLine internally. Read that
            // line only in the test; assertions inspect rendered runs, not options.
            TextLine? shaped = element is FormattedTextElement
                ? typeof(FormattedTextElement).GetField("TextLine", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(element) as TextLine
                : element.GetType().GetField("Text", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(element) as TextLine;
            if (shaped is null) { continue; }
            TextRun[] characters = shaped.TextRuns.Where(item => !item.Text.IsEmpty).ToArray();
            if (characters.Length == 0) { continue; }
            result.Add(new(string.Concat(characters.Select(item => item.Text.ToString())), element, shaped,
                characters[0].Properties?.ForegroundBrush, characters[0].Properties?.FontRenderingEmSize ?? 0));
        }
        return result;
    }

    private static int[] Walk(Fixture fixture, PhysicalKey direction, int offset)
    {
        fixture.Editor.Select(offset, 0); fixture.Editor.TextArea.Focus(); fixture.Pump();
        List<int> positions = [offset];
        for (int attempts = 0; attempts < fixture.Editor.Text.Length + 5; attempts++)
        {
            Press(fixture, direction);
            if (fixture.Editor.CaretOffset == positions[^1]) { return positions.ToArray(); }
            positions.Add(fixture.Editor.CaretOffset);
        }
        throw new AssertionException("Caret navigation did not reach a document boundary.");
    }

    private static void Press(Fixture fixture, PhysicalKey key)
    {
        fixture.Editor.TextArea.Focus();
        fixture.Window.KeyPressQwerty(key, RawInputModifiers.None);
        fixture.Window.KeyReleaseQwerty(key, RawInputModifiers.None);
        fixture.Pump();
    }

    private static void AssertPixels(WriteableBitmap frame, Rect bounds, Color color)
    {
        using var pixels = frame.Lock();
        byte[] data = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, data, 0, data.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888; int colored = 0;
        for (int y = Math.Max(0, (int)Math.Floor(bounds.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(bounds.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(bounds.Right)); x++)
            {
                int index = y * pixels.RowBytes + x * 4;
                if (Math.Abs(data[index + (bgra ? 2 : 0)] - color.R) < 8 && Math.Abs(data[index + 1] - color.G) < 8
                    && Math.Abs(data[index + (bgra ? 0 : 2)] - color.B) < 8) { colored++; }
            }
        }
        Assert.That(colored, Is.GreaterThan(1), "The actual marker rectangle must contain its original foreground pixels.");
    }

    private sealed class Fixture : IDisposable
    {
        internal TextEditor Editor { get; } = new()
        {
            IsReadOnly = true, FontFamily = new FontFamily("DejaVu Sans Mono"), FontSize = 18,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        internal Window Window { get; }
        internal DiffViewerNonprintingRenderer Renderer { get; }
        internal int DocumentChanges { get; private set; }
        internal Fixture(string text)
        {
            Editor.Styles.Add(new StyleInclude(new Uri("avares://GitExtensions/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
            Editor.Options.EnableHyperlinks = false; Editor.Options.EnableEmailHyperlinks = false;
            Editor.Text = text;
            Editor.Document.Changed += (_, _) => DocumentChanges++;
            Renderer = new(Editor);
            Window = new() { Width = 680, Height = 270, Content = Editor };
            SetTheme(false); Window.Show(); Pump();
        }
        internal void SetTheme(bool dark)
        {
            Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Editor.Background = Brush.Parse(dark ? "#323232" : "#FFFFFF");
            Editor.Foreground = Brush.Parse(dark ? "#F0F0F0" : "#000000");
        }
        internal void Pump() { Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); Editor.TextArea.TextView.EnsureVisualLines(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        public void Dispose() { Renderer.Dispose(); Window.Close(); Dispatcher.UIThread.RunJobs(); }
    }
}
