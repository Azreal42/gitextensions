using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using RootFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

// Diagnostic admission: do not infer the minimum layout from a last frame captured
// immediately after changing Width/Height. Each case is independently observable.
[NonParallelizable]
public sealed class BrowseMinimumLayoutDiagnosticTests
{
    [AvaloniaTest]
    [TestCase("en", false, CommitInfoPosition.BelowList)]
    [TestCase("en", false, CommitInfoPosition.LeftwardFromList)]
    [TestCase("en", false, CommitInfoPosition.RightwardFromList)]
    [TestCase("fr", true, CommitInfoPosition.BelowList)]
    [TestCase("fr", true, CommitInfoPosition.LeftwardFromList)]
    [TestCase("fr", true, CommitInfoPosition.RightwardFromList)]
    public async Task FreshMinimumAndWideFramesKeepToolbarHistoryDetailsAndRealInputReachable(string language, bool dark, CommitInfoPosition position)
    {
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        Application application = Application.Current!;
        ThemeVariant? previousTheme = application.RequestedThemeVariant;
        using RootFixture root = await RootFixture.CreateAsync();
        // These assignments deliberately run in the test caller's ExecutionContext.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        application.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        new PreferencesStore().Save(new DesktopPreferences
        {
            Theme = dark ? "Dark" : "Light", Language = language, StartWithRecentWorkingDir = false,
            CheckForUpdates = false, ShowAuthorAvatarInCommitInfo = false, HistoryLimit = 100, MaxRevisionGraphCommits = 0
        });
        new HistoryViewPreferencesStore().Save(new() { ShowArtificialCommits = true, ShowAvatar = false, ShowStashes = false, ShowSessionCheckpoints = false });
        BrowserLayoutPreferencesStore layoutStore = new(); layoutStore.Save(new(position));
        MainWindow? main = null;
        try
        {
            await root.GitAsync("checkout", "-b", "feature/compact-layout");
            await File.WriteAllTextAsync(root.PathInRepository("feature.txt"), "feature content\n");
            await root.GitAsync("add", "--", "feature.txt"); await root.GitAsync("commit", "-m", "Feature layout change");
            await root.GitAsync("checkout", "main"); await root.ChangeAsync("main content\n", "Main layout change");
            await root.GitAsync("merge", "--no-ff", "feature/compact-layout", "-m", "Merge layout feature");
            await root.GitAsync("update-ref", "refs/remotes/origin/main", "HEAD");
            main = new() { Width = 1440, Height = 900, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            main.Show(); await main.OpenRepositoryAsync(root.Repository.Path); await Work(main);
            ListBox rows = Field<ListBox>(main, "RevisionHistory");
            GitCommit head = rows.Items.OfType<GitCommit>().Single(commit => commit.Parents.Count == 2);
            rows.ScrollIntoView(head); await Settle(main, new(1440, 900));
            PushProcessTestDriver.Pointer((Control)rows.ContainerFromItem(head)!); await Work(main);
            object source = rows.ItemsSource!; object[] objects = rows.Items.Cast<object>().ToArray();
            Capture wide = await CaptureAt(main, language, position, "wide-before", new(1440, 900));
            object? selectedBeforeResize = rows.SelectedItem;
            int anchorBeforeResize = rows.Selection.AnchorIndex;
            main.Width = 860; main.Height = 620;
            Capture minimum = await CaptureAt(main, language, position, "minimum", new(860, 620));
            HeaderObservation minimumHeader = await ReadAndScrollMinimumHeader(main, language, position);
            bool retainedFocus = rows.IsKeyboardFocusWithin;
            object? retainedSelection = rows.SelectedItem;
            int retainedAnchor = rows.Selection.AnchorIndex;
            CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(main, "RepositoryToolbar");
            // This diagnostic uses the actual window allocation, never toolbar.Width/Place.
            PushProcessTestDriver.Pointer(toolbar.Overflow);
            await Settle(main, new(860, 620));
            await CaptureAt(main, language, position, "overflow-open", new(860, 620));
            Assert.That(toolbar.OverflowFlyout.IsOpen, Is.True, "One real click must open the allocated overflow button.");
            MenuItem[] menu = toolbar.OverflowFlyout.ItemsSource!.OfType<MenuItem>().ToArray();
            Assert.That(menu, Is.Not.Empty);
            Assert.That(menu.Any(item => item.IsEffectivelyVisible && item.IsEffectivelyEnabled), Is.True);
            Press(main, PhysicalKey.Escape); await Settle(main, new(860, 620));
            Assert.That(toolbar.OverflowFlyout.IsOpen, Is.False, "Escape must close the real popup.");
            rows.ScrollIntoView(head); await Settle(main, new(860, 620));
            PushProcessTestDriver.Pointer((Control)rows.ContainerFromItem(head)!); await Work(main);
            int nextIndex = rows.SelectedIndex + 1; object next = rows.Items[nextIndex]!;
            Press(main, PhysicalKey.ArrowDown); await Work(main);
            bool firstArrow = ReferenceEquals(rows.SelectedItem, next) && rows.IsKeyboardFocusWithin;
            object? afterArrow = rows.SelectedItem;
            main.Width = 1440; main.Height = 900;
            Capture restored = await CaptureAt(main, language, position, "wide-restored", new(1440, 900));
            Assert.Multiple(() =>
            {
                AssertGeometry(wide, position); AssertGeometry(minimum, position); AssertGeometry(restored, position);
                AssertReadableHeader(minimumHeader, head, position);
                Assert.That(Field<ScrollViewer>(main, "CommitInformationHeaderScroll"), Is.SameAs(minimumHeader.Scroll));
                Assert.That(Field<SelectableTextBlock>(main, "CommitInformationAuthor"), Is.SameAs(minimumHeader.Author));
                Assert.That(Field<SelectableTextBlock>(main, "CommitInformationCommithash"), Is.SameAs(minimumHeader.Hash));
                Assert.That(minimumHeader.Hash.Text, Is.EqualTo(((GitCommit)afterArrow!).Hash), "The same header renders the newly selected commit after the existing first-arrow action.");
                Assert.That(main.HistoryLoadingCompletion.IsCompleted && main.RepositoryMutationCompletion.IsCompleted && main.CommitInformationCompletion.IsCompleted, Is.True);
                Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo(language));
                Assert.That(main.ActualThemeVariant, Is.EqualTo(dark ? ThemeVariant.Dark : ThemeVariant.Light));
                Assert.That(TabCaption(Field<TabItem>(main, "DiffTab")), Is.EqualTo(dark ? "Différenciation" : "Diff"));
                Assert.That(retainedFocus, Is.True, "A window resize must retain native history focus.");
                Assert.That(retainedSelection, Is.SameAs(selectedBeforeResize)); Assert.That(retainedAnchor, Is.EqualTo(anchorBeforeResize));
                Assert.That(firstArrow, Is.True, "The first Down after a real single click navigates one visual row and keeps focus.");
                Assert.That(rows.IsKeyboardFocusWithin, Is.True); Assert.That(rows.SelectedItem, Is.SameAs(afterArrow));
                Assert.That(rows.ItemsSource, Is.SameAs(source)); Assert.That(rows.Items.Count, Is.EqualTo(objects.Length));
                Assert.That(rows.Items.Cast<object>().Zip(objects).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
                Assert.That(layoutStore.Load().CommitInfoPosition, Is.EqualTo(position), "Narrowing does not replace the saved placement.");
                Assert.That(main.LastException, Is.Null);
            });
        }
        catch
        {
            if (main is { IsVisible: true }) { await CaptureAt(main, language, position, "failure", main.ClientSize); }
            throw;
        }
        finally
        {
            if (main is not null)
            {
                main.Close(); await Until(main, () => !main.IsVisible && main.WindowWorkCompletion.IsCompleted);
                await main.WindowWorkCompletion; await main.CommitInformationCompletion;
            }
            application.RequestedThemeVariant = previousTheme; CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [AvaloniaTest]
    [TestCase("en", false, CommitInfoPosition.LeftwardFromList)]
    [TestCase("en", false, CommitInfoPosition.RightwardFromList)]
    [TestCase("fr", true, CommitInfoPosition.LeftwardFromList)]
    [TestCase("fr", true, CommitInfoPosition.RightwardFromList)]
    public async Task AUserDraggedSideWidthSurvivesNarrowingAndBelowThenSideBeforeReturningWide(string language, bool dark, CommitInfoPosition position)
    {
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        Application application = Application.Current!;
        ThemeVariant? previousTheme = application.RequestedThemeVariant;
        using RootFixture root = await RootFixture.CreateAsync();
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        application.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        new PreferencesStore().Save(new DesktopPreferences
        {
            Theme = dark ? "Dark" : "Light", Language = language, StartWithRecentWorkingDir = false,
            CheckForUpdates = false, ShowAuthorAvatarInCommitInfo = false, HistoryLimit = 100, MaxRevisionGraphCommits = 0
        });
        new HistoryViewPreferencesStore().Save(new() { ShowArtificialCommits = true, ShowAvatar = false, ShowStashes = false, ShowSessionCheckpoints = false });
        BrowserLayoutPreferencesStore layoutStore = new(); layoutStore.Save(new(position));
        MainWindow? main = null;
        try
        {
            await root.ChangeAsync("second width content\n", "Second width change");
            await root.ChangeAsync("third width content\n", "Third width change");
            string material = await ReadMaterial();
            main = new() { Width = 1440, Height = 900, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            main.Show(); await main.OpenRepositoryAsync(root.Repository.Path); await Work(main);
            ListBox rows = Field<ListBox>(main, "RevisionHistory");
            GitCommit selected = rows.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Third width change");
            rows.ScrollIntoView(selected); await Settle(main, new(1440, 900));
            PushProcessTestDriver.Pointer((Control)rows.ContainerFromItem(selected)!); await Work(main);
            object source = rows.ItemsSource!; object[] objects = rows.Items.Cast<object>().ToArray();
            Capture initial = await CaptureAt(main, language, position, "width-initial", new(1440, 900));
            GridSplitter splitter = Field<GridSplitter>(main, "CommitInfoSplitter");
            // Drag has no shared driver API. Guard its real target in place;
            // normal clicks below reuse Pointer. Do not prepend a second click
            // which could turn this drag into a framework double-tap.
            Point start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), main)!.Value;
            Visual? hit = main.InputHitTest(start) as Visual;
            Assert.That(splitter.IsEffectivelyVisible && splitter.IsEffectivelyEnabled
                && new Rect(default, main.ClientSize).Contains(start)
                && hit?.GetSelfAndVisualAncestors().Contains(splitter) == true, Is.True, "The actual side splitter must receive the one drag.");
            Vector delta = new(position == CommitInfoPosition.RightwardFromList ? -45 : 45, 0);
            main.MouseMove(start); main.MouseDown(start, MouseButton.Left); main.MouseMove(start + delta); main.MouseUp(start + delta, MouseButton.Left);
            await Work(main);
            Capture requested = await CaptureAt(main, language, position, "width-requested-wide", new(1440, 900));
            rows.ScrollIntoView(selected); await Settle(main, new(1440, 900));
            PushProcessTestDriver.Pointer((Control)rows.ContainerFromItem(selected)!); await Work(main);
            int anchor = rows.Selection.AnchorIndex;
            GitCommandLog.Clear();
            main.Width = 860; main.Height = 620;
            Capture narrowed = await CaptureAt(main, language, position, "width-narrowed", new(860, 620));
            bool focusOnNarrowing = rows.IsKeyboardFocusWithin;
            object? selectionOnNarrowing = rows.SelectedItem;
            await Place(CommitInfoPosition.BelowList);
            Capture below = await CaptureAt(main, language, CommitInfoPosition.BelowList, "width-below-at-minimum", new(860, 620));
            bool belowPersisted = layoutStore.Load().CommitInfoPosition == CommitInfoPosition.BelowList;
            await Place(position);
            Capture sideAgain = await CaptureAt(main, language, position, "width-side-at-minimum", new(860, 620));
            // Observe before the next explicit user click, so that click cannot
            // conceal a changed selection or a replaced source during placement.
            object? selectionAfterPlacement = rows.SelectedItem;
            int anchorAfterPlacement = rows.Selection.AnchorIndex;
            bool sourceAfterPlacement = ReferenceEquals(rows.ItemsSource, source)
                && rows.Items.Count == objects.Length && rows.Items.Cast<object>().Zip(objects).All(pair => ReferenceEquals(pair.First, pair.Second));
            rows.ScrollIntoView(selected); await Settle(main, new(860, 620));
            PushProcessTestDriver.Pointer((Control)rows.ContainerFromItem(selected)!); await Work(main);
            main.Width = 1440; main.Height = 900;
            Capture restored = await CaptureAt(main, language, position, "width-restored-wide", new(1440, 900));
            bool focusOnReturn = rows.IsKeyboardFocusWithin;
            object next = rows.Items[rows.SelectedIndex + 1]!;
            Press(main, PhysicalKey.ArrowDown); await Work(main);
            bool firstArrow = ReferenceEquals(rows.SelectedItem, next) && rows.IsKeyboardFocusWithin;
            IReadOnlyList<GitCommandLogEntry> layoutReads = GitCommandLog.Snapshot();
            string afterMaterial = await ReadMaterial();
            TestContext.WriteLine($"H37 requested width: initial={initial.Information.Width}; user={requested.Information.Width}; narrow={narrowed.Information.Width}; below={below.Information.Width}; narrow-again={sideAgain.Information.Width}; restored={restored.Information.Width}");
            Assert.Multiple(() =>
            {
                foreach (Capture capture in new[] { initial, requested, narrowed, below, sideAgain, restored })
                {
                    Assert.That(capture.Stable, Is.True, capture.Stage);
                    Assert.That(capture.Client.Width, Is.EqualTo(capture.Expected.Width).Within(.5));
                    Assert.That(capture.Client.Height, Is.EqualTo(capture.Expected.Height).Within(.5));
                }
                Assert.That(requested.Information.Width, Is.EqualTo(initial.Information.Width + 45).Within(2), "The real wide splitter drag must actually enlarge the side panel.");
                Assert.That(narrowed.Information.Width, Is.LessThan(requested.Information.Width - 20), "This path must exercise a genuine temporary clamp, not two already-fitting widths.");
                Assert.That(restored.Information.Width, Is.EqualTo(requested.Information.Width).Within(2), "Returning wide must recover the pre-clamp user width, including after reparenting Below then back to the same side.");
                Assert.That(belowPersisted, Is.True); Assert.That(layoutStore.Load().CommitInfoPosition, Is.EqualTo(position));
                Assert.That(selectionOnNarrowing, Is.SameAs(selected)); Assert.That(selectionAfterPlacement, Is.SameAs(selected));
                Assert.That(anchorAfterPlacement, Is.EqualTo(anchor)); Assert.That(sourceAfterPlacement, Is.True);
                Assert.That(rows.ItemsSource, Is.SameAs(source));
                Assert.That(rows.Items.Cast<object>().Zip(objects).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True);
                Assert.That(focusOnNarrowing, Is.True); Assert.That(focusOnReturn, Is.True); Assert.That(firstArrow, Is.True);
                Assert.That(layoutReads.Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status"), "Placement and resize do not reload repository history/status; newly exposed pane reads remain allowed.");
                Assert.That(afterMaterial, Is.EqualTo(material), "Actual refs, index bytes and working file remain unchanged.");
                Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo(language));
                Assert.That(main.ActualThemeVariant, Is.EqualTo(dark ? ThemeVariant.Dark : ThemeVariant.Light));
                Assert.That(main.LastException, Is.Null);
            });

            async Task Place(CommitInfoPosition value)
            {
                Button arrow = Field<Button>(main, "CommitInfoPositionMenu");
                PushProcessTestDriver.Pointer(arrow); await Settle(main, main.ClientSize);
                MenuFlyout menu = (MenuFlyout)arrow.Flyout!;
                await CaptureAt(main, language, value, "width-position-menu", main.ClientSize);
                Assert.That(menu.IsOpen, Is.True);
                MenuItem choice = menu.ItemsSource!.OfType<MenuItem>().Single(item => Equals(item.Tag, value));
                PushProcessTestDriver.Pointer(choice); await Work(main); await Settle(main, main.ClientSize);
                Assert.That(menu.IsOpen, Is.False, "The real menu click closes the placement popup.");
            }
        }
        catch
        {
            if (main is { IsVisible: true }) { await CaptureAt(main, language, position, "width-failure", main.ClientSize); }
            throw;
        }
        finally
        {
            if (main is not null)
            {
                main.Close(); await Until(main, () => !main.IsVisible && main.WindowWorkCompletion.IsCompleted);
                await main.WindowWorkCompletion; await main.CommitInformationCompletion;
            }
            application.RequestedThemeVariant = previousTheme; CultureInfo.CurrentUICulture = previousCulture;
        }

        async Task<string> ReadMaterial()
        {
            string refs = (await root.GitAsync("show-ref")).StandardOutput;
            byte[] index = await File.ReadAllBytesAsync(Path.Combine(root.Repository.Path, ".git", "index"));
            return refs + "\nHEAD=" + await File.ReadAllTextAsync(Path.Combine(root.Repository.Path, ".git", "HEAD"))
                + "\nINDEX=" + Convert.ToHexString(index) + "\nWORKTREE=" + await File.ReadAllTextAsync(root.File);
        }
    }

    private sealed record HeaderObservation(ScrollViewer Scroll, SelectableTextBlock Author, SelectableTextBlock Hash,
        string AuthorText, string HashText, int AuthorLogicalLines, int AuthorRenderedLines, int HashRenderedLines,
        bool AuthorCollapsed, bool HashCollapsed, bool ScrollFocusable, double Extent, double Viewport,
        double InitialOffset, double FinalOffset, bool LastHashGlyphVisible, bool SameFocus, bool SameSelection,
        bool ReadersCompleted, int AdmittedGitReads);

    private static async Task<HeaderObservation> ReadAndScrollMinimumHeader(MainWindow main, string language, CommitInfoPosition position)
    {
        ScrollViewer header = Field<ScrollViewer>(main, "CommitInformationHeaderScroll");
        SelectableTextBlock author = Field<SelectableTextBlock>(main, "CommitInformationAuthor");
        SelectableTextBlock hash = Field<SelectableTextBlock>(main, "CommitInformationCommithash");
        ScrollContentPresenter viewport = header.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
        ListBox rows = Field<ListBox>(main, "RevisionHistory");
        object? focus = main.FocusManager?.GetFocusedElement(), selection = rows.SelectedItem;
        string authorText = author.Text ?? "", hashText = hash.Text ?? "";
        int logical = authorText.Replace("\r\n", "\n", StringComparison.Ordinal).Count(character => character == '\n') + 1;
        int authorLines = author.TextLayout.TextLines.Count, hashLines = hash.TextLayout.TextLines.Count;
        bool authorCollapsed = author.TextLayout.TextLines.Any(line => line.HasCollapsed), hashCollapsed = hash.TextLayout.TextLines.Any(line => line.HasCollapsed);
        double initialOffset = header.Offset.X, extent = header.Extent.Width, visible = header.Viewport.Width;
        long lastRead = GitCommandLog.Snapshot().Select(entry => entry.Id).DefaultIfEmpty().Max();
        bool readersCompleted = main.HistoryLoadingCompletion.IsCompleted && main.RepositoryMutationCompletion.IsCompleted && main.CommitInformationCompletion.IsCompleted;
        if (position != CommitInfoPosition.BelowList)
        {
            await CaptureAt(main, language, position, "header-before-horizontal-wheel", main.ClientSize);
            Point wheel = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), main)!.Value;
            Visual? hit = main.InputHitTest(wheel) as Visual;
            Assert.That(header.IsEffectivelyVisible && header.IsEffectivelyEnabled && new Rect(default, main.ClientSize).Contains(wheel)
                && hit?.GetSelfAndVisualAncestors().Contains(header) == true, Is.True, "The real horizontal wheel must target this header viewport.");
            for (int step = 0; step < 80 && !(header.Offset.X > initialOffset && LastGlyphIsVisible()); step++)
            {
                main.MouseMove(wheel); main.MouseWheel(wheel, new Vector(-1, 0), RawInputModifiers.None);
                await Settle(main, main.ClientSize);
            }
            await CaptureAt(main, language, position, "header-after-horizontal-wheel", main.ClientSize);
        }
        bool lastVisible = LastGlyphIsVisible();
        return new(header, author, hash, authorText, hashText, logical, authorLines, hashLines, authorCollapsed, hashCollapsed,
            header.Focusable, extent, visible, initialOffset, header.Offset.X, lastVisible,
            ReferenceEquals(main.FocusManager?.GetFocusedElement(), focus), ReferenceEquals(rows.SelectedItem, selection),
            readersCompleted && main.HistoryLoadingCompletion.IsCompleted && main.RepositoryMutationCompletion.IsCompleted && main.CommitInformationCompletion.IsCompleted,
            GitCommandLog.Snapshot().Count(entry => entry.Id > lastRead));

