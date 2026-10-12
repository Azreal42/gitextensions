using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DiffViewerSearchTests
{
    [AvaloniaTest]
    public void CtrlFSeedsSelectionOrCaretWordAndReusesTheReadOnlyDialog()
    {
        using Fixture fixture = new("first α_name second\r\nlast");
        int start = fixture.View.Text.IndexOf("α_name", StringComparison.Ordinal);
        fixture.View.Select(start, "α_name".Length); PressFind(fixture);
        Window dialog = FindWindow(fixture);
        TextBox query = Find<TextBox>(dialog, "DiffViewerFindQuery");
        Assert.That(query.Text, Is.EqualTo("α_name")); Assert.That(query.IsKeyboardFocusWithin, Is.True);
        Assert.That(fixture.View.Search.MatchCase || fixture.View.Search.MatchWholeWordOnly, Is.False);
        Assert.That(query.SelectionStart, Is.Zero); Assert.That(query.SelectionEnd, Is.EqualTo("α_name".Length));
        Click(Find<CheckBox>(dialog, "DiffViewerFindMatchCase"));
        Press(dialog, PhysicalKey.Escape); Assert.That(dialog.IsVisible, Is.False);
        Assert.That(fixture.View.Editor.TextArea.IsKeyboardFocusWithin, Is.True, "Closing the modeless Find must focus the original reader without a test-side focus call.");
        fixture.View.Select(start + 2, 0); PressFind(fixture);
        Assert.That(FindWindow(fixture), Is.SameAs(dialog));
        Assert.That(query.Text, Is.EqualTo("α_name")); Assert.That(fixture.View.Search.MatchCase, Is.True);
        Assert.That(dialog.GetVisualDescendants().OfType<Button>().Select(button => button.Name), Does.Not.Contain("Replace"));
        Assert.That(fixture.View.Editor.IsReadOnly, Is.True);
        Press(dialog, PhysicalKey.Escape);
        string text = fixture.View.Text;
        fixture.View.Editor.TextArea.Focus(); fixture.Window.KeyTextInput("SHOULD NOT BE INSERTED"); Pump(fixture.Window);
        Assert.That(fixture.View.Text, Is.EqualTo(text));
    }

    [AvaloniaTest]
    public void F3AndShiftF3UseTheOriginalCrLfOffsetsAndLoopInBothDirections()
    {
        using Fixture fixture = new("line odd\r\nline even\r\nline odd");
        fixture.View.Search.Query = "odd"; fixture.View.Select(0, 0);
        foreach (int offset in new[] { 5, 26, 5 })
        {
            PressEditor(fixture, PhysicalKey.F3);
            AssertMatch(fixture, offset, 3);
        }
        fixture.View.Select(0, 0);
        foreach (int offset in new[] { 26, 5, 26 })
        {
            PressEditor(fixture, PhysicalKey.F3, RawInputModifiers.Shift);
            AssertMatch(fixture, offset, 3);
        }
        fixture.View.Select(fixture.View.Text.Length, 0);
        Assert.That(fixture.View.Search.FindNext()!.LoopedAround, Is.True);
    }

    [AvaloniaTest]
    public void CaseWholeWordAndUnderscoreFollowTheOriginalSearcherRatherThanRegex()
    {
        using Fixture fixture = new("name_suffix name NAME xname name1 λname λ_name name- λ🙂 λ🙂");
        DiffViewerSearch search = fixture.View.Search;
        search.Query = "name"; search.MatchWholeWordOnly = true;
        int[] expected = [12, 17, fixture.View.Text.IndexOf("name-", StringComparison.Ordinal)];
        foreach (int offset in expected) { Assert.That(search.FindNext()!.Offset, Is.EqualTo(offset)); }
        Assert.That(search.FindNext()!.Offset, Is.EqualTo(expected[0]));
        search.MatchCase = true; fixture.View.Select(0, 0);
        Assert.That(search.FindNext()!.Offset, Is.EqualTo(12));
        Assert.That(search.FindNext()!.Offset, Is.EqualTo(expected[2]));
        search.MatchWholeWordOnly = false; fixture.View.Select(0, 0);
        Assert.That(search.FindNext()!.Offset, Is.Zero);
        search.Query = "λ🙂"; fixture.View.Select(0, 0);
        DiffViewerSearchMatch unicode = search.FindNext()!;
        Assert.That(unicode.Length, Is.EqualTo(3)); Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo("λ🙂"));
    }

    [AvaloniaTest]
    public void MultilineSelectionBoundsSearchAndF3OutsideItDiscardsOnlyTheRegion()
    {
        using Fixture fixture = new("line odd\r\nline even\r\nline odd\r\nline after");
        fixture.View.Search.Query = "line";
        fixture.View.Select(10, 16); PressFind(fixture);
        Window dialog = FindWindow(fixture);
        Assert.That(fixture.View.Search.ScanRegion, Is.EqualTo(new DiffViewerSearchMatch(10, 16)));
        Assert.That(dialog.Title, Does.Contain("selection only"));
        foreach (int offset in new[] { 10, 21, 10 })
        { Click(Find<Button>(dialog, "DiffViewerFindNext")); AssertMatch(fixture, offset, 4); }
        Click(Find<Button>(dialog, "DiffViewerFindPrevious")); AssertMatch(fixture, 21, 4);
        fixture.View.Editor.CaretOffset = 31;
        PressEditor(fixture, PhysicalKey.F3);
        Assert.That(fixture.View.Search.ScanRegion, Is.Null);
        Assert.That(fixture.View.Search.Query, Is.EqualTo("line")); AssertMatch(fixture, 31, 4);
        Press(dialog, PhysicalKey.Escape);
        Assert.That(fixture.View.Search.ScanRegion, Is.Null);
    }

    [AvaloniaTest]
    public void SelectionOccurrencesOverlapIgnoreCaseAndAltArrowsPreserveActualSelection()
    {
        using Fixture fixture = new("aaAAa\r\nAa end");
        fixture.View.Select(1, 2);
        Assert.That(fixture.View.Search.Occurrences.Select(match => match.Offset), Is.EqualTo(new[] { 0, 1, 2, 3, 7 }));
        fixture.View.Editor.CaretOffset = 1;
        int[] offsets = [2, 3, 7, 7];
        foreach (int offset in offsets)
        {
            PressEditor(fixture, PhysicalKey.ArrowRight, RawInputModifiers.Alt);
            Assert.That(fixture.View.CaretOffset, Is.EqualTo(offset));
            Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo("aA"));
            Assert.That((fixture.View.SelectionStart, fixture.View.SelectionLength), Is.EqualTo((1, 2)));
        }
        foreach (int offset in new[] { 3, 2, 1, 0, 0 })
        {
            PressEditor(fixture, PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
            Assert.That(fixture.View.CaretOffset, Is.EqualTo(offset));
            Assert.That((fixture.Source.SelectionStart, fixture.Source.SelectionEnd), Is.EqualTo((1, 3)));
        }
        for (int cycle = 0; cycle < 3; cycle++) { Pump(fixture.Window); }
        Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo("aA"));
        Assert.That(fixture.View.CaretOffset, Is.Zero, "Deferred editor validation must preserve the independent caret and selection.");
        fixture.View.Select(5, 0); Assert.That(fixture.View.Search.Occurrences, Is.Empty);
    }

    [AvaloniaTest]
    public void RegionSearchIncludesItsFirstAndLastCharactersAndKeepsTheCaretAtTheEndInside()
    {
        using Fixture fixture = new("outside needle\nneedle\nneedle\noutside needle");
        const string regionText = "needle\nneedle";
        int start = fixture.View.Text.IndexOf(regionText, StringComparison.Ordinal), end = start + regionText.Length;
        fixture.View.Search.Query = "needle";
        fixture.View.Select(start, regionText.Length); PressFind(fixture); Window dialog = FindWindow(fixture);
        DiffViewerSearchMatch expectedRegion = new(start, regionText.Length);
        foreach (int offset in new[] { start, end - 6, start })
        {
            PressEditor(fixture, PhysicalKey.F3); AssertMatch(fixture, offset, 6);
            Assert.That(fixture.View.Search.ScanRegion, Is.EqualTo(expectedRegion));
        }
        Assert.That(fixture.View.Search.HighlightAll(), Is.EqualTo(2));
        Assert.That(fixture.View.Search.Highlights.Select(match => match.Offset), Is.EqualTo(new[] { start, end - 6 }));
        Assert.That(fixture.View.Search.Highlights.Last().Offset + fixture.View.Search.Highlights.Last().Length, Is.EqualTo(end));
        Press(dialog, PhysicalKey.Escape);
    }

    [AvaloniaTest]
    public void ARegionWithOnlyATrailingUnicodeMatchCanFindAndHighlightItWithoutSearchingOutside()
    {
        using Fixture fixture = new("before\nx\nλ🙂after\noutside λ🙂after");
        const string region = "x\nλ🙂after", query = "λ🙂after";
        int start = fixture.View.Text.IndexOf(region, StringComparison.Ordinal);
        fixture.View.Search.Query = query; fixture.View.Select(start, region.Length); PressFind(fixture);
        Assert.That(fixture.View.Search.HighlightAll(), Is.EqualTo(1));
        Assert.That(fixture.View.Search.Highlights.Single(), Is.EqualTo(new DiffViewerSearchMatch(start + 2, query.Length)));
        PressEditor(fixture, PhysicalKey.F3); AssertMatch(fixture, start + 2, query.Length);
        Assert.That(fixture.View.Search.ScanRegion, Is.EqualTo(new DiffViewerSearchMatch(start, region.Length)));
        Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo(query));
    }

    [AvaloniaTest]
    public void BackwardsFromAnInitialRegionFindsItsFinalMatchThenStepsPastTheCurrentResultAndWraps()
    {
        using Fixture fixture = new("last\nmiddle\nlast");
        fixture.View.Search.Query = "last"; fixture.View.Select(0, fixture.View.Text.Length); PressFind(fixture);
        DiffViewerSearchMatch region = new(0, fixture.View.Text.Length);
        foreach (int offset in new[] { 12, 0, 12, 0 })
        {
            PressEditor(fixture, PhysicalKey.F3, RawInputModifiers.Shift); AssertMatch(fixture, offset, 4);
            Assert.That(fixture.View.Search.ScanRegion, Is.EqualTo(region));
        }
        fixture.View.Search.ClearScanRegion();
    }

    [AvaloniaTest]
    public void WordRulesKeepTheOriginalUtf16CombiningMarkUnderscoreAndInvariantCaseSemantics()
    {
        using Fixture fixture = new("e\u0301 e_x e\u0301x 𐐨_foo 𐐀_foo");
        fixture.View.Search.MatchWholeWordOnly = true; fixture.View.Search.Query = "e";
        Assert.That(fixture.View.Search.FindNext()!.Offset, Is.Zero);
        Assert.That(fixture.View.Search.FindNext()!.Offset, Is.EqualTo(7), "Combining marks retain the original non-word-char rule; underscore remains a word char.");
        fixture.View.Search.Query = "e\u0301"; fixture.View.Select(0, 0);
        Assert.That(fixture.View.Search.FindNext()!.Length, Is.EqualTo(2));
        Assert.That(fixture.View.Search.FindNext()!.Offset, Is.EqualTo(7));
        fixture.View.Search.Query = "é";
        Assert.That(fixture.View.Search.FindNext(), Is.Null, "The original searcher does not normalize composed and decomposed text.");
        fixture.View.Search.MatchWholeWordOnly = false; fixture.View.Search.Query = "𐐨"; fixture.View.Select(0, 0);
        DiffViewerSearchMatch first = fixture.View.Search.FindNext()!, second = fixture.View.Search.FindNext()!;
        Assert.That(first.Length, Is.EqualTo(2)); Assert.That(second.Length, Is.EqualTo(2));
        Assert.That(fixture.View.Text.Substring(first.Offset, first.Length), Is.EqualTo("𐐨"));
        Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo("𐐀"));
    }

    [AvaloniaTest]
    public async Task WhitespaceDoesNotCreateOccurrencesAndOnlyForwardF3CanOpenTheDiffTool()
    {
        using Fixture fixture = new("word  \r\n word");
        fixture.View.Select(4, 4);
        Assert.That(fixture.View.Search.Occurrences, Is.Empty);
        fixture.View.Search.Query = ""; int calls = 0;
        fixture.View.Search.OpenDiffToolRequested = () => { calls++; return Task.CompletedTask; };
        PressEditor(fixture, PhysicalKey.F3);
        await fixture.View.Search.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(calls, Is.EqualTo(1));
        PressEditor(fixture, PhysicalKey.F3, RawInputModifiers.Shift);
        Window message = fixture.Window.OwnedWindows.Single(window => window.Name == "DiffViewerSearchInformation" && window.IsVisible);
        Assert.That(calls, Is.EqualTo(1)); message.Close();
        await fixture.View.Search.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaTest]
    public void SearchUsesDisplayedUtf16OffsetsForAnsiCombinedRangeAndPlainSource()
    {
        using Fixture fixture = new("");
        foreach ((DiffViewMode mode, string canonical, string display) in new[]
        {
            (DiffViewMode.Unified, "diff --git a/f b/f\n@@ -1 +1 @@\n-old\n+λ🙂 token\n", "diff --git a/f b/f\n@@ -1 +1 @@\n-old\n\u001b[32m+λ🙂 token\u001b[m\n"),
            (DiffViewMode.Combined, "diff --cc f\n@@@ -1 -1 +1 @@@\n++λ🙂 token\n", "diff --cc f\n@@@ -1 -1 +1 @@@\n\u001b[32m++λ🙂 token\u001b[m\n"),
            (DiffViewMode.RangeDiff, "1: aaaaa ! 1: bbbbb λ🙂 token\n", "\u001b[33m1: aaaaa ! 1: bbbbb λ🙂 token\u001b[m\n"),
            (DiffViewMode.PlainText, "λ🙂 token\n", "λ🙂 token\n")
        })
        {
            fixture.View.Mode = mode; fixture.View.DocumentIdentity = mode.ToString();
            fixture.View.ShowDocument(new(canonical, display, GitDiffAppearance.Patch, false) { UseGitColoring = true });
            fixture.View.Search.Query = "λ🙂"; fixture.View.Select(0, 0);
            DiffViewerSearchMatch match = fixture.View.Search.FindNext()!;
            Assert.That(match.Offset, Is.EqualTo(fixture.View.Text.IndexOf("λ🙂", StringComparison.Ordinal)));
            Assert.That(match.Length, Is.EqualTo(3)); Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo("λ🙂"));
            Assert.That(fixture.View.Text.Contains('\u001b'), Is.False, "The actual ESC character must be absent; culture-aware substring matching can treat it as ignorable.");
            Assert.That(fixture.View.CanonicalText, Is.EqualTo(canonical));
        }
    }

    [AvaloniaTest]
    public void LoadingInvalidatesRegionsAndMarkersButKeepsTheQueryAndReadingPosition()
    {
        using Fixture fixture = new("target\r\nsecond target\r\nlast target");
        fixture.View.DocumentIdentity = "repo/file/HEAD-worktree"; fixture.View.ShowDocument(Document(fixture.Source.Text!));
        fixture.View.Search.Query = "target"; fixture.View.Select(0, 21); PressFind(fixture);
        Window dialog = FindWindow(fixture); Assert.That(fixture.View.Search.ScanRegion, Is.Not.Null);
        fixture.View.Select(15, 6); int caret = fixture.View.CaretOffset;
        fixture.View.BeginDocumentLoad();
        Assert.That(dialog.IsVisible, Is.False); Assert.That(fixture.View.Search.ScanRegion, Is.Null);
        Assert.That(fixture.View.Search.Occurrences, Is.Empty); Assert.That(fixture.View.Search.FindNext(), Is.Null);
        Assert.That(fixture.View.Search.Query, Is.EqualTo("target")); Assert.That(fixture.View.CaretOffset, Is.EqualTo(caret));
        fixture.View.ShowDocument(Document(fixture.Source.Text!));
        Assert.That(fixture.View.Search.IsAvailable, Is.True); Assert.That(fixture.View.CaretOffset, Is.EqualTo(caret));
        Assert.That(fixture.View.Search.Occurrences, Has.Count.EqualTo(3));
        fixture.View.DocumentIdentity = "repo/other/HEAD-worktree";
        Assert.That(fixture.View.Search.Occurrences, Is.Empty); Assert.That(fixture.View.Search.Query, Is.EqualTo("target"));
        fixture.View.ShowDocument(Document("other target"));
        Assert.That(fixture.View.CaretOffset, Is.Zero); Assert.That(fixture.View.Search.Occurrences, Is.Empty);
    }

    [AvaloniaTest]
    public void DetachingOrClosingTheOwnerClosesItsCachedFindWindowWithoutLeakingIntoAnotherViewer()
    {
        using Fixture fixture = new("find me\nfind me");
        fixture.View.Select(0, 4); PressFind(fixture); Window first = FindWindow(fixture);
        fixture.Window.Content = null; Pump(fixture.Window);
        Assert.That(first.IsVisible, Is.False); Assert.That(fixture.Window.OwnedWindows, Does.Not.Contain(first));
        fixture.Window.Content = fixture.View; Pump(fixture.Window); PressFind(fixture);
        Window second = FindWindow(fixture); Assert.That(second, Is.Not.SameAs(first));
        using Fixture other = new("different"); other.View.Select(0, 0);
        Assert.That(other.View.Search.Query, Is.Empty); Assert.That(other.View.Search.Occurrences, Is.Empty);
        fixture.Window.Close(); Pump(fixture.Window);
        Assert.That(second.IsVisible, Is.False); Assert.That(fixture.View.Search.ScanRegion, Is.Null);
        fixture.View.Dispose(); Assert.That(fixture.View.Search.IsAvailable, Is.False);
    }

    [AvaloniaTest]
    public void OriginalFindMenuIconFrenchDialogAndSelectionColorsRenderInBothLocalThemes()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture; CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using Fixture fixture = new("needle here\nother needle here\nthird needle here\n");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                fixture.Window.RequestedThemeVariant = theme; Pump(fixture.Window); fixture.View.Select(0, 6); Pump(fixture.Window);
                IReadOnlyDictionary<string, string> colors = ClassicStyles.OriginalColors(theme == ThemeVariant.Dark);
                Color expected = Color.Parse(colors["HighlightAllOccurences"]);
                using (WriteableBitmap frame = Capture(fixture.Window, "diff-search-occurrences-" + theme + ".png"))
                { Assert.That(Count(frame, expected), Is.GreaterThan(50), "A nonselected occurrence must actually paint the original CSS background."); }
                MenuItem find = fixture.View.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "DiffViewerFind");
                Assert.That(find.InputGesture, Is.EqualTo(new KeyGesture(Key.F, KeyModifiers.Control)));
                Image icon = (Image)find.Icon!; Assert.That((icon.Width, icon.Height), Is.EqualTo((16d, 16d)));
                using MemoryStream actual = new(); ((Bitmap)icon.Source!).Save(actual);
                using Stream source = AssetLoader.Open(new Uri("avares://GitExtensions/Assets/Icons/Preview.png")); using Bitmap original = new(source);
                using MemoryStream expectedIcon = new(); original.Save(expectedIcon); Assert.That(actual.ToArray(), Is.EqualTo(expectedIcon.ToArray()));
                fixture.View.ContextMenu.Open(fixture.View); Pump(fixture.Window); Click(find);
                Window dialog = FindWindow(fixture); Pump(dialog);
                Assert.That(dialog.Title, Is.EqualTo("Trouver")); Assert.That(dialog.ActualThemeVariant, Is.EqualTo(theme));
                Assert.That(((AccessText)Find<CheckBox>(dialog, "DiffViewerFindMatchCase").Content!).Text, Is.EqualTo("Respecter la _casse"));
                Assert.That(((AccessText)Find<Button>(dialog, "DiffViewerFindNext").Content!).Text, Is.EqualTo("_Suivant"));
                Assert.That(Find<TextBox>(dialog, "DiffViewerFindQuery").Bounds.Width, Is.EqualTo(317));
                foreach (CheckBox checkbox in dialog.GetVisualDescendants().OfType<CheckBox>()) { Assert.That(checkbox.Bounds.Height, Is.EqualTo(24)); }
                foreach (string name in new[] { "DiffViewerFindCancel", "DiffViewerFindHighlightAll", "DiffViewerFindPrevious", "DiffViewerFindNext" })
                {
                    Button button = Find<Button>(dialog, name);
                    Rect bounds = new(button.TranslatePoint(default, dialog)!.Value, button.Bounds.Size);
                    Assert.That(bounds.X, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Y, Is.GreaterThanOrEqualTo(0));
                    Assert.That(bounds.Right, Is.LessThanOrEqualTo(dialog.ClientSize.Width));
                    Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(dialog.ClientSize.Height), "The original 419×169 client layout must not clip its bottom buttons.");
                    AccessText caption = (AccessText)button.Content!;
                    Assert.That(caption.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(caption.Bounds.Width), "The French caption must fit its rendered button.");
                }
                AccessText findLabel = (AccessText)Find<Label>(dialog, "DiffViewerFindLabel").Content!;
                Assert.That(findLabel.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(findLabel.Bounds.Width));
                using (WriteableBitmap frame = Capture(dialog, "diff-search-find-fr-" + theme + ".png"))
                { Assert.That(Count(frame, Color.Parse(colors["PanelBackground"])), Is.GreaterThan(500)); }
                Press(dialog, PhysicalKey.Escape);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public void HighlightAllHonorsSearchOptionsAndClosesTheRegionWhileLeavingPersistentMarkers()
    {
        using Fixture fixture = new("word wordy WORD\r\nword word\r\nword outside");
        fixture.View.Search.Query = "word"; fixture.View.Search.MatchCase = true; fixture.View.Search.MatchWholeWordOnly = true;
        fixture.View.Select(0, 28); PressFind(fixture); Window dialog = FindWindow(fixture);
        Click(Find<Button>(dialog, "DiffViewerFindHighlightAll"));
        Assert.That(dialog.IsVisible, Is.False); Assert.That(fixture.View.Search.ScanRegion, Is.Null);
        Assert.That(fixture.View.Search.Highlights.Select(match => match.Offset), Is.EqualTo(new[] { 0, 17, 22 }));
        fixture.View.Select(0, 4); Assert.That(fixture.View.Search.Highlights, Is.Empty);
        Assert.That(fixture.View.Search.Occurrences.Select(match => match.Offset), Does.Contain(11), "Selection highlighting is case-insensitive independently of the Find settings.");
        fixture.Source.Text = "new text"; Assert.That(fixture.View.Search.Highlights, Is.Empty); Assert.That(fixture.View.Search.ScanRegion, Is.Null);
    }

    [AvaloniaTest]
    public async Task RealGitPatchSearchScrollsToTheActualReadonlyLineWithoutTouchingIndexOrWorktree()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-search-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            GitRepository repository = await GitRepository.InitAsync(directory);
            await repository.RunAsync(["config", "user.name", "Search fixture"]); await repository.RunAsync(["config", "user.email", "fixture@example.invalid"]);
            const string path = ":λ[1]*.txt";
            string fullPath = System.IO.Path.Combine(directory, path);
            string original = string.Concat(Enumerable.Range(1, 240).Select(line => "line " + line + "\n"));
            await File.WriteAllTextAsync(fullPath, original); await repository.StageAsync([path]); await repository.CommitAsync(new GitCommitOptions("Initial"));
            await File.WriteAllTextAsync(fullPath, original.Replace("line 180\n", "target λ🙂 180\n", StringComparison.Ordinal));
            byte[] index = await File.ReadAllBytesAsync(System.IO.Path.Combine(directory, ".git/index"));
            byte[] worktree = await File.ReadAllBytesAsync(fullPath);
            using Fixture fixture = new(""); fixture.View.Mode = DiffViewMode.Unified;
            fixture.View.ShowDocument(await repository.GetDiffDocumentAsync(path, options: new(ShowEntireFile: true), displayOptions: new(false)));
            fixture.View.Search.Query = "target λ🙂"; PressEditor(fixture, PhysicalKey.F3); Pump(fixture.Window);
            Assert.That(fixture.View.Editor.SelectedText, Is.EqualTo("target λ🙂")); Assert.That(fixture.View.CurrentFileLine, Is.EqualTo(180));
            Assert.That(fixture.View.Editor.TextArea.TextView.ScrollOffset.Y, Is.GreaterThan(0));
            int row = fixture.View.Editor.Document.GetLineByOffset(fixture.View.SelectionStart).LineNumber;
            fixture.View.Editor.TextArea.TextView.EnsureVisualLines();
            Assert.That(fixture.View.Editor.TextArea.TextView.GetVisualLine(row), Is.Not.Null);
            Assert.That(await File.ReadAllBytesAsync(System.IO.Path.Combine(directory, ".git/index")), Is.EqualTo(index));
            Assert.That(await File.ReadAllBytesAsync(fullPath), Is.EqualTo(worktree));
            Assert.That(fixture.View.CanStagePatch, Is.True, "Reading must not replace or rewrite the canonical patch.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertMatch(Fixture fixture, int offset, int length)
    {
        Assert.That((fixture.View.SelectionStart, fixture.View.SelectionLength), Is.EqualTo((offset, length)));
        Assert.That(fixture.View.CaretOffset, Is.EqualTo(offset + length));
        Assert.That((fixture.Source.SelectionStart, fixture.Source.SelectionEnd), Is.EqualTo((offset, offset + length)));
    }
    private static void PressFind(Fixture fixture) => PressEditor(fixture, PhysicalKey.F, RawInputModifiers.Control);
    private static void PressEditor(Fixture fixture, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { fixture.View.Editor.TextArea.Focus(); Press(fixture.Window, key, modifiers); }
    private static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window is not Window { IsVisible: false }) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel root) { root.UpdateLayout(); Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
    private static T Find<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Window FindWindow(Fixture fixture) => fixture.Window.OwnedWindows.Single(window => window.Name == "DiffViewerFindDialog" && window.IsVisible);
    private static void Click(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control) ?? throw new AssertionException("No rendered root."); Pump(root);
        Assert.That(control.IsEffectivelyVisible && control.Bounds.Height > 0, Is.True);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point); root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump(root);
    }
    private static WriteableBitmap Capture(TopLevel root, string name)
    {
        Pump(root); WriteableBitmap bitmap = root.CaptureRenderedFrame() ?? throw new AssertionException("No rendered search frame.");
        string path = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, name); bitmap.Save(path); TestContext.AddTestAttachment(path); return bitmap;
    }
    private static int Count(WriteableBitmap bitmap, Color color)
    {
        using ILockedFramebuffer pixels = bitmap.Lock(); byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888; int count = 0;
        for (int y = 0; y < pixels.Size.Height; y++) { for (int x = 0; x < pixels.Size.Width; x++)
        { int index = y * pixels.RowBytes + x * 4; if (bytes[index + (bgra ? 2 : 0)] == color.R && bytes[index + 1] == color.G && bytes[index + (bgra ? 0 : 2)] == color.B) { count++; } } }
        return count;
    }
    private static GitDiffDocument Document(string text) => new(text, text, GitDiffAppearance.Patch, true) { IsLosslessPatch = true, PatchBytes = Encoding.UTF8.GetBytes(text) };
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-search-ui-" + Guid.NewGuid().ToString("N"));
        internal TextBox Source { get; } = new();
        internal DiffView View { get; }
        internal Window Window { get; }
        internal Fixture(string text)
        {
            Directory.CreateDirectory(_directory); Source.Text = text;
            View = new(Source, new DiffViewerPreferencesStore(_directory)) { Mode = DiffViewMode.PlainText };
            Window = new Window { Width = 720, Height = 320, Content = View };
            Window.Show(); Pump(Window);
        }
        public void Dispose()
        { View.Dispose(); Window.Close(); Pump(Window); Directory.Delete(_directory, recursive: true); }
    }
}
