using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DiffFormatsTests
{
    private const string Combined = "diff --cc file.txt\nindex aaaa,bbbb..cccc\n--- a/file.txt\n+++ b/file.txt\n@@@ -41,4 -71,4 +101,4 @@@\n  context\n- first parent only\n -second parent only\n--removed from both\n++added λ🙂\n+ kept by second\n +kept by first\n  trailing\n";
    private const string Range = "1:  aaaa111 ! 1:  bbbb111 Subject λ🙂\n    @@ Metadata\n     Author: Tester\n    -old subject\n    +new subject\n    @@ file.txt @@\n     -common deletion\n     +common addition\n    -+old addition\n    ++new addition\n2:  aaaa222 < -:  ------- Removed\n-:  ------- > 2:  bbbb222 Added\n3:  aaaa333 = 3:  bbbb333 Unchanged\n";

    [AvaloniaTest]
    public void CombinedPrefixesCountEachParentIndependentlyAndPreserveExactUnicodeCrLfOffsets()
    {
        TextBox source = new() { Text = Combined.Replace("\n", "\r\n", StringComparison.Ordinal) };
        DiffView view = new(source) { Mode = DiffViewMode.Combined };
        Window window = new() { Width = 740, Height = 280, Content = view };
        try
        {
            window.Show();
            Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.Combined));
            Assert.That(view.CanStagePatch, Is.False);
            Assert.That(view.GetColumnNumbers(6), Is.EqualTo(new int?[] { 41, 71, 101 }));
            Assert.That(view.GetColumnNumbers(7), Is.EqualTo(new int?[] { 42, null, null }));
            Assert.That(view.GetColumnNumbers(8), Is.EqualTo(new int?[] { null, 72, null }));
            Assert.That(view.GetColumnNumbers(9), Is.EqualTo(new int?[] { 43, 73, null }));
            Assert.That(view.GetColumnNumbers(10), Is.EqualTo(new int?[] { null, null, 102 }));
            Assert.That(view.GetColumnNumbers(11), Is.EqualTo(new int?[] { null, 74, 103 }));
            Assert.That(view.GetColumnNumbers(12), Is.EqualTo(new int?[] { 44, null, 104 }));
            Assert.That(view.GetColumnNumbers(13), Is.EqualTo(new int?[] { 45, 75, 105 }));
            int offset = source.Text!.IndexOf("++added λ🙂", StringComparison.Ordinal);
            view.Select(offset, "++added λ🙂\r\n".Length);
            Assert.That(source.SelectionStart, Is.EqualTo(offset));
            Assert.That(source.SelectionEnd, Is.EqualTo(offset + "++added λ🙂\r\n".Length));
            view.Mode = DiffViewMode.Auto;
            Assert.That(view.SelectionStart, Is.EqualTo(offset));
            Assert.That(view.SelectionLength, Is.EqualTo("++added λ🙂\r\n".Length));
            Assert.That(view.Text, Is.EqualTo(source.Text));
            view.Select(0, 0); view.Editor.TextArea.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt); window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
            int firstChange = source.Text.IndexOf("- first parent only", StringComparison.Ordinal);
            Assert.That(view.CaretOffset, Is.EqualTo(firstChange));
            Assert.That(source.CaretIndex, Is.EqualTo(firstChange));
            view.Select(source.Text.Length, 0);
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt); window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt);
            Assert.That(view.CaretOffset, Is.EqualTo(firstChange));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task RealThreeParentMergeDisplaysFourColumnsAndOriginalPaletteInBothThemes()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string[] parents = new string[3];
        for (int parent = 0; parent < 3; parent++)
        {
            parents[parent] = await fixture.CommitTreeAsync($"shared\nparent{parent + 1}\nremoved-all\nshared-end\n", "Parent " + (parent + 1));
        }
        string merge = await fixture.CommitTreeAsync("shared\nresolved λ🙂\nshared-end\n", "Merge three", parents);
        string output = (await fixture.Git("show", "--format=", "--no-color", "--cc", merge)).StandardOutput;
        Assert.That(output, Does.Contain("@@@@ "));
        DiffView view = new(new TextBox { Text = output });
        Window window = new() { Width = 740, Height = 300, Content = view };
        try
        {
            window.Show();
            Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.Combined));
            Assert.That(view.CanStagePatch, Is.False);
            string[] lines = output.Split('\n');
            int shared = Array.IndexOf(lines, "   shared") + 1;
            int resolved = Array.IndexOf(lines, "+++resolved λ🙂") + 1;
            int end = Array.IndexOf(lines, "   shared-end") + 1;
            Assert.That(shared, Is.GreaterThan(0)); Assert.That(resolved, Is.GreaterThan(0)); Assert.That(end, Is.GreaterThan(0));
            Assert.That(view.GetColumnNumbers(shared), Is.EqualTo(new int?[] { 1, 1, 1, 1 }));
            Assert.That(view.GetColumnNumbers(resolved), Is.EqualTo(new int?[] { null, null, null, 2 }));
            Assert.That(view.GetColumnNumbers(end), Is.EqualTo(new int?[] { 4, 4, 4, 3 }));
            for (int parent = 0; parent < 3; parent++)
            {
                int row = Array.FindIndex(lines, line => line.EndsWith("parent" + (parent + 1), StringComparison.Ordinal)) + 1;
                int?[] expected = new int?[4]; expected[parent] = 2;
                Assert.That(row, Is.GreaterThan(0)); Assert.That(view.GetColumnNumbers(row), Is.EqualTo(expected));
            }
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; using WriteableBitmap frame = Capture(window, "diff-combined-three-" + theme + ".png");
                AssertColor(frame, theme == ThemeVariant.Dark ? "#076641" : "#c8ffc8");
                AssertColor(frame, theme == ThemeVariant.Dark ? "#6e1919" : "#ffc8c8");
                AssertNumberGlyphs(frame, window, view, shared, 4, theme == ThemeVariant.Dark ? "#969696" : "#6d6d6d", theme == ThemeVariant.Dark ? "#323232" : "#ffffff");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void CombinedHeadersResetAllCountersAndMalformedHeadersNeverBecomeStageableOrdinaryHunks()
    {
        TextBox source = new() { Text = Combined + "diff --combined other.txt\n@@@ -0,0 -1,1 +200,2 @@@\n+ present in second\n++new\n\\ No newline at end of file\n" };
        DiffView view = new(source) { Mode = DiffViewMode.Unified };
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.Combined));
        Assert.That(view.CanStagePatch, Is.False);
        string[] lines = source.Text!.Split('\n');
        int row = Array.IndexOf(lines, "+ present in second") + 1;
        Assert.That(view.GetColumnNumbers(row), Is.EqualTo(new int?[] { null, 1, 200 }));
        Assert.That(view.GetColumnNumbers(row + 1), Is.EqualTo(new int?[] { null, null, 201 }));
        Assert.That(view.GetColumnNumbers(row + 2), Is.EqualTo(new int?[] { null, null, null }));
        source.Text = "diff --cc file\n@@@ -broken +2 @@@\n++line\n";
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.Combined));
        Assert.That(view.CanStagePatch, Is.False);
        Assert.That(view.GetLineNumbers(3), Is.EqualTo(((int?)null, (int?)null)));
    }

    [AvaloniaTest]
    public async Task RealRangeDiffUsesBufferNumbersAndNavigatesCommitSummariesInsteadOfInnerHunks()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string basis = await fixture.CommitTreeAsync("initial\n", "Basis");
        // Retain enough common patch content for Git's real range matcher, and
        // keep the second patch's context away from the changed first line.
        string oldContents = "initial\n" + string.Concat(Enumerable.Range(0, 20).Select(index => "line" + index + "\n"));
        string newContents = oldContents.Replace("line0\n", "line0 updated\n", StringComparison.Ordinal);
        string oldOne = await fixture.CommitTreeAsync(oldContents, "Add one", basis);
        string oldTwo = await fixture.CommitTreeAsync(oldContents + "common\n", "Add common", oldOne);
        string newOne = await fixture.CommitTreeAsync(newContents, "Add one changed λ🙂", basis);
        string newTwo = await fixture.CommitTreeAsync(newContents + "common\n", "Add common", newOne);
        string output = (await fixture.Git("range-diff", "--no-color", basis + ".." + oldTwo, basis + ".." + newTwo)).StandardOutput;
        Assert.That(output, Does.Contain(" ! ").And.Contain(" = "));
        TextBox source = new() { Text = output.Replace("\n", "\r\n", StringComparison.Ordinal) };
        DiffView view = new(source) { Mode = DiffViewMode.RangeDiff };
        Window window = new() { Width = 900, Height = 400, Content = view };
        try
        {
            window.Show();
            Assert.That(view.CanStagePatch, Is.False);
            for (int line = 1; line <= view.Editor.Document.LineCount; line++) { Assert.That(view.GetColumnNumbers(line), Is.EqualTo(new int?[] { line })); }
            int lastHeader = source.Text!.LastIndexOf("2: ", StringComparison.Ordinal);
            // Find the start of its summary line rather than the second column's "2:".
            lastHeader = source.Text.LastIndexOf('\n', lastHeader) + 1;
            Assert.That(view.GoToNextChange(), Is.True);
            Assert.That(view.CaretOffset, Is.EqualTo(lastHeader));
            Assert.That(source.CaretIndex, Is.EqualTo(lastHeader));
            Assert.That(view.GoToNextChange(), Is.False);
            Assert.That(view.GoToPreviousChange(), Is.True);
            Assert.That(view.CaretOffset, Is.Zero);
            Assert.That(view.GoToPreviousChange(), Is.False);
            Assert.That(source.Text, Is.EqualTo(view.Text));
            view.Editor.TextArea.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt); window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
            Assert.That(view.CaretOffset, Is.EqualTo(lastHeader));
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt); window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt);
            Assert.That(view.CaretOffset, Is.Zero);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void RangeDiffRendersNestedChangesWithOriginalNormalAndBrightColorsAndSingleNumberColumn()
    {
        DiffView view = new(new TextBox { Text = Range }) { Mode = DiffViewMode.RangeDiff };
        Window window = new() { Width = 820, Height = 330, Content = view };
        try
        {
            window.Show();
            Assert.That(view.GetColumnNumbers(8), Is.EqualTo(new int?[] { 8 }));
            int[] headers = [Range.IndexOf("2: ", StringComparison.Ordinal), Range.IndexOf("-: ", Range.IndexOf("\n-: ", StringComparison.Ordinal), StringComparison.Ordinal), Range.IndexOf("3: ", StringComparison.Ordinal)];
            foreach (int header in headers) { Assert.That(view.GoToNextChange(), Is.True); Assert.That(view.CaretOffset, Is.EqualTo(header)); }
            Assert.That(view.GoToNextChange(), Is.False);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; using WriteableBitmap frame = Capture(window, "diff-range-nested-" + theme + ".png");
                foreach (string color in theme == ThemeVariant.Dark ? new[] { "#076641", "#6e1919", "#0d7a4e", "#8a2c2c", "#5f5e5e" } : new[] { "#c8ffc8", "#ffc8c8", "#87ff87", "#ffa5a5", "#e6e6e6" }) { AssertColor(frame, color); }
                AssertNumberGlyphs(frame, window, view, 8, 1, theme == ThemeVariant.Dark ? "#969696" : "#6d6d6d", theme == ThemeVariant.Dark ? "#076641" : "#c8ffc8");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void ModeChangesPreserveSelectionAndAutoRequiresActualRangeHeaders()
    {
        TextBox source = new() { Text = "plain preview\n1: aaaa description only\n+not a patch\n" };
        DiffView view = new(source);
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.Unified));
        Assert.That(view.CanStagePatch, Is.False);
        Assert.That(view.GetColumnNumbers(3), Is.EqualTo(new int?[] { 3 }));
        source.Text = Range;
        Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.RangeDiff));
        view.Select(Range.IndexOf("λ🙂", StringComparison.Ordinal), "λ🙂".Length);
        int start = view.SelectionStart;
        view.Mode = DiffViewMode.RangeDiff;
        Assert.That(view.SelectionStart, Is.EqualTo(start)); Assert.That(view.SelectionLength, Is.EqualTo("λ🙂".Length));
        Assert.That(source.SelectionStart, Is.EqualTo(start)); Assert.That(source.SelectionEnd, Is.EqualTo(start + "λ🙂".Length));
        source.Text = "diff --git a/file b/file\n@@ -1 +1 @@\n-old\n+new\n";
        view.Mode = DiffViewMode.Unified;
        Assert.That(view.CanStagePatch, Is.True);
        Assert.That(view.GetColumnNumbers(3), Is.EqualTo(new int?[] { 1, null }));
    }

    [AvaloniaTest]
    public void PlainTextNeverInterpretsSourceFileMarkersAsDiffsAndKeepsExactSelectionInBothThemes()
    {
        string text = ("Source file λ🙂\n" + Combined + Range + "diff --git a/source b/source\n@@ -1 +1 @@\n-old source\n+new source\n").Replace("\n", "\r\n", StringComparison.Ordinal);
        TextBox source = new() { Text = text };
        DiffView view = new(source) { Mode = DiffViewMode.PlainText };
        Window window = new() { Width = 820, Height = 620, Content = view };
        try
        {
            window.Show();
            Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.PlainText));
            Assert.That(view.CanStagePatch, Is.False);
            for (int line = 1; line <= view.Editor.Document.LineCount; line++) { Assert.That(view.GetColumnNumbers(line), Is.EqualTo(new int?[] { line })); }
            int offset = text.IndexOf("λ🙂", StringComparison.Ordinal);
            view.Select(offset, "λ🙂\r\n".Length);
            Assert.That(view.GoToNextChange(), Is.False);
            Assert.That(view.GoToPreviousChange(), Is.False);
            Assert.That(view.SelectionStart, Is.EqualTo(offset));
            Assert.That(view.SelectionLength, Is.EqualTo("λ🙂\r\n".Length));
            Assert.That(source.SelectionStart, Is.EqualTo(offset));
            Assert.That(source.SelectionEnd, Is.EqualTo(offset + "λ🙂\r\n".Length));
            Assert.That(view.Text, Is.EqualTo(text));
            view.Select(0, 0); view.Editor.TextArea.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt); window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
            Assert.That(view.CaretOffset, Is.Zero);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; using WriteableBitmap frame = Capture(window, "diff-plain-source-" + theme + ".png");
                AssertColor(frame, theme == ThemeVariant.Dark ? "#323232" : "#ffffff");
                using ILockedFramebuffer pixels = frame.Lock();
                foreach (string color in theme == ThemeVariant.Dark ? new[] { "#076641", "#6e1919", "#0d7a4e", "#8a2c2c" } : new[] { "#c8ffc8", "#ffc8c8", "#87ff87", "#ffa5a5" })
                {
                    Assert.That(CountPixels(pixels, new Rect(0, 0, pixels.Size.Width, pixels.Size.Height), Color.Parse(color)), Is.Zero, "Diff-looking file contents must retain the neutral source-view palette.");
                }
            }
        }
        finally { window.Close(); }
    }

    private static WriteableBitmap Capture(Window window, string name)
    {
        window.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new AssertionException("Missing rendered diff.");
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/linux/GitExtensions.Desktop/GitExtensions.Desktop.csproj"))) { root = root.Parent; }
        string directory = Path.Combine(root?.FullName ?? TestContext.CurrentContext.WorkDirectory, "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame.Save(path); TestContext.AddTestAttachment(path); return frame;
    }
    private static void AssertColor(WriteableBitmap bitmap, string hex)
    {
        using ILockedFramebuffer pixels = bitmap.Lock();
        Assert.That(CountPixels(pixels, new Rect(0, 0, pixels.Size.Width, pixels.Size.Height), Color.Parse(hex)), Is.GreaterThan(100), "Original palette " + hex + " must be rendered.");
    }
    private static void AssertNumberGlyphs(WriteableBitmap bitmap, Window window, DiffView view, int number, int columns, string foregroundHex, string backgroundHex)
    {
        var margin = view.Editor.TextArea.LeftMargins.Single(item => item.GetType().Name == "DiffNumberMargin");
        AvaloniaEdit.Rendering.VisualLine line = view.Editor.TextArea.TextView.GetVisualLine(number);
        Point origin = margin.TranslatePoint(default, window)!.Value;
        double width = (margin.Bounds.Width - 4) / columns;
        using ILockedFramebuffer pixels = bitmap.Lock();
        for (int column = 0; column < columns; column++)
        {
            Rect bounds = new(origin.X + 4 + width * column, origin.Y + line.VisualTop - view.Editor.TextArea.TextView.VerticalOffset, width, line.Height);
            (int visible, int beyondForeground) = CountGlyphPixels(pixels, bounds, Color.Parse(foregroundHex), Color.Parse(backgroundHex));
            Assert.That(visible, Is.GreaterThan(8), "Every numbered parent/result column must render its number glyphs in the original palette, including antialiased strokes.");
            Assert.That(beyondForeground, Is.Zero, "Number strokes must not exceed the original foreground contrast.");
        }
    }
    private static (int Visible, int BeyondForeground) CountGlyphPixels(ILockedFramebuffer pixels, Rect rectangle, Color foreground, Color background)
    {
        Assert.That(pixels.Format, Is.EqualTo(PixelFormat.Bgra8888).Or.EqualTo(PixelFormat.Rgba8888));
        byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        int visible = 0, beyondForeground = 0;
        int redIndex = pixels.Format == PixelFormat.Bgra8888 ? 2 : 0, blueIndex = 2 - redIndex;
        double dr = foreground.R - background.R, dg = foreground.G - background.G, db = foreground.B - background.B;
        double magnitude = dr * dr + dg * dg + db * db;
        for (int y = Math.Max(0, (int)Math.Floor(rectangle.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(rectangle.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(rectangle.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(rectangle.Right)); x++)
            {
                int offset = y * pixels.RowBytes + x * 4;
                double r = bytes[offset + redIndex], g = bytes[offset + 1], b = bytes[offset + blueIndex];
                // Rasterized glyphs blend the source CSS foreground with the known row background.
                double coverage = ((r - background.R) * dr + (g - background.G) * dg + (b - background.B) * db) / magnitude;
                if (coverage < .25 || Math.Abs(r - (background.R + coverage * dr)) > 3
                    || Math.Abs(g - (background.G + coverage * dg)) > 3 || Math.Abs(b - (background.B + coverage * db)) > 3) { continue; }
                if (coverage > 1.05) { beyondForeground++; } else { visible++; }
            }
        }
        return (visible, beyondForeground);
    }
    private static int CountPixels(ILockedFramebuffer pixels, Rect rectangle, Color color)
    {
        Assert.That(pixels.Format, Is.EqualTo(PixelFormat.Bgra8888).Or.EqualTo(PixelFormat.Rgba8888));
        byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888; int count = 0;
        for (int y = Math.Max(0, (int)Math.Ceiling(rectangle.Y)); y < Math.Min(pixels.Size.Height, rectangle.Bottom); y++)
        {
            for (int x = Math.Max(0, (int)Math.Ceiling(rectangle.X)); x < Math.Min(pixels.Size.Width, rectangle.Right); x++)
            {
                int offset = y * pixels.RowBytes + x * 4;
                if (bytes[offset + (bgra ? 2 : 0)] == color.R && bytes[offset + 1] == color.G && bytes[offset + (bgra ? 0 : 2)] == color.B && bytes[offset + 3] == 255) { count++; }
            }
        }
        return count;
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-diff-formats-" + Guid.NewGuid().ToString("N"));
        private readonly GitRunner _runner = new();
        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new(); Directory.CreateDirectory(fixture.DirectoryPath);
            try
            {
                await fixture.Git("init", "-b", "main"); await fixture.Git("config", "user.name", "Diff Tester"); await fixture.Git("config", "user.email", "diff@example.invalid"); await fixture.Git("config", "commit.gpgsign", "false");
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public async Task<GitResult> Git(params string[] args)
        { GitResult result = await _runner.RunAsync(DirectoryPath, args); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public async Task<string> CommitTreeAsync(string contents, string message, params string[] parents)
        {
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "file.txt"), contents); await Git("add", "--", "file.txt");
            string tree = (await Git("write-tree")).StandardOutput.Trim();
            List<string> arguments = ["commit-tree", tree, "-m", message]; foreach (string parent in parents) { arguments.AddRange(["-p", parent]); }
            return (await Git(arguments.ToArray())).StandardOutput.Trim();
        }
        public void Dispose() { if (Directory.Exists(DirectoryPath)) { Directory.Delete(DirectoryPath, true); } }
    }
}