        bool LastGlyphIsVisible()
        {
            if (hashText.Length == 0) { return false; }
            Rect glyph = hash.TextLayout.HitTestTextPosition(hashText.Length - 1);
            Point origin = hash.TranslatePoint(default, viewport)!.Value;
            Rect painted = new(origin + glyph.Position, glyph.Size);
            return Contains(new Rect(default, viewport.Bounds.Size), painted);
        }
    }

    private static void AssertReadableHeader(HeaderObservation observation, GitCommit selected, CommitInfoPosition position)
    {
        Assert.That(observation.AuthorText, Does.Contain(selected.AuthorName).And.Contain(selected.AuthorEmail));
        Assert.That(observation.AuthorRenderedLines, Is.EqualTo(observation.AuthorLogicalLines), "Author name/email retain only their explicit line breaks, rather than wrapping into a character column.");
        Assert.That(observation.HashText, Is.EqualTo(selected.Hash), "The actual header retains the complete Git object ID.");
        Assert.That(observation.HashRenderedLines, Is.EqualTo(1));
        Assert.That(observation.AuthorCollapsed, Is.False); Assert.That(observation.HashCollapsed, Is.False);
        Assert.That(observation.ScrollFocusable, Is.False, "The header viewport adds no focus stop competing with the commit list.");
        Assert.That(observation.SameFocus, Is.True); Assert.That(observation.SameSelection, Is.True);
        Assert.That(observation.ReadersCompleted, Is.True); Assert.That(observation.AdmittedGitReads, Is.Zero, "Horizontal scrolling is presentation only.");
        if (position != CommitInfoPosition.BelowList)
        {
            Assert.That(observation.Extent, Is.GreaterThan(observation.Viewport + 1), "This narrow side actually requires horizontal scrolling.");
            Assert.That(observation.FinalOffset, Is.GreaterThan(observation.InitialOffset));
            Assert.That(observation.LastHashGlyphVisible, Is.True, "A real horizontal wheel must expose the final character of the complete hash inside this viewport.");
        }
    }

    private sealed record Capture(string Stage, bool Stable, Size Expected, Size Client, Rect Root, Rect Toolbar, Rect Path,
        Rect RevisionPanels, Rect History, Rect Information, Rect Details, Rect DetailsSplitter, Rect InfoSplitter,
        double RenderScaling, PixelSize? Frame, bool OverflowVisible, Rect Overflow, Rect[] Inline, Rect[] Subjects);

    private static async Task<Capture> CaptureAt(MainWindow main, string language, CommitInfoPosition position, string stage, Size expected)
    {
        bool stable = await Settle(main, expected);
        CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(main, "RepositoryToolbar");
        ListBox rows = Field<ListBox>(main, "RevisionHistory");
        using WriteableBitmap? frame = main.GetLastRenderedFrame();
        Capture capture = new(stage, stable, expected, main.ClientSize, In(main, (Control)main.Content!), In(main, toolbar), In(main, Field<TextBox>(main, "RepositoryPath")),
            In(main, Field<Grid>(main, "RevisionPanels")), In(main, rows), In(main, Field<CommitInformationView>(main, "CommitInformation")),
            In(main, Field<TabControl>(main, "DetailsTabs")), In(main, Field<GridSplitter>(main, "DetailsSplitter")), In(main, Field<GridSplitter>(main, "CommitInfoSplitter")),
            main.RenderScaling, frame?.PixelSize, toolbar.Overflow.IsEffectivelyVisible, In(main, toolbar.Overflow),
            toolbar.InlineItems.Where(item => item.IsEffectivelyVisible).Select(item => In(main, item)).ToArray(),
            rows.GetVisualDescendants().OfType<TextBlock>().Where(item => item.Name == "RevisionSubject").Select(item => In(main, item)).ToArray());
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory, "h37-layout-" + language + "-" + position + "-" + stage + "-" + Guid.NewGuid().ToString("N"));
        string json = stem + ".json";
        object? focus = main.FocusManager?.GetFocusedElement();
        ScrollViewer? header = main.GetVisualDescendants().OfType<ScrollViewer>().SingleOrDefault(item => item.Name == "CommitInformationHeaderScroll");
        SelectableTextBlock? author = main.GetVisualDescendants().OfType<SelectableTextBlock>().SingleOrDefault(item => item.Name == "CommitInformationAuthor");
        SelectableTextBlock? hash = main.GetVisualDescendants().OfType<SelectableTextBlock>().SingleOrDefault(item => item.Name == "CommitInformationCommithash");
        string snapshot = JsonSerializer.Serialize(new
        {
            capture, Culture = CultureInfo.CurrentUICulture.Name, Theme = main.ActualThemeVariant.ToString(),
            Window = new { main.Width, main.Height, main.Bounds, main.MinWidth, main.MinHeight },
            Work = new { main.IsBusy, History = main.HistoryLoadingCompletion.IsCompleted, Foreground = main.RepositoryMutationCompletion.IsCompleted, CommitInfo = main.CommitInformationCompletion.IsCompleted, Global = main.WindowWorkCompletion.IsCompleted },
            Selection = rows.SelectedItems!.Cast<object>().Select(Key).ToArray(), rows.SelectedIndex, Anchor = rows.Selection.AnchorIndex,
            Focus = focus?.GetType().Name + "/" + (focus as Control)?.Name,
            FocusAncestors = (focus as Visual)?.GetSelfAndVisualAncestors().Select(item => item.GetType().Name + "/" + (item as Control)?.Name).ToArray(),
            Path = new { Field<TextBox>(main, "RepositoryPath").Width, Field<TextBox>(main, "RepositoryPath").Bounds },
            Header = header is null ? null : new
            {
                Bounds = In(main, header), header.Extent, header.Viewport, header.Offset, header.Focusable,
                Author = author is null ? null : new { author.Text, Bounds = In(main, author), Lines = author.TextLayout.TextLines.Count, Collapsed = author.TextLayout.TextLines.Any(line => line.HasCollapsed) },
                Hash = hash is null ? null : new { hash.Text, Bounds = In(main, hash), Lines = hash.TextLayout.TextLines.Count, Collapsed = hash.TextLayout.TextLines.Any(line => line.HasCollapsed) }
            },
            Inline = toolbar.InlineItems.Select(item => new { item.Name, item.IsVisible, item.IsEffectivelyVisible, item.IsEffectivelyEnabled, Bounds = In(main, item), Desired = item.DesiredSize }).ToArray(),
            Overflow = toolbar.OverflowFlyout.ItemsSource!.Cast<Control>().Select(item => new { item.Name, item.IsEffectivelyVisible, item.IsEffectivelyEnabled }).ToArray(),
            Columns = rows.GetVisualDescendants().OfType<Control>().Where(item => item.Name is "RevisionMessage" or "RevisionSubject" or "RevisionAuthor" or "RevisionDate" or "RevisionHash")
                .Select(item => new { item.Name, Bounds = In(main, item), item.DesiredSize }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
        await File.WriteAllTextAsync(json, snapshot); TestContext.AddTestAttachment(json); TestContext.WriteLine("H37 layout diagnostic: " + json);
        if (frame is not null) { string image = stem + ".png"; frame.Save(image); TestContext.AddTestAttachment(image); }
        if (toolbar.OverflowFlyout.IsOpen)
        {
            TopLevel? popup = toolbar.OverflowFlyout.ItemsSource!.OfType<Control>().Select(TopLevel.GetTopLevel).FirstOrDefault(top => top is not null && top != main);
            if (popup is not null)
            {
                using WriteableBitmap? popupFrame = popup.GetLastRenderedFrame();
                if (popupFrame is not null) { string image = stem + "-popup.png"; popupFrame.Save(image); TestContext.AddTestAttachment(image); }
            }
        }
        return capture;
    }

    private static void AssertGeometry(Capture c, CommitInfoPosition position)
    {
        Rect client = new(default, c.Client);
        Assert.That(c.Stable, Is.True, c.Stage + ": layout and fresh render did not settle.");
        Assert.That(c.Client.Width, Is.EqualTo(c.Expected.Width).Within(.5)); Assert.That(c.Client.Height, Is.EqualTo(c.Expected.Height).Within(.5));
        Assert.That(c.Root.Width, Is.EqualTo(c.Client.Width).Within(.5)); Assert.That(c.Root.Height, Is.EqualTo(c.Client.Height).Within(.5));
        Assert.That(c.Frame, Is.Not.Null);
        Assert.That(c.Frame?.Width, Is.EqualTo((int)Math.Round(c.Client.Width * c.RenderScaling)));
        Assert.That(c.Frame?.Height, Is.EqualTo((int)Math.Round(c.Client.Height * c.RenderScaling)));
        foreach ((string name, Rect bounds) in new[] { ("root", c.Root), ("toolbar", c.Toolbar), ("path", c.Path), ("history", c.History), ("details", c.Details), ("details splitter", c.DetailsSplitter), ("information", c.Information) })
        {
            Assert.That(bounds.Width, Is.GreaterThan(0), c.Stage + ": " + name + " width");
            Assert.That(bounds.Height, Is.GreaterThan(0), c.Stage + ": " + name + " height");
            Assert.That(Contains(client, bounds), Is.True, c.Stage + ": " + name + " must stay inside the client, " + bounds);
        }
        Assert.That(c.History.Height, Is.GreaterThan(20), c.Stage + ": more than one actual revision row remains available.");
        Assert.That(c.Details.Height, Is.GreaterThan(20), c.Stage + ": details remain a separately visible panel.");
        Assert.That(c.History.Bottom, Is.LessThanOrEqualTo(c.Details.Y + 1));
        Assert.That(c.Toolbar.Width, Is.EqualTo(c.Client.Width).Within(1));
        Assert.That(c.Subjects, Is.Not.Empty, c.Stage + ": real Git subjects are materialized.");
        foreach (Rect subject in c.Subjects)
        {
            Assert.That(subject.Width, Is.GreaterThan(0), c.Stage + ": the actual revision subject must retain visible width.");
            Assert.That(subject.Right, Is.GreaterThan(c.History.X));
            Assert.That(subject.X, Is.LessThan(c.History.Right));
        }
        Assert.That(c.Inline, Is.Not.Empty);
        foreach (Rect item in c.Inline) { Assert.That(Contains(c.Toolbar, item), Is.True, c.Stage + ": inline toolbar item " + item); }
        Assert.That(c.Inline.Select(item => item.Y).Distinct().Count(), Is.EqualTo(1), c.Stage + ": the original toolbar remains one row.");
        if (c.Expected.Width == 860)
        {
            Assert.That(c.OverflowVisible, Is.True); Assert.That(Contains(c.Toolbar, c.Overflow), Is.True);
            Assert.That(c.Inline.All(item => item.Right <= c.Overflow.X + 1), Is.True, "The overflow button must not cover the last inline command.");
        }
        if (position == CommitInfoPosition.BelowList) { Assert.That(c.Information.Y, Is.GreaterThanOrEqualTo(c.History.Bottom)); }
        else
        {
            Assert.That(position == CommitInfoPosition.RightwardFromList ? c.History.Right <= c.Information.X + 1 : c.Information.Right <= c.History.X + 1, Is.True);
        }
    }

    private static async Task<bool> Settle(MainWindow main, Size expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15); string? previous = null; int same = 0;
        do
        {
            await Dispatcher.UIThread.InvokeAsync(main.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Dispatcher.UIThread.RunJobs(); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); Dispatcher.UIThread.RunJobs();
            using WriteableBitmap? frame = main.GetLastRenderedFrame();
            string value = string.Join("|", main.ClientSize, ((Control)main.Content!).Bounds,
                Field<Control>(main, "RepositoryToolbar").Bounds, Field<Control>(main, "RepositoryPath").Bounds,
                Field<Control>(main, "RevisionPanels").Bounds, Field<Control>(main, "DetailsTabs").Bounds, frame?.PixelSize);
            bool current = Math.Abs(main.ClientSize.Width - expected.Width) < .5 && Math.Abs(main.ClientSize.Height - expected.Height) < .5
                && frame is not null && frame.PixelSize.Width == (int)Math.Round(main.ClientSize.Width * main.RenderScaling)
                && frame.PixelSize.Height == (int)Math.Round(main.ClientSize.Height * main.RenderScaling);
            same = current && value == previous ? same + 1 : 0; previous = value;
            if (same >= 3) { return true; }
            await Task.Delay(16);
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }
    private static async Task Work(MainWindow main)
    {
        await Until(main, () => !main.IsBusy && main.HistoryLoadingCompletion.IsCompleted && main.RepositoryMutationCompletion.IsCompleted && main.CommitInformationCompletion.IsCompleted);
        await main.HistoryLoadingCompletion; await main.RepositoryMutationCompletion; await main.CommitInformationCompletion;
    }
    private static async Task Until(MainWindow main, Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline)
        { Dispatcher.UIThread.RunJobs(); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(15); }
        Assert.That(done(), Is.True, "The diagnostic's actual repository/panel work did not drain.");
    }
    private static bool Contains(Rect outside, Rect inside) => inside.X >= outside.X - 1 && inside.Y >= outside.Y - 1 && inside.Right <= outside.Right + 1 && inside.Bottom <= outside.Bottom + 1;
    private static Rect In(MainWindow main, Control control) => new(control.TranslatePoint(default, main) ?? default, control.Bounds.Size);
    private static T Field<T>(MainWindow main, string name) where T : Control => main.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static TopLevel InputOwner(MainWindow main) => main.FocusManager?.GetFocusedElement() is Control focused ? TopLevel.GetTopLevel(focused) ?? main : main;
    private static void Press(MainWindow main, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { TopLevel owner = InputOwner(main); owner.KeyPressQwerty(key, modifiers); owner.KeyReleaseQwerty(key, modifiers); }
    private static string Key(object item) => item switch { GitCommit commit => commit.Hash, ArtificialHistoryRevision artificial => artificial.GraphId, _ => item.GetType().Name };
    private static string? TabCaption(TabItem item) => item.Header is string text ? text : (item.Header as Panel)?.Children.OfType<TextBlock>().Single().Text;
}
