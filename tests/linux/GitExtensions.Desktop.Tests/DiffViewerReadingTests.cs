using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands.Settings;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DiffViewerReadingTests
{
    private const string Header = "diff --git a/file.txt b/file.txt\n--- a/file.txt\n+++ b/file.txt\n@@ -41,1 +71,1 @@\n";
    private const string Body = "-ancien α🙂\n+nouveau α🙂\n";
    private const string Patch = Header + Body;

    [AvaloniaTest]
    public async Task RealGitHighFileLineIsReachedThroughTheOriginalCtrlGDialogAndLiteralPath()
    {
        using RepositoryFixture repository = await RepositoryFixture.CreateAsync();
        using Fixture fixture = new();
        fixture.View.DocumentIdentity = repository.Path + "/" + RepositoryFixture.FileName + "/HEAD-worktree";
        fixture.View.ShowDocument(await repository.Repository.GetDiffDocumentAsync(RepositoryFixture.FileName, displayOptions: new(false)));
        Pump(fixture.Window);
        Assert.That(fixture.View.MaxFileLine, Is.GreaterThan(fixture.View.Editor.Document.LineCount));
        Assert.That(fixture.View.CanStagePatch, Is.True);
        fixture.View.Editor.TextArea.Focus(); Press(fixture.Window, PhysicalKey.G, RawInputModifiers.Control);
        Window dialog = await Owned(fixture.Window);
        NumericUpDown number = dialog.GetVisualDescendants().OfType<NumericUpDown>().Single();
        Assert.That(number.Maximum, Is.EqualTo(fixture.View.MaxFileLine));
        TextBox input = number.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.That(input.IsKeyboardFocusWithin, Is.True);
        Press(dialog, PhysicalKey.A, RawInputModifiers.Control); dialog.KeyTextInput("181"); Pump(dialog);
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "DiffViewerGoToAccept"));
        await fixture.View.ReadingMenu.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5)); Pump(fixture.Window);
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(181));
        Assert.That(fixture.View.Editor.Document.GetText(fixture.View.Editor.Document.GetLineByOffset(fixture.View.CaretOffset)), Does.StartWith("+Changed line 181"));
        Assert.That(fixture.View.Editor.TextArea.IsKeyboardFocusWithin, Is.True);
        Assert.That(fixture.View.GoToFileLine(181, oldVersion: true), Is.True);
        Assert.That(fixture.View.Editor.Document.GetText(fixture.View.Editor.Document.GetLineByOffset(fixture.View.CaretOffset)), Does.StartWith("-Line 181"));
        Assert.That(fixture.View.GoToFileLine(0), Is.False);
        Assert.That(fixture.View.GoToFileLine(fixture.View.MaxFileLine + 1), Is.False);
    }

    [AvaloniaTest]
    public void DeletedCombinedRangeAndPlainViewsUseTheOriginalFileNumberSemantics()
    {
        using Fixture fixture = new();
        fixture.Source.Text = "diff --git a/deleted b/deleted\n--- a/deleted\n+++ /dev/null\n@@ -120,2 +0,0 @@\n-first\n-second\n";
        Assert.That(fixture.View.MaxFileLine, Is.EqualTo(121));
        Assert.That(fixture.View.GoToFileLine(121), Is.True);
        Assert.That(fixture.View.CaretOffset, Is.Zero, "The original default right-hand goto returns offset zero for a fully deleted file.");
        Assert.That(fixture.View.GoToFileLine(121, oldVersion: true), Is.True);
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(121));
        Assert.That(fixture.View.Editor.Document.GetText(fixture.View.Editor.Document.GetLineByOffset(fixture.View.CaretOffset)), Is.EqualTo("-second"));
        fixture.Source.Text = "diff --cc merged\n@@@ -40,2 -80,2 +120,2 @@@\n  context\n++added\n";
        Assert.That(fixture.View.MaxFileLine, Is.EqualTo(121));
        Assert.That(fixture.View.GoToFileLine(121), Is.True);
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(121));
        Assert.That(fixture.View.GetColumnNumbers(fixture.View.Editor.Document.GetLineByOffset(fixture.View.CaretOffset).LineNumber), Is.EqualTo(new int?[] { null, null, 121 }));
        Assert.That(fixture.View.CanCopyPatch, Is.True, "The original copy menu treats CombinedDiff as a normal diff when appearance is Patch.");
        fixture.View.Mode = DiffViewMode.RangeDiff;
        fixture.Source.Text = "1: aaaaaa ! 1: bbbbbb summary\n    @@ -200 +300 @@\n     +nested\n";
        Assert.That(fixture.View.MaxFileLine, Is.EqualTo(4));
        Assert.That(fixture.View.GoToFileLine(2), Is.True);
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(2));
        Assert.That(fixture.View.CanCopyPatch, Is.False);
        fixture.View.Mode = DiffViewMode.PlainText; fixture.Source.Text = Patch;
        Assert.That(fixture.View.GoToFileLine(5), Is.True);
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(5));
        Assert.That(fixture.View.MaxFileLine, Is.EqualTo(fixture.View.Editor.Document.LineCount));
        Assert.That(fixture.View.CanCopyPatch, Is.False);
    }

    [AvaloniaTest]
    public async Task RenderedCopyCommandsPreserveUnicodeCrLfAndUseTheConfiguredLineEndingRule()
    {
        using Fixture fixture = new();
        string patch = Patch.Replace("\n", "\r\n", StringComparison.Ordinal);
        fixture.View.ShowDocument(Document(patch)); fixture.View.CopyAutoCrlf = AutoCRLFType.@false;
        IClipboard clipboard = fixture.Window.Clipboard!;
        string? previous = await ClipboardExtensions.TryGetTextAsync(clipboard);
        try
        {
            int start = patch.IndexOf("-ancien", StringComparison.Ordinal);
            fixture.View.Select(start, Body.Replace("\n", "\r\n", StringComparison.Ordinal).Length);
            foreach ((string command, string expected) in new[]
            {
                ("CopyPatch", "-ancien α🙂\r\n+nouveau α🙂\r\n"),
                ("CopyNewVersion", "nouveau α🙂\r\n"),
                ("CopyOldVersion", "ancien α🙂\r\n")
            })
            {
                await ClipboardExtensions.SetTextAsync(clipboard, "before");
                Click(OpenMenu(fixture, command));
                await fixture.View.ReadingMenu.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo(expected));
                Assert.That(fixture.View.SelectionStart, Is.EqualTo(start));
                Assert.That(fixture.Source.SelectionStart, Is.EqualTo(start));
            }
            fixture.View.Select(patch.IndexOf("α🙂", StringComparison.Ordinal), "α🙂".Length);
            Click(OpenMenu(fixture, "CopyNewVersion")); await fixture.View.ReadingMenu.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("α🙂"), "A mid-line selection must not lose a code character or split its surrogate pair.");
            fixture.View.Select(start, Body.Replace("\n", "\r\n", StringComparison.Ordinal).Length);
            fixture.View.CopyAutoCrlf = AutoCRLFType.@true;
            Click(OpenMenu(fixture, "CopyNewVersion")); await fixture.View.ReadingMenu.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo("nouveau α🙂" + Environment.NewLine));
            fixture.View.Select(0, 0);
            Click(OpenMenu(fixture, "CopyPatch")); await fixture.View.ReadingMenu.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(await ClipboardExtensions.TryGetTextAsync(clipboard), Is.EqualTo(patch), "No selection copies the whole patch verbatim even when autocrlf is true.");
        }
        finally { await ClipboardExtensions.SetTextAsync(clipboard, previous ?? ""); }
    }

    [AvaloniaTest]
    public async Task OriginalFrenchReadingMenuAndNumericDialogRenderAndCancelWithoutMovingInBothThemes()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using Fixture fixture = new(); fixture.View.ShowDocument(Document(Patch));
            fixture.View.Select(Patch.IndexOf("α🙂", StringComparison.Ordinal), "α🙂".Length);
            int start = fixture.View.SelectionStart, caret = fixture.View.CaretOffset;
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                fixture.Window.RequestedThemeVariant = theme;
                MenuItem goTo = OpenMenu(fixture, "GoToLine");
                Assert.That(((AccessText)goTo.Header!).Text, Is.EqualTo("Aller à la _ligne…"));
                Assert.That(((AccessText)Item(fixture, "CopyNewVersion").Header!).Text, Is.EqualTo("Copier la _nouvelle version"));
                foreach (MenuItem item in fixture.View.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.Name?.StartsWith("DiffViewerCopy", StringComparison.Ordinal) == true || item == goTo))
                { Assert.That(item.Bounds.Height, Is.EqualTo(22)); Assert.That(((AccessText)item.Header!).Text, Does.Not.Contain("&")); }
                Capture(TopLevel.GetTopLevel(goTo)!, "diff-reading-menu-fr-" + theme + ".png");
                goTo.Focus(); TopLevel popup = TopLevel.GetTopLevel(goTo)!; Press(popup, PhysicalKey.Enter, RawInputModifiers.None);
                Window dialog = await Owned(fixture.Window); Pump(dialog);
                Assert.That(dialog.Title, Is.EqualTo("Aller à la ligne"));
                NumericUpDown number = dialog.GetVisualDescendants().OfType<NumericUpDown>().Single();
                TextBox input = number.GetVisualDescendants().OfType<TextBox>().Single();
                Assert.That(number.Minimum, Is.EqualTo(1)); Assert.That(number.Value, Is.EqualTo(1));
                Assert.That(input.Text, Is.EqualTo("1")); Assert.That(input.Bounds.Height, Is.GreaterThanOrEqualTo(20));
                Capture(dialog, "diff-reading-goto-fr-" + theme + ".png");
                Assert.That(dialog.ActualThemeVariant, Is.EqualTo(theme), "The owned goto dialog must use its viewer's current theme.");
                IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(theme == ThemeVariant.Dark);
                Color background = Color.Parse(colors["PanelBackground"]), foreground = Color.Parse(colors["WindowText"]);
                TextBlock label = dialog.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Name == "DiffViewerLineLabel");
                Assert.That(((ISolidColorBrush)dialog.Background!).Color, Is.EqualTo(background));
                Assert.That(((ISolidColorBrush)label.Foreground!).Color, Is.EqualTo(foreground));
                using (WriteableBitmap frame = dialog.CaptureRenderedFrame() ?? throw new AssertionException("No goto dialog frame."))
                { AssertTextPixels(frame, new(label.TranslatePoint(default, dialog)!.Value, label.Bounds.Size), foreground, background); }
                Press(dialog, PhysicalKey.Escape, RawInputModifiers.None);
                await fixture.View.ReadingMenu.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5)); Pump(fixture.Window);
                Assert.That(fixture.View.SelectionStart, Is.EqualTo(start)); Assert.That(fixture.View.CaretOffset, Is.EqualTo(caret));
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public void ThemeColorAndIdenticalReloadPreserveBackwardSelectionCaretAndActualScroll()
    {
        using Fixture fixture = new(); string text = WidePatch();
        fixture.View.DocumentIdentity = "repo/file/parent-result"; fixture.View.ShowDocument(Document(text)); Pump(fixture.Window);
        int start = text.IndexOf("nouveau α🙂", StringComparison.Ordinal), length = "nouveau α🙂".Length;
        fixture.View.Select(start, length); fixture.View.Editor.CaretOffset = start;
        int row = fixture.View.Editor.Document.GetLineByOffset(start).LineNumber;
        fixture.View.Editor.ScrollToLine(row); Pump(fixture.Window);
        IScrollable scrolling = fixture.View.Editor.TextArea.TextView;
        scrolling.Offset = new Vector(60, scrolling.Offset.Y + 2); Pump(fixture.Window);
        Vector expected = scrolling.Offset;
        Assert.That(expected.Y, Is.GreaterThan(0)); Assert.That(expected.X, Is.GreaterThan(0));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            fixture.Window.RequestedThemeVariant = theme; Pump(fixture.Window);
            AssertPosition();
            fixture.View.ShowDocument(Document(text)); Pump(fixture.Window); AssertPosition();
        }
        DiffViewerPreferencesStore store = new(fixture.Directory);
        Assert.That(store.Save(store.Load() with { ReverseGitColoring = false }), Is.True); Pump(fixture.Window); AssertPosition();
        void AssertPosition()
        {
            Assert.That(fixture.View.SelectionStart, Is.EqualTo(start)); Assert.That(fixture.View.SelectionLength, Is.EqualTo(length));
            Assert.That(fixture.View.CaretOffset, Is.EqualTo(start));
            Assert.That(fixture.Source.SelectionStart, Is.EqualTo(start)); Assert.That(fixture.Source.SelectionEnd, Is.EqualTo(start + length));
            Assert.That(scrolling.Offset.X, Is.EqualTo(expected.X).Within(0.5)); Assert.That(scrolling.Offset.Y, Is.EqualTo(expected.Y).Within(0.5));
        }
    }

    [AvaloniaTest]
    public async Task RealGitContextReloadRemapsSelectionByFileLineAndAnotherIdentityResetsEvenForIdenticalText()
    {
        using RepositoryFixture repository = await RepositoryFixture.CreateAsync(); using Fixture fixture = new();
        fixture.View.DocumentIdentity = "repo/literal-file/HEAD-worktree";
        GitDiffDocument compact = await repository.Repository.GetDiffDocumentAsync(RepositoryFixture.FileName, options: new(ContextLines: 1), displayOptions: new(false));
        fixture.View.ShowDocument(compact); int start = fixture.View.Text.IndexOf("λ🙂", StringComparison.Ordinal);
        fixture.View.Select(start, "λ🙂".Length); int fileLine = fixture.View.CurrentFileLine;
        GitDiffDocument expanded = await repository.Repository.GetDiffDocumentAsync(RepositoryFixture.FileName, options: new(ContextLines: 15), displayOptions: new(false));
        fixture.View.ShowDocument(expanded); Pump(fixture.Window);
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(fileLine));
        Assert.That(fixture.View.Text.Substring(fixture.View.SelectionStart, fixture.View.SelectionLength), Is.EqualTo("λ🙂"));
        Assert.That(fixture.View.SelectionStart, Is.Not.EqualTo(start));
        Assert.That(fixture.Source.SelectionStart, Is.EqualTo(fixture.View.SelectionStart));
        fixture.View.Editor.ScrollToLine(fixture.View.Editor.Document.LineCount); Pump(fixture.Window);
        fixture.View.DocumentIdentity = "repo/other-file/HEAD-worktree";
        fixture.View.ShowDocument(expanded); Pump(fixture.Window);
        Assert.That(fixture.View.SelectionLength, Is.Zero); Assert.That(fixture.View.CaretOffset, Is.Zero);
        Assert.That(fixture.View.Editor.TextArea.TextView.VerticalOffset, Is.Zero);
        fixture.View.Select(20, 3); fixture.View.DocumentIdentity = "repo/other-file/another-revision";
        Assert.That(fixture.View.CaretOffset, Is.Zero, "Identity changes reset immediately even if the caller's Text property assignment is a no-op.");
    }

    [AvaloniaTest]
    public void ContextReloadKeepsTheSelectedFileSectionWhenMultipleFilesHaveTheSameNumbers()
    {
        using Fixture fixture = new();
        string first = "diff --git a/one b/one\n@@ -40,1 +50,1 @@\n-old one\n+new one\n";
        string second = "diff --git a/two b/two\n@@ -40,1 +50,1 @@\n-old two\n+new two\n";
        fixture.View.DocumentIdentity = "repo/multiple-files/parent-result";
        fixture.View.ShowDocument(Document(first + second));
        fixture.View.Select(fixture.View.Text.IndexOf("new two", StringComparison.Ordinal), "new two".Length);
        string expanded = first.Replace("@@ -40,1 +50,1 @@", "@@ -39,2 +49,2 @@\n context one", StringComparison.Ordinal)
            + second.Replace("@@ -40,1 +50,1 @@", "@@ -39,2 +49,2 @@\n context two", StringComparison.Ordinal);
        fixture.View.ShowDocument(Document(expanded)); Pump(fixture.Window);
        Assert.That(fixture.View.Text.Substring(fixture.View.SelectionStart, fixture.View.SelectionLength), Is.EqualTo("new two"));
        Assert.That(fixture.View.SelectionStart, Is.GreaterThan(fixture.View.Text.IndexOf("diff --git a/two", StringComparison.Ordinal)));
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(50));
    }

    [AvaloniaTest]
    public void RealControlWheelZoomKeepsUnicodeSelectionColumnsAndInlineOffsetsAndStopsAtOriginalMinimum()
    {
        using Fixture fixture = new(); fixture.View.ShowDocument(Document(WidePatch())); Pump(fixture.Window);
        int start = fixture.View.Text.IndexOf("α🙂", StringComparison.Ordinal);
        fixture.View.Select(start, "α🙂".Length);
        int row = fixture.View.Editor.Document.GetLineByOffset(start).LineNumber;
        int?[] numbers = fixture.View.GetColumnNumbers(row).ToArray();
        var inline = fixture.View.InlineSpans.Select(span => (span.Offset, span.Length)).ToArray();
        Assert.That(inline, Is.Not.Empty);
        double original = fixture.View.Editor.FontSize;
        Point point = fixture.View.TranslatePoint(new Point(160, 90), fixture.Window)!.Value;
        fixture.Window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control); Pump(fixture.Window);
        Assert.That(fixture.View.Editor.FontSize, Is.EqualTo(original + 1)); AssertOffsets();
        Capture(fixture.Window, "diff-reading-zoom-light.png");
        fixture.Window.RequestedThemeVariant = ThemeVariant.Dark; Pump(fixture.Window);
        fixture.Window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Control); Pump(fixture.Window);
        Assert.That(fixture.View.Editor.FontSize, Is.EqualTo(original)); AssertOffsets();
        Capture(fixture.Window, "diff-reading-zoom-dark.png");
        for (int step = 0; step < 20; step++) { fixture.Window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Control); Pump(fixture.Window); }
        Assert.That(fixture.View.Editor.FontSize, Is.EqualTo(6)); AssertOffsets();
        void AssertOffsets()
        {
            Assert.That(fixture.View.SelectionStart, Is.EqualTo(start)); Assert.That(fixture.View.SelectionLength, Is.EqualTo("α🙂".Length));
            Assert.That(fixture.Source.SelectionStart, Is.EqualTo(start)); Assert.That(fixture.View.GetColumnNumbers(row), Is.EqualTo(numbers));
            Assert.That(fixture.View.InlineSpans.Select(span => (span.Offset, span.Length)), Is.EqualTo(inline)); Assert.That(fixture.View.CanStagePatch, Is.True);
        }
    }

    [AvaloniaTest]
    public void AppearanceReloadUsesFileNumbersAcrossRealDifftasticAndPatchHeaders()
    {
        using Fixture fixture = new();
        string compact = "diff --git a/numeric-new.txt b/numeric-new.txt\n--- a/numeric-new.txt\n+++ b/numeric-new.txt\n@@ -3,1 +2,1 @@\n 3000 shared record\n";
        fixture.View.DocumentIdentity = "repo/numeric-new.txt/parent-result";
        fixture.View.ShowDocument(Document(compact)); Assert.That(fixture.View.GoToFileLine(2), Is.True);
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "tests/linux/Fixtures/difftastic-0.71.0-numeric-unicode-177.ansi.txt"))) { root = root.Parent; }
        Assert.That(root, Is.Not.Null, "The real tool fixture must be available in the source checkout.");
        string display = File.ReadAllText(System.IO.Path.Combine(root!.FullName, "tests/linux/Fixtures/difftastic-0.71.0-numeric-unicode-177.ansi.txt"));
        fixture.View.ShowDocument(new(compact, display, GitDiffAppearance.Difftastic, false) { DifftasticWidth = 177, UseGitColoring = true }); Pump(fixture.Window);
        int row = fixture.View.Editor.Document.GetLineByOffset(fixture.View.CaretOffset).LineNumber;
        Assert.That(fixture.View.GetColumnNumbers(row), Is.EqualTo(new int?[] { 3, 2 }));
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(2));
        Assert.That(fixture.View.Editor.Document.GetText(fixture.View.Editor.Document.GetLineByNumber(row)), Does.StartWith("3000 shared record"));
        Assert.That(fixture.View.MaxFileLine, Is.EqualTo(7)); Assert.That(fixture.View.CanCopyPatch, Is.False);
        int?[][] numbers = Enumerable.Range(1, fixture.View.Editor.Document.LineCount).Select(line => fixture.View.GetColumnNumbers(line).ToArray()).ToArray();
        Point point = fixture.View.TranslatePoint(new Point(180, 60), fixture.Window)!.Value;
        fixture.Window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control); Pump(fixture.Window);
        Assert.That(Enumerable.Range(1, fixture.View.Editor.Document.LineCount).Select(line => fixture.View.GetColumnNumbers(line).ToArray()), Is.EqualTo(numbers));
        fixture.View.ShowDocument(Document(compact.Replace("@@ -3,1 +2,1 @@", "@@ -1,3 +1,2 @@\n 1000 unchanged sentinel\n-2000 deleted λ 🦊 漢字", StringComparison.Ordinal))); Pump(fixture.Window);
        Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(2));
        Assert.That(fixture.View.Editor.Document.GetText(fixture.View.Editor.Document.GetLineByOffset(fixture.View.CaretOffset)), Is.EqualTo(" 3000 shared record"));
    }

    [AvaloniaTest]
    public async Task EncodingSelectorUsesRepositoryDefaultAndRealKeyboardChoiceWithoutWritingGitConfiguration()
    {
        using RepositoryFixture repository = await RepositoryFixture.CreateAsync(); using Fixture fixture = new();
        await repository.Git("config", "i18n.filesencoding", "ISO-8859-1"); await repository.Git("config", "core.autocrlf", "true");
        await fixture.View.ConfigureRepositoryAsync(repository.Repository);
        Assert.That(fixture.View.DefaultFileEncodingName, Is.EqualTo("iso-8859-1")); Assert.That(fixture.View.FileEncodingName, Is.Null);
        Assert.That(fixture.View.CopyAutoCrlf, Is.EqualTo(AutoCRLFType.@true));
        ComboBox selector = fixture.View.ReadingMenu.CreateEncodingSelector();
        fixture.Toolbar.Children.Add(selector); Pump(fixture.Window);
        string[] labels = selector.Items.OfType<DiffViewerEncodingChoice>().Select(item => item.DisplayName).ToArray();
        Assert.That(labels[0], Does.Contain("Western European")); Assert.That(selector.SelectedIndex, Is.Zero);
        List<string> events = [];
        fixture.View.FileEncodingChanged += (_, _) => events.Add("file"); fixture.View.DisplayOptionsChanged += (_, _) => events.Add("diff");
        int index = selector.Items.OfType<DiffViewerEncodingChoice>().Select((item, row) => (item.Name, Row: row))
            .Single(item => item.Name == "windows-1252").Row;
        selector.Focus(); Press(fixture.Window, PhysicalKey.F4, RawInputModifiers.None);
        Assert.That(selector.IsDropDownOpen, Is.True);
        Press(fixture.Window, PhysicalKey.Home, RawInputModifiers.None);
        for (int row = 0; row < index; row++) { Press(fixture.Window, PhysicalKey.ArrowDown, RawInputModifiers.None); }
        Press(fixture.Window, PhysicalKey.Enter, RawInputModifiers.None); Pump(fixture.Window);
        Assert.That(fixture.View.FileEncodingName, Is.EqualTo("windows-1252")); Assert.That(events, Is.Not.Empty); Assert.That(events, Does.Not.Contain("diff"));
        Assert.That((await repository.Git("config", "--get", "i18n.filesencoding")).StandardOutput.Trim(), Is.EqualTo("ISO-8859-1"));
        fixture.View.ShowDocument(Document(Patch)); events.Clear(); fixture.View.FileEncodingName = null;
        Assert.That(events, Is.EqualTo(new[] { "file", "diff" })); Assert.That(fixture.View.DisplayOptions.FileEncodingName, Is.Null);
        Assert.That(selector.SelectedIndex, Is.Zero);
        fixture.View.FileEncodingName = "latin1";
        Assert.That(fixture.View.DisplayOptions.FileEncodingName, Is.EqualTo("iso-8859-1"));
        Assert.Throws<NotSupportedException>(() => fixture.View.FileEncodingName = "not-a-file-codec");
        Assert.That(fixture.View.FileEncodingName, Is.EqualTo("iso-8859-1"));
    }

    [AvaloniaTest]
    public async Task RealEncodingPopupKeepsTheOriginalWidthAndReadableSelectionInBothThemes()
    {
        using RepositoryFixture repository = await RepositoryFixture.CreateAsync(); using Fixture fixture = new();
        GitFileEncodingChoice longest = GitFileEncodingCodec.AvailableEncodings.MaxBy(choice => choice.DisplayName.Length)!;
        await repository.Git("config", "i18n.filesencoding", longest.Name);
        await fixture.View.ConfigureRepositoryAsync(repository.Repository);
        ComboBox selector = fixture.View.ReadingMenu.CreateEncodingSelector(); fixture.Toolbar.Children.Add(selector); Pump(fixture.Window);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Window.RequestedThemeVariant = theme; Pump(fixture.Window);
            Assert.That(selector.Bounds.Width, Is.EqualTo(140)); Assert.That(selector.Bounds.Height, Is.EqualTo(24));
            TextBlock closed = selector.GetVisualDescendants().OfType<TextBlock>().Single(label => label.Name == "DiffViewerEncodingLabel");
            Assert.That(closed.Text, Does.StartWith(longest.DisplayName));
            Assert.That(closed.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.True, "The 140 px selector must display an ellipsis rather than clip a long encoding name.");
            Assert.That(ToolTip.GetTip(closed), Is.EqualTo(closed.Text));
            Assert.That(selector.Focus(), Is.True); Press(fixture.Window, PhysicalKey.F4, RawInputModifiers.None);
            Assert.That(selector.IsDropDownOpen, Is.True);
            ComboBoxItem active = (ComboBoxItem)(selector.ContainerFromIndex(selector.SelectedIndex) ?? throw new AssertionException("No realized encoding choice."));
            TopLevel popup = TopLevel.GetTopLevel(active) ?? throw new AssertionException("No encoding popup root."); Pump(popup);
            popup.MouseMove(new Point(popup.Bounds.Width - 2, popup.Bounds.Height - 2)); Pump(popup);
            Popup dropdown = selector.GetVisualDescendants().OfType<Popup>().Single(item => item.Name == "PART_Popup");
            Assert.That(dropdown.IsOpen, Is.True);
            Control child = dropdown.Child ?? throw new AssertionException("No attached encoding popup child.");
            Assert.That(TopLevel.GetTopLevel(child), Is.SameAs(popup));
            Assert.That(child.GetVisualDescendants(), Does.Contain(active), "The selected row must belong to the opened popup child, including on overlay-popup platforms.");
            Assert.That(child.IsEffectivelyVisible && active.IsEffectivelyVisible, Is.True);
            Assert.That(child.Bounds.Width, Is.InRange(140d, 250d));
            Assert.That(active.IsSelected, Is.True);
            Assert.That(active.Bounds.Height, Is.EqualTo(24));
            TextBlock label = active.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Name == "DiffViewerEncodingLabel");
            Assert.That(label.Bounds.Width, Is.LessThanOrEqualTo(216)); Assert.That(ToolTip.GetTip(label), Is.EqualTo(label.Text));
            ContentPresenter presenter = active.GetVisualDescendants().OfType<ContentPresenter>().Single(item => item.Name == "PART_ContentPresenter");
            IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(theme == ThemeVariant.Dark);
            Color background = Color.Parse(colors["Highlight"]), foreground = Color.Parse(colors[theme == ThemeVariant.Dark ? "WindowText" : "HighlightText"]);
            Assert.That(((ISolidColorBrush)presenter.Background!).Color, Is.EqualTo(background));
            Assert.That(((ISolidColorBrush)presenter.Foreground!).Color, Is.EqualTo(foreground));
            Assert.That(((ISolidColorBrush)label.Foreground!).Color, Is.EqualTo(foreground));
            Assert.That(Math.Round(Contrast(foreground, background), 2), Is.GreaterThanOrEqualTo(4.5));
            using WriteableBitmap frame = popup.CaptureRenderedFrame() ?? throw new AssertionException("No encoding popup frame.");
            Rect textBounds = new(label.TranslatePoint(default, popup)!.Value, label.Bounds.Size);
            AssertTextPixels(frame, textBounds, foreground, background);
            ComboBoxItem ordinary = (ComboBoxItem)(selector.ContainerFromIndex(1) ?? throw new AssertionException("No rendered unselected encoding choice."));
            Assert.That(ordinary.IsSelected || ordinary.IsPointerOver, Is.False);
            TextBlock ordinaryLabel = ordinary.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Name == "DiffViewerEncodingLabel");
            Color ordinaryForeground = Color.Parse(colors["WindowText"]), ordinaryBackground = Color.Parse(colors["EditorBackground"]);
            Assert.That(((ISolidColorBrush)ordinaryLabel.Foreground!).Color, Is.EqualTo(ordinaryForeground));
            AssertTextPixels(frame, new(ordinaryLabel.TranslatePoint(default, popup)!.Value, ordinaryLabel.Bounds.Size), ordinaryForeground, ordinaryBackground);
            string path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "diff-reading-encoding-popup-" + theme + ".png"); frame.Save(path); TestContext.AddTestAttachment(path);
            Press(fixture.Window, PhysicalKey.Escape, RawInputModifiers.None); Assert.That(selector.IsDropDownOpen, Is.False);
            Assert.That(fixture.View.FileEncodingName, Is.Null, "Opening and cancelling the dropdown must retain the repository default.");
        }
    }

    private static void AssertTextPixels(WriteableBitmap frame, Rect bounds, Color foreground, Color background)
    {
        using ILockedFramebuffer pixels = frame.Lock(); byte[] data = new byte[pixels.RowBytes * pixels.Size.Height];
        Marshal.Copy(pixels.Address, data, 0, data.Length); bool bgra = pixels.Format == PixelFormat.Bgra8888;
        double dr = foreground.R - background.R, dg = foreground.G - background.G, db = foreground.B - background.B, magnitude = dr * dr + dg * dg + db * db;
        int glyphs = 0, solidBackground = 0;
        for (int y = Math.Max(0, (int)Math.Floor(bounds.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(bounds.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(bounds.Right)); x++)
            {
                int offset = y * pixels.RowBytes + x * 4; double r = data[offset + (bgra ? 2 : 0)], g = data[offset + 1], b = data[offset + (bgra ? 0 : 2)];
                if (r == background.R && g == background.G && b == background.B) { solidBackground++; }
                double coverage = ((r - background.R) * dr + (g - background.G) * dg + (b - background.B) * db) / magnitude;
                if (coverage >= .25 && coverage <= 1.05 && Math.Abs(r - (background.R + coverage * dr)) <= 3
                    && Math.Abs(g - (background.G + coverage * dg)) <= 3 && Math.Abs(b - (background.B + coverage * db)) <= 3) { glyphs++; }
            }
        }
        Assert.That(solidBackground, Is.GreaterThan(100), "The original background must reach the rendered label.");
        Assert.That(glyphs, Is.GreaterThan(30), "The label must actually render in its contrasting original foreground.");
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static GitDiffDocument Document(string text) => new(text, text, GitDiffAppearance.Patch, true) { IsLosslessPatch = true, PatchBytes = Encoding.UTF8.GetBytes(text) };
    private static string WidePatch() => "diff --git a/wide b/wide\n--- a/wide\n+++ b/wide\n@@ -501,200 +601,200 @@\n"
        + string.Concat(Enumerable.Range(1, 200).Select(row => row == 90 ? "-ancien α🙂 " + new string('x', 180) + "\n+nouveau α🙂 " + new string('x', 180) + "\n" : " context " + row + " " + new string('x', 180) + "\n"));
    private static void Pump(TopLevel window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers)
    { window.KeyPressQwerty(key, modifiers); if (window is not Window { IsVisible: false }) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Click(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control) ?? throw new AssertionException("No rendered control root."); Pump(root);
        Assert.That(control.IsEffectivelyVisible && control.Bounds.Height > 0, Is.True);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root);
    }
    private static MenuItem Item(Fixture fixture, string name) => fixture.View.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "DiffViewer" + name);
    private static MenuItem OpenMenu(Fixture fixture, string name)
    {
        fixture.View.ContextMenu!.Close(); fixture.Window.MouseMove(fixture.View.TranslatePoint(new Point(12, 12), fixture.Window)!.Value);
        fixture.View.ContextMenu.Open(fixture.View); Pump(fixture.Window); MenuItem item = Item(fixture, name);
        Assert.That(item.IsEffectivelyVisible, Is.True); Assert.That(item.IsEffectivelyEnabled, Is.True); return item;
    }
    private static async Task<Window> Owned(Window owner)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (owner.OwnedWindows.FirstOrDefault(window => window.IsVisible && window.Name == "DiffViewerGoToLineDialog") is not Window)
        { Pump(owner); await Task.Delay(10, timeout.Token); }
        return owner.OwnedWindows.Single(window => window.IsVisible && window.Name == "DiffViewerGoToLineDialog");
    }
    private static void Capture(TopLevel root, string file)
    {
        Pump(root); using WriteableBitmap frame = root.CaptureRenderedFrame() ?? throw new AssertionException("No native viewer/menu frame.");
        string path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, file); frame.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-viewer-reading-" + Guid.NewGuid().ToString("N"));
        internal TextBox Source { get; } = new();
        internal DiffView View { get; }
        internal Window Window { get; }
        internal StackPanel Toolbar { get; } = new() { Orientation = Avalonia.Layout.Orientation.Horizontal };
        internal Fixture()
        {
            AvailableEncodingsPreferencesStore encodings = new(Directory);
            encodings.SaveChanges(encodings.Load(), encodings.Catalog.Defaults.Append("windows-1252"));
            View = new(Source, new DiffViewerPreferencesStore(Directory), encodings);
            DockPanel content = new(); DockPanel.SetDock(Toolbar, Dock.Top); content.Children.Add(Toolbar); content.Children.Add(View);
            Window = new() { Width = 720, Height = 300, Content = content }; Window.Show(); Pump(Window);
        }
        public void Dispose()
        {
            View.ContextMenu?.Close(); View.Dispose(); Window.Close(); Dispatcher.UIThread.RunJobs();
            if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, true); }
        }
    }
    private sealed class RepositoryFixture : IDisposable
    {
        internal const string FileName = "file[é].txt";
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-viewer-reading-repo-" + Guid.NewGuid().ToString("N"));
        internal GitRepository Repository { get; private set; } = null!;
        private readonly GitRunner _runner = new();
        internal static async Task<RepositoryFixture> CreateAsync()
        {
            RepositoryFixture fixture = new();
            try
            {
                System.IO.Directory.CreateDirectory(fixture.Path); await fixture.Git("init", "-b", "main");
                await fixture.Git("config", "user.name", "Viewer Tester"); await fixture.Git("config", "user.email", "viewer@example.org");
                await fixture.Git("config", "commit.gpgsign", "false"); await fixture.Git("config", "core.hooksPath", System.IO.Path.Combine(fixture.Path, "no-hooks"));
                string[] lines = Enumerable.Range(1, 230).Select(line => "Line " + line).ToArray();
                await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, FileName), string.Join('\n', lines) + "\n");
                await fixture.Git("add", "--all"); await fixture.Git("commit", "-m", "Initial viewer file");
                lines[180] = "Changed line 181 λ🙂";
                await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Path, FileName), string.Join('\n', lines) + "\n");
                fixture.Repository = await GitRepository.OpenAsync(fixture.Path); return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal async Task<GitResult> Git(params string[] arguments)
        { GitResult result = await _runner.RunAsync(Path, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        public void Dispose() { if (System.IO.Directory.Exists(Path)) { System.IO.Directory.Delete(Path, true); } }
    }
}
