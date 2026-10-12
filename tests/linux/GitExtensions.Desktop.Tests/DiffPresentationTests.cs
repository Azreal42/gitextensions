using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DiffPresentationTests
{
    private const string Header = "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n@@ -1,1 +1,1 @@\n";
    private const string Patch = Header + "-same old value λ🙂\n+same new value λ🙂\n";

    [AvaloniaTest]
    public void ColoredPatchKeepsCanonicalUnicodeCrLfOffsetsAcrossThemeChanges()
    {
        using Fixture fixture = new();
        string patch = Patch.Replace("\n", "\r\n", StringComparison.Ordinal);
        fixture.View.ShowDocument(new(patch, Colored(patch), GitDiffAppearance.Patch, true) { IsLosslessPatch = true });
        int start = patch.IndexOf("+same", StringComparison.Ordinal), length = "+same new value λ🙂\r\n".Length;
        fixture.View.Select(start, length);
        Assert.That(fixture.View.CanStagePatch, Is.True);
        Assert.That(fixture.View.Text, Is.EqualTo(patch));
        Assert.That(fixture.Source.Text, Is.EqualTo(patch));
        Assert.That(fixture.Source.SelectionStart, Is.EqualTo(start));
        Assert.That(fixture.Source.SelectionEnd, Is.EqualTo(start + length));
        Assert.That(fixture.View.AnsiSpans, Has.Count.EqualTo(2));
        Assert.That(fixture.View.InlineSpans.Any(span => span.Background is not null && span.Length > 1), Is.True);
        fixture.Window.RequestedThemeVariant = ThemeVariant.Dark;
        fixture.Window.UpdateLayout();
        Assert.That(fixture.View.Text, Is.EqualTo(patch));
        Assert.That(fixture.View.CanonicalText, Is.EqualTo(patch));
        Assert.That(fixture.View.SelectionStart, Is.EqualTo(start));
        Assert.That(fixture.View.SelectionLength, Is.EqualTo(length));
        Assert.That(fixture.View.AnsiSpans[0].Background, Is.EqualTo(DiffViewerColors.ForTheme(true).Backgrounds[1]));
        Assert.That(fixture.View.GetLineNumbers(5), Is.EqualTo(((int?)1, (int?)null)));
        Assert.That(fixture.View.GetLineNumbers(6), Is.EqualTo(((int?)null, (int?)1)));
    }

    [AvaloniaTest]
    public void OriginalAnsiAndDimmedInlineBackgroundsAreActuallyRenderedInBothThemes()
    {
        using Fixture fixture = new();
        fixture.View.ShowDocument(new(Patch, Colored(Patch), GitDiffAppearance.Patch, true));
        foreach (bool dark in new[] { false, true })
        {
            fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            fixture.Window.UpdateLayout();
            using WriteableBitmap frame = fixture.Window.CaptureRenderedFrame()!;
            Assert.That(frame, Is.Not.Null);
            GitDiffColorPalette palette = DiffViewerColors.ForTheme(dark);
            Assert.That(Count(frame, palette.Backgrounds[1]), Is.GreaterThan(8));
            Assert.That(Count(frame, palette.Backgrounds[2]), Is.GreaterThan(8));
            GitDiffColor dimmedRed = GitDiffColorMath.Dim(GitDiffColorMath.Dim(palette.Backgrounds[1], palette.EditorBackground), palette.EditorBackground);
            GitDiffColor dimmedGreen = GitDiffColorMath.Dim(GitDiffColorMath.Dim(palette.Backgrounds[2], palette.EditorBackground), palette.EditorBackground);
            Assert.That(Count(frame, dimmedRed), Is.GreaterThan(8));
            Assert.That(Count(frame, dimmedGreen), Is.GreaterThan(8));
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ansi-inline-" + (dark ? "dark" : "light") + ".png");
            frame.Save(path); TestContext.AddTestAttachment(path);
        }
    }

    [AvaloniaTest]
    public void AlternativeOrNonEquivalentDisplaysNeverExposeCanonicalStageSelection()
    {
        using Fixture fixture = new();
        foreach (GitDiffAppearance appearance in new[] { GitDiffAppearance.GitWordDiff, GitDiffAppearance.Difftastic, GitDiffAppearance.Patch })
        {
            fixture.View.ShowDocument(new(Patch, Header + "same oldnew value λ🙂\n", appearance, true));
            fixture.View.Select(Header.Length, 4);
            Assert.That(fixture.View.CanStagePatch, Is.False, appearance.ToString());
            Assert.That(fixture.Source.Text, Is.EqualTo(Patch));
            Assert.That(fixture.Source.SelectionStart, Is.Zero);
            Assert.That(fixture.Source.SelectionEnd, Is.Zero);
        }
        fixture.View.ShowDocument(new(Patch, Patch, GitDiffAppearance.Patch, false));
        Assert.That(fixture.View.CanStagePatch, Is.False, "The repository's readonly capability also applies when text is identical.");
    }

    [AvaloniaTest]
    public void ExplicitlyUncoloredGitOutputDoesNotRestoreApplicationWholeLineColors()
    {
        using Fixture fixture = new();
        string patch = Header + "-old\n+new\n";
        fixture.View.ShowDocument(new(patch, patch, GitDiffAppearance.Patch, true) { UseGitColoring = true, IsLosslessPatch = true });
        fixture.Window.UpdateLayout();
        using WriteableBitmap frame = fixture.Window.CaptureRenderedFrame()!;
        Assert.That(fixture.View.AnsiSpans, Is.Empty);
        Assert.That(fixture.View.InlineSpans, Is.Empty);
        Assert.That(Count(frame, DiffViewerColors.ForTheme(false).Backgrounds[1]), Is.Zero);
        Assert.That(Count(frame, DiffViewerColors.ForTheme(false).Backgrounds[2]), Is.Zero);
        Assert.That(fixture.View.CanStagePatch, Is.True);
    }

    [AvaloniaTest]
    public void PlainFilePreservesLiteralEscapeSequencesAndClearsDiffColors()
    {
        using Fixture fixture = new();
        fixture.View.ShowDocument(new(Patch, Colored(Patch), GitDiffAppearance.Patch, true) { IsLosslessPatch = true });
        fixture.View.Mode = DiffViewMode.PlainText;
        fixture.Source.Text = "literal \u001b[31mred\u001b[0m in a file\n";
        Assert.That(fixture.View.Text, Is.EqualTo(fixture.Source.Text));
        Assert.That(fixture.View.AnsiSpans, Is.Empty);
        Assert.That(fixture.View.InlineSpans, Is.Empty);
        Assert.That(fixture.View.CanStagePatch, Is.False);
        fixture.View.Mode = DiffViewMode.Auto;
        fixture.Source.Text = Patch;
        Assert.That(fixture.View.CanStagePatch, Is.True);
        Assert.That(fixture.View.AnsiSpans, Is.Empty);
    }

    [AvaloniaTest]
    public void TrueColorAnd256ColorSpansOverrideTheOriginalForegroundWithoutTouchingPatch()
    {
        using Fixture fixture = new();
        string raw = Patch.Replace("+same new value λ🙂", "\u001b[38;2;10;20;30;48;5;123m+same new value λ🙂\u001b[m", StringComparison.Ordinal);
        fixture.View.ShowDocument(new(Patch, raw, GitDiffAppearance.Patch, true));
        GitDiffColorSpan span = fixture.View.AnsiSpans.Single();
        Assert.That(span.Offset, Is.EqualTo(Patch.IndexOf("+same", StringComparison.Ordinal)));
        Assert.That(span.Foreground, Is.EqualTo(new GitDiffColor(10, 20, 30)));
        Assert.That(span.Background, Is.EqualTo(new GitDiffColor(102, 255, 255)));
        fixture.Window.UpdateLayout();
        using WriteableBitmap frame = fixture.Window.CaptureRenderedFrame()!;
        Assert.That(Count(frame, span.Background!.Value), Is.GreaterThan(8));
        Assert.That(Count(frame, span.Foreground!.Value), Is.GreaterThan(2));
        Assert.That(fixture.View.CanonicalText, Is.EqualTo(Patch));
    }

    [AvaloniaTest]
    public void MovedColorsRemainUndimmedByTheInlineMatcherAndInsertionGetsAnAnchor()
    {
        using Fixture fixture = new();
        string raw = Patch.Replace("-same old value λ🙂", "\u001b[30;105m-same old value λ🙂\u001b[m", StringComparison.Ordinal)
            .Replace("+same new value λ🙂", "\u001b[30;104m+same new value λ🙂\u001b[m", StringComparison.Ordinal);
        fixture.View.ShowDocument(new(Patch, raw, GitDiffAppearance.Patch, true));
        Assert.That(fixture.View.InlineSpans, Is.Empty, "Moved lines retain Git's zebra and dimmed-zebra colors.");
        string patch = Header + "-same value\n+same extra value\n";
        fixture.View.ShowDocument(new(patch, patch, GitDiffAppearance.Patch, true));
        Assert.That(fixture.View.InlineSpans.Any(span => span.IsAnchor && span.Length == 0), Is.True);
        fixture.Window.UpdateLayout();
        using WriteableBitmap frame = fixture.Window.CaptureRenderedFrame()!;
        Assert.That(Count(frame, DiffViewerColors.ForTheme(false).Foregrounds[10]), Is.GreaterThan(0), "The insertion anchor is painted.");
    }

    [AvaloniaTest]
    public void DifftasticMovesPrintedNumbersToBothGuttersAndKeepsItsColumnBoundary()
    {
        using Fixture fixture = new();
        fixture.Window.Width = 800;
        string display = "file.rs --- Rust\n\u001b[31m12 old\u001b[m" + new string(' ', 34) + "\u001b[32m13 new\u001b[m\n";
        fixture.View.ShowDocument(new(Patch, display, GitDiffAppearance.Difftastic, false) { DifftasticWidth = 80, UseGitColoring = true });
        Assert.That(fixture.View.EffectiveMode, Is.EqualTo(DiffViewMode.Difftastic));
        Assert.That(fixture.View.Text, Does.Contain("old").And.Contain("new").And.Not.Contain("12").And.Not.Contain("13"));
        Assert.That(fixture.View.GetColumnNumbers(2), Is.EqualTo(new int?[] { 12, 13 }));
        Assert.That(fixture.View.RightColumnStart, Is.EqualTo(37));
        Assert.That(fixture.View.CanonicalText, Is.EqualTo(Patch));
        Assert.That(fixture.View.CanStagePatch, Is.False);
        Assert.That(fixture.View.GoToNextChange(), Is.True);
        Assert.That(fixture.View.CaretOffset, Is.EqualTo("file.rs --- Rust\n".Length));
        foreach (bool dark in new[] { false, true })
        {
            fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            fixture.Window.UpdateLayout();
            Assert.That(fixture.View.GetColumnNumbers(2), Is.EqualTo(new int?[] { 12, 13 }));
            using WriteableBitmap frame = fixture.Window.CaptureRenderedFrame()!;
            Assert.That(frame, Is.Not.Null);
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "difftastic-columns-" + (dark ? "dark" : "light") + ".png");
            frame.Save(path); TestContext.AddTestAttachment(path);
        }
    }

    [AvaloniaTest]
    public void RealCompactDifftasticOutputRetainsBothGuttersOriginalColorsAndNumericCode()
    {
        using Stream stream = typeof(DiffPresentationTests).Assembly.GetManifestResourceStream("Difftastic071Compact")!;
        using StreamReader reader = new(stream);
        string display = reader.ReadToEnd();
        using Fixture fixture = new();
        fixture.Window.Width = 1200;
        fixture.Window.Height = 520;
        fixture.View.ShowDocument(new(Patch, display, GitDiffAppearance.Difftastic, false) { DifftasticWidth = 200, UseGitColoring = true });
        foreach (bool dark in new[] { false, true })
        {
            fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            fixture.Window.UpdateLayout();
            Assert.That(fixture.View.EffectiveMode, Is.EqualTo(DiffViewMode.Difftastic));
            Assert.That(fixture.View.RightColumnStart, Is.InRange(40, 60), "The real tool shrinks these columns despite DFT_WIDTH=200.");
            for (int line = 1; line <= 16; line++)
            {
                Assert.That(fixture.View.GetColumnNumbers(line + 1), Is.EqualTo(new int?[] { line, line }), $"Actual old/new numbers on row {line}.");
            }
            Assert.That(fixture.View.Text, Does.Contain("Value => 42;").And.Contain("Value => 43;")
                .And.Contain("Bonjour λ 🦊").And.Not.Contain(" 1 public"), "Removing printed numbers must preserve numbers and Unicode in the source code.");
            Assert.That(fixture.View.CanonicalText, Is.EqualTo(Patch));
            Assert.That(fixture.View.CanStagePatch, Is.False);
            Assert.That(fixture.View.AnsiSpans.All(span => span.Offset >= 0 && span.Offset + span.Length <= fixture.View.Text.Length), Is.True);
            using WriteableBitmap frame = fixture.Window.CaptureRenderedFrame()!;
            GitDiffColorPalette palette = DiffViewerColors.ForTheme(dark);
            Assert.That(Count(frame, palette.Backgrounds[1]), Is.GreaterThan(0), "Original removed-line color must be painted.");
            Assert.That(Count(frame, palette.Backgrounds[2]), Is.GreaterThan(0), "Original added-line color must be painted.");
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "difftastic-real-compact-" + (dark ? "dark" : "light") + ".png");
            frame.Save(path); TestContext.AddTestAttachment(path);
        }
    }

    [AvaloniaTest]
    public void UncoloredDifftasticKeepsTheExactToolOutputAndItsSourceNumbersReadOnly()
    {
        using Stream stream = typeof(DiffPresentationTests).Assembly.GetManifestResourceStream("Difftastic071Plain")!;
        using StreamReader reader = new(stream);
        string display = reader.ReadToEnd();
        using Fixture fixture = new();
        fixture.View.ShowDocument(new(Patch, display, GitDiffAppearance.Difftastic, false) { DifftasticWidth = 177, UseGitColoring = true });
        foreach (bool dark in new[] { false, true })
        {
            fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            fixture.Window.UpdateLayout();
            Assert.That(fixture.View.Text, Is.EqualTo(display), "The real one-column tool output can mimic a second gutter; all source numbers and whitespace must survive.");
            Assert.That(fixture.View.GetColumnNumbers(2), Is.EqualTo(new int?[] { null, null }));
            Assert.That(fixture.View.RightColumnStart, Is.Zero);
            Assert.That(fixture.View.CanonicalText, Is.EqualTo(Patch));
            Assert.That(fixture.View.CanStagePatch, Is.False);
            Assert.That(fixture.View.AnsiSpans, Is.Empty);
        }
    }

    [AvaloniaTest]
    public void GitWordDiffUsesOriginalColorHeuristicForExclusiveAndMixedLineNumbers()
    {
        using Fixture fixture = new();
        string display = Header + "\u001b[31;7mold value\u001b[m\n\u001b[32;7mnew value\u001b[m\nnormal \u001b[31;7mold\u001b[m\u001b[32;7mnew\u001b[m value\n";
        fixture.View.ShowDocument(new(Patch, display, GitDiffAppearance.GitWordDiff, false));
        Assert.That(fixture.View.GetLineNumbers(5), Is.EqualTo(((int?)1, (int?)null)));
        Assert.That(fixture.View.GetLineNumbers(6), Is.EqualTo(((int?)null, (int?)1)));
        Assert.That(fixture.View.GetLineNumbers(7), Is.EqualTo(((int?)2, (int?)2)));
        Assert.That(fixture.View.InlineSpans, Is.Empty);
        Assert.That(fixture.View.CanStagePatch, Is.False);
    }

    private static string Colored(string patch) => patch.Replace("-same old value λ🙂", "\u001b[31;7m-same old value λ🙂\u001b[m", StringComparison.Ordinal)
        .Replace("+same new value λ🙂", "\u001b[32;7m+same new value λ🙂\u001b[m", StringComparison.Ordinal);

    private static int Count(WriteableBitmap bitmap, GitDiffColor color)
    {
        using ILockedFramebuffer buffer = bitmap.Lock();
        byte[] bytes = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        bool bgra = buffer.Format == PixelFormat.Bgra8888; int count = 0;
        for (int y = 0; y < buffer.Size.Height; y++) for (int x = 0; x < buffer.Size.Width; x++)
        {
            int offset = y * buffer.RowBytes + x * 4;
            if (bytes[offset] == (bgra ? color.B : color.R) && bytes[offset + 1] == color.G && bytes[offset + 2] == (bgra ? color.R : color.B) && bytes[offset + 3] == 255) { count++; }
        }
        return count;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-diff-presentation-" + Guid.NewGuid().ToString("N"));
        internal TextBox Source { get; } = new();
        internal DiffView View { get; }
        internal Window Window { get; }
        internal Fixture()
        {
            View = new(Source, new DiffViewerPreferencesStore(_root));
            Window = new() { Width = 640, Height = 260, Content = View, RequestedThemeVariant = ThemeVariant.Light };
            Window.Show();
        }
        public void Dispose() { View.Dispose(); Window.Close(); if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}
