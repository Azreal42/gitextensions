using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using RepositoryFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class FontConsumerTests
{
    private static FontPreferences DistinctFonts => new()
    {
        Application = new("DejaVu Sans", 12, true, true),
        Code = new("DejaVu Sans Mono", 11.25, true, true),
        Commit = new("DejaVu Serif", 13, false, true),
        Monospace = new("DejaVu Sans", 15, false, false)
    };

    [AvaloniaTest]
    public void DefaultsReachTheRealCodeAndCommitConsumersInPointsWithoutSharingTheirRole()
    {
        using Profile profile = new(new FontPreferences());
        TextBox source = new() { Text = "literal\r\ntext" }; using DiffView code = new(source) { Mode = DiffViewMode.PlainText };
        using CommitInformationView information = new(); information.SetMessage("literal commit message");
        Assert.That(code.Editor.FontSize, Is.EqualTo(40d / 3).Within(.000001));
        Assert.That(information.MessageControl.FontSize, Is.EqualTo(12));
        Assert.That(code.Editor.FontSize, Is.Not.EqualTo(information.MessageControl.FontSize));
        Assert.That(code.Text, Is.EqualTo(source.Text));
    }

    [AvaloniaTest]
    public void DefaultCompactRowsKeepTheirRealTextZonesUnclippedWithoutAddingArtificialPadding()
    {
        using Profile profile = new(new FontPreferences()); DesktopFontSession fonts = DesktopFonts.Active;
        TextBlock treeText = new() { Text = "By branch", Name = "CompactFontTreeText" };
        TreeViewItem node = new() { Header = treeText };
        TreeView tree = new() { ItemsSource = new[] { node } };
        Control treeHost = ClassicTreeViewAppearance.Wrap(tree);
        GitCommit commit = new(new string('a', 40), [], "By author", "author@example.test", DateTimeOffset.UnixEpoch, "By subject", "body");
        GitReference reference = new("refs/heads/By-descenders-gyp", commit.Hash, null, false);
        Control history = RevisionHistoryRow.Create(commit, [reference]);
        TextBlock menuText = new() { Text = "By command" };
        MenuItem menuItem = new() { Header = menuText, InputGesture = new(Key.F9, KeyModifiers.Control) };
        Button opener = new() { Content = "Open", ContextMenu = new ContextMenu { ItemsSource = new[] { menuItem } } };
        Window window = Host(new StackPanel { Children = { treeHost, history, opener } }, fonts, false); window.Show(); Pump(window);
        try
        {
            Border treeZone = node.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_LayoutRoot" && border.TemplatedParent == node);
            ContentPresenter treePresenter = node.GetVisualDescendants().OfType<ContentPresenter>().Single(presenter => presenter.Name == "PART_HeaderPresenter" && presenter.TemplatedParent == node);
            Assert.That(treeZone.Bounds.Height, Is.EqualTo(18));
            Assert.That(treePresenter.Margin.Top + treePresenter.Margin.Bottom, Is.Zero);
            AssertTextFits(treeZone, treeText);
            Assert.That(history.Bounds.Height, Is.EqualTo(18));
            AssertTextFits(history, history.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RevisionSubject"));
            AssertTextFits(history, history.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RevisionHash"));
            RightClick(opener); UntilSynchronous(() => opener.ContextMenu!.IsOpen, window);
            Control popup = (Control)menuItem.GetVisualRoot()!; Pump(popup);
            Border menuZone = menuItem.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_LayoutRoot" && border.TemplatedParent == menuItem);
            Assert.That(menuItem.Bounds.Height, Is.EqualTo(22));
            Assert.That(menuZone.Padding.Top + menuZone.Padding.Bottom, Is.Zero);
            AssertTextFits(menuZone, menuText);
            TextBlock gesture = menuItem.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_InputGestureText");
            AssertTextFits(menuZone, gesture);
            Border badge = history.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "RevisionReference");
            TextBlock badgeText = badge.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RevisionReferenceLabel");
            TestContext.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { BadgeText = badgeText.Text,
                BadgeBounds = badge.Bounds, BadgeBorder = badge.BorderThickness, BadgePadding = badge.Padding,
                LabelTextLayoutHeight = badgeText.TextLayout.Height, LabelBounds = badgeText.Bounds,
                LabelDesiredSize = badgeText.DesiredSize, LabelMargin = badgeText.Margin }));
            ResolvedDesktopFont application = fonts.For(DesktopFontRole.Application);
            TestContext.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { application.Requested, application.EffectiveFamilyName, application.SizeInDips,
                TextHeight = DesktopFontMetrics.TextHeight(application), TreeZone = treeZone.Bounds, TreeText = treeText.Bounds, TreeDesired = treeText.DesiredSize,
                MenuZone = menuZone.Bounds, MenuText = menuText.Bounds, MenuDesired = menuText.DesiredSize, MenuPadding = menuZone.Padding }));
            Capture(window, "fonts-default-compact-real-text-zones.png");
        }
        finally { opener.ContextMenu!.Close(); window.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void ApplicationStyleFactoriesReachActualPopupItemsAndTheirShortcutText(bool dark)
    {
        using Profile profile = new(DistinctFonts); DesktopFontSession session = DesktopFonts.Active;
        MenuItem item = new() { Header = "A long command", InputGesture = new(Key.F9, KeyModifiers.Control) };
        Button opener = new() { Content = "Open", ContextMenu = new ContextMenu { ItemsSource = new[] { item } } };
        TextBox input = new() { Text = "Application editor" };
        Window window = Host(new StackPanel { Children = { input, opener } }, session, dark); window.Show(); Pump(window);
        try
        {
            AssertFont(window, session.For(DesktopFontRole.Application)); AssertFont(input, session.For(DesktopFontRole.Application));
            RightClick(opener);
            UntilSynchronous(() => opener.ContextMenu!.IsOpen, window);
            Control popup = (Control)item.GetVisualRoot()!; Pump(popup);
            AssertFont(item, session.For(DesktopFontRole.Application));
            TextBlock gesture = item.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_InputGestureText");
            AssertFont(gesture, session.For(DesktopFontRole.Application));
            Assert.That(item.Bounds.Height, Is.GreaterThanOrEqualTo(gesture.DesiredSize.Height));
            AssertTextFits(item, gesture);
            Capture((TopLevel)popup, "fonts-application-popup-" + (dark ? "dark" : "light") + ".png");
        }
        finally { opener.ContextMenu!.Close(); window.Close(); }
    }

    [AvaloniaTest]
    public void FixedWindowsSearchNoticeAndCommandLogDoNotInheritTheCustomApplicationFace()
    {
        using Profile profile = new(DistinctFonts with { Application = new("DejaVu Serif", 12, true, true) });
        DesktopFontSession fonts = DesktopFonts.Active;
        using RevisionQuickSearch search = new(() => 1, () => 0, (_, _) => true, "Search: ");
        Window noticeHost = Host(search.Notice, fonts, false);
        GitCommandLogWindow log = new(TranslationCatalog.Load("", CultureInfo.GetCultureInfo("en")));
        log.Styles.Add(ClassicStyles.Create(fonts));
        noticeHost.Show(); log.Show(); search.Type("a"); Pump(noticeHost); Pump(log);
        try
        {
            TextBlock notice = Field<TextBlock>(noticeHost, "RevisionQuickSearchText");
            Assert.That(notice.FontFamily, Is.EqualTo(FontFamily.Default));
            Assert.That(notice.FontSize, Is.EqualTo(44d / 3));
            Assert.That(notice.FontWeight, Is.EqualTo(FontWeight.Bold));
            Assert.That(notice.FontStyle, Is.EqualTo(FontStyle.Normal));
            TextBox output = Field<TextBox>(log, "GitCommandLogText");
            Assert.That(output.FontFamily, Is.EqualTo(new FontFamily("DejaVu Sans Mono")));
            Assert.That(output.FontSize, Is.EqualTo(12));
            Assert.That(output.FontWeight, Is.EqualTo(FontWeight.Normal));
            Assert.That(output.FontStyle, Is.EqualTo(FontStyle.Normal));
            Assert.That(fonts.For(DesktopFontRole.Application).Style, Is.EqualTo(FontStyle.Italic));
            Assert.That(fonts.For(DesktopFontRole.Application).Weight, Is.EqualTo(FontWeight.Bold));
        }
        finally { log.Close(); noticeHost.Close(); }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task LargerApplicationAndIdentifierFontsKeepActualBrowseGraphTreeAndWorktreeRowsAligned(bool dark)
    {
        using RepositoryFixture repository = await RepositoryFixture.CreateAsync();
        using Profile profile = new(DistinctFonts with { Application = new("DejaVu Sans", 15, true, true) }); DesktopFontSession fonts = DesktopFonts.Active;
        MainWindow main = new() { Width = 1400, Height = 900, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        main.Styles.Add(ClassicStyles.Create(fonts)); main.Styles.Add(ClassicMenuAppearance.Create(fonts));
        main.Show();
        try
        {
            await main.OpenRepositoryAsync(repository.Repository.Path); await Idle(main);
            ListBox history = Field<ListBox>(main, "RevisionHistory"); object? source = history.ItemsSource, selected = history.SelectedItem;
            ListBoxItem row = history.GetVisualDescendants().OfType<ListBoxItem>().First();
            TextBlock hash = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RevisionHash");
            TextBlock author = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RevisionAuthorName");
            AssertFont(hash, fonts.For(DesktopFontRole.Monospace)); AssertFont(author, fonts.For(DesktopFontRole.Application));
            AssertTextFits(row, hash); AssertTextFits(row, author);
            Assert.That(row.Bounds.Height, Is.GreaterThanOrEqualTo(hash.DesiredSize.Height));
            CommitGraph graph = main.GetVisualDescendants().OfType<CommitGraph>().Single();
            Assert.That(graph.LineHeight, Is.EqualTo(row.Bounds.Height).Within(.01));
            TreeView tree = Field<TreeView>(main, "RepositoryNavigation");
            TreeViewItem node = tree.GetVisualDescendants().OfType<TreeViewItem>().First(item => item.IsEffectivelyVisible);
            Border header = node.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_LayoutRoot");
            Assert.That(header.Bounds.Height, Is.GreaterThanOrEqualTo(DesktopFontMetrics.TextHeight(fonts.For(DesktopFontRole.Application))));
            Control navigationToolbar = Field<StackPanel>(main, "RepositoryNavigationToolbar");
            Button collapse = Field<Button>(main, "NavigationCollapseAll"); AssertVerticallyContained(navigationToolbar, collapse);
            foreach (ToggleButton toggle in navigationToolbar.GetVisualChildren().OfType<ToggleButton>())
            {
                AssertVerticallyContained(navigationToolbar, toggle);
                Assert.That(toggle.Bounds.Height, Is.EqualTo(collapse.Bounds.Height).Within(.01));
            }
            TextBox search = Field<TextBox>(main, "RepositoryNavigationSearch");
            AssertVerticallyContained((Control)search.GetVisualParent()!, search);
            Grid virtualRows = Field<Grid>(main, "WorkingRevisions");
            Button working = Field<Button>(main, "WorkingDirectoryRevision"), index = Field<Button>(main, "IndexRevision");
            Assert.That(virtualRows.IsEffectivelyVisible, Is.True);
            Rect workingBounds = AssertVerticallyContained(virtualRows, working), indexBounds = AssertVerticallyContained(virtualRows, index);
            Assert.That(workingBounds.Bottom, Is.LessThanOrEqualTo(indexBounds.Top + .01), "The two artificial rows must not overlap when their text rows grow.");
            Assert.That(working.Bounds.Height, Is.EqualTo(row.Bounds.Height).Within(.01));
            Assert.That(index.Bounds.Height, Is.EqualTo(row.Bounds.Height).Within(.01));
            foreach (Canvas canvas in virtualRows.Children.OfType<Canvas>())
            {
                Rect bounds = AssertVerticallyContained(virtualRows, canvas);
                Rect actionBounds = Grid.GetRow(canvas) == 0 ? workingBounds : indexBounds;
                Assert.That(bounds.Top, Is.EqualTo(actionBounds.Top).Within(.01));
                Assert.That(bounds.Height, Is.EqualTo(actionBounds.Height).Within(.01));
                Avalonia.Controls.Shapes.Line stroke = canvas.Children.OfType<Avalonia.Controls.Shapes.Line>().Single();
                Avalonia.Controls.Shapes.Ellipse dot = canvas.Children.OfType<Avalonia.Controls.Shapes.Ellipse>().Single();
                Assert.That(stroke.EndPoint.Y, Is.EqualTo(canvas.Bounds.Height).Within(.01));
                Assert.That(Canvas.GetTop(dot) + dot.Bounds.Height / 2, Is.EqualTo(canvas.Bounds.Height / 2).Within(.01));
            }
            WorktreeTable table = new(text => text); table.SetItems(await repository.Repository.GetWorktreeSnapshotsAsync());
            Window manager = Host(table, fonts, dark); manager.Show(); Pump(manager);
            try
            {
                TextBlock sha = Field<TextBlock>(manager, "WorktreeSha"); AssertFont(sha, fonts.For(DesktopFontRole.Monospace));
                ListBoxItem worktree = table.Rows.GetVisualDescendants().OfType<ListBoxItem>().Single();
                Assert.That(worktree.Bounds.Height, Is.GreaterThanOrEqualTo(DesktopFontMetrics.TextHeight(fonts.For(DesktopFontRole.Monospace))));
                Assert.That(sha.Bounds.Height, Is.GreaterThanOrEqualTo(DesktopFontMetrics.TextHeight(fonts.For(DesktopFontRole.Monospace))), "The real cell must retain a complete line after margins and its bottom border.");
                Capture(manager, "fonts-identifiers-worktrees-" + (dark ? "dark" : "light") + ".png");
            }
            finally { manager.Close(); }
            Assert.That(history.ItemsSource, Is.SameAs(source)); Assert.That(history.SelectedItem, Is.SameAs(selected));
            Capture(main, "fonts-browse-aligned-" + (dark ? "dark" : "light") + ".png");
        }
        finally { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted, main); await main.WindowWorkCompletion; }
    }

    [AvaloniaTest]
    public async Task CommitMessageAndSharedInformationUseCommitWhileActualDiffAndProcessUseTheirSeparateRoles()
    {
        using RepositoryFixture repository = await RepositoryFixture.CreateAsync(); using Profile profile = new(DistinctFonts);
        DesktopFontSession fonts = DesktopFonts.Active;
        CommitDialog commit = new(repository.Repository); commit.Styles.Add(ClassicStyles.Create(fonts)); commit.Show();
        try
        {
            await Until(() => !commit.IsBusy, commit);
            AssertFont(Field<TextBox>(commit, "Message"), fonts.For(DesktopFontRole.Commit));
            AssertFont(Field<TextBox>(commit, "CommitOutput"), fonts.For(DesktopFontRole.Monospace));
            AssertFont(Field<TextEditor>(commit, "DiffTextEditor"), fonts.For(DesktopFontRole.Code));
            using CommitInformationView information = new(); information.SetRevision(repository.Repository,
                (await repository.Repository.GetLogAsync(1, all: false)).Single());
            Window host = Host(information, fonts, false); host.Show();
            try
            {
                await information.LoadAsync(); Pump(host);
                AssertFont(information.MessageControl, fonts.For(DesktopFontRole.Commit));
                AssertFont(Field<SelectableTextBlock>(host, "CommitInformationAuthor"), fonts.For(DesktopFontRole.Application));
                Capture(host, "fonts-commit-information-independent.png");
            }
            finally { information.BeginClose(); await information.Completion; host.Close(); }
        }
        finally { commit.Close(); await Until(() => !commit.IsVisible && !commit.IsBusy, commit); }
    }

    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task SavedEolStyleReachesActualDiffTextRunsOnlyWhileNonprintingIsOnAndPreservesCopy(bool glyphs, bool dark)
    {
        using Profile profile = new(DistinctFonts with { ShowEolMarkerAsGlyph = glyphs });
        const string text = "a b\tc\rd e\nf g\r\nlast";
        TextBox source = new() { Text = text }; using DiffView view = new(source) { Mode = DiffViewMode.PlainText };
        Window window = Host(view, DesktopFonts.Active, dark); window.Show(); Pump(window);
        var document = view.Editor.Document; view.Select(1, text.Length - 2); string selected = view.Editor.SelectedText;
        string? oldClipboard = await ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
        try
        {
            view.Editor.Copy(); string? copied = await ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
            Assert.That(Markers(view.Editor, 1), Is.Empty);
            Assert.That(view.RenderingMenu.ToggleNonPrinting(), Is.True); Pump(window);
            Assert.That(Enumerable.Range(1, 3).Select(number => Markers(view.Editor, number).Last()),
                Is.EqualTo(glyphs ? new[] { "«", "¶", "¤" } : new[] { "\\r", "\\n", "\\r\\n" }));
            Assert.That(Markers(view.Editor, 4), Is.Empty);
            Assert.That(Markers(view.Editor, 1).Take(2), Is.EqualTo(new[] { "·", "»" }));
            Assert.That(view.Editor.Document, Is.SameAs(document)); Assert.That(view.Editor.SelectedText, Is.EqualTo(selected));
            Assert.That(view.Text, Is.EqualTo(text)); Assert.That(source.Text, Is.EqualTo(text));
            view.Editor.Copy(); Assert.That(await ClipboardExtensions.TryGetTextAsync(window.Clipboard!), Is.EqualTo(copied));
            Capture(window, "fonts-eol-" + (glyphs ? "glyphs-" : "labels-") + (dark ? "dark" : "light") + ".png");
            Assert.That(view.RenderingMenu.ToggleNonPrinting(), Is.True); Pump(window); Assert.That(Markers(view.Editor, 1), Is.Empty);
            Assert.That(profile.Store.Load().ShowEolMarkerAsGlyph, Is.EqualTo(glyphs));
        }
        finally { await ClipboardExtensions.SetTextAsync(window.Clipboard!, oldClipboard ?? ""); window.Close(); }
    }

    [AvaloniaTest]
    public void SavingFontsNeverRebindsOrRefocusesExistingConsumersAndOnlyANewSessionSeesTheSavedChoice()
    {
        FontPreferences before = DistinctFonts; using Profile profile = new(before);
        DesktopFontSession active = DesktopFonts.Active;
        TextBox source = new() { Text = "unchanged file\n" }; using DiffView view = new(source) { Mode = DiffViewMode.PlainText };
        using CommitInformationView information = new(); information.SetMessage("unchanged message");
        TextBox other = new() { Text = "user input" };
        Window window = Host(new StackPanel { Children = { other, view, information } }, active, false); window.Show(); Pump(window);
        try
        {
            PushProcessTestDriver.Pointer(other); object? focused = window.FocusManager?.GetFocusedElement();
            var document = view.Editor.Document; double size = view.Editor.FontSize; GitCommandLog.Clear();
            FontPreferences after = before with { Code = before.Code with { SizeInPoints = 18 }, Commit = before.Commit with { SizeInPoints = 19 }, ShowEolMarkerAsGlyph = true };
            profile.Store.SaveChanges(before, after); Pump(window);
            Assert.That(DesktopFonts.Active, Is.SameAs(active)); Assert.That(view.Editor.FontSize, Is.EqualTo(size));
            Assert.That(view.Editor.Document, Is.SameAs(document)); Assert.That(window.FocusManager?.GetFocusedElement(), Is.SameAs(focused));
            Assert.That(information.MessageControl.FontSize, Is.EqualTo(before.Commit.SizeInDips)); Assert.That(GitCommandLog.Snapshot(), Is.Empty);
            DesktopFontSession restarted = DesktopFonts.CreateSession(profile.Store);
            Assert.That(restarted.For(DesktopFontRole.Code).SizeInDips, Is.EqualTo(24));
            Assert.That(restarted.For(DesktopFontRole.Commit).SizeInDips, Is.EqualTo(76d / 3).Within(.000001));
            Assert.That(restarted.ShowEolMarkerAsGlyph, Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void RealControlWheelZoomKeepsTheStyledCodeSelectionAndPersistentPointChoice()
    {
        using Profile profile = new(DistinctFonts); byte[] before = File.ReadAllBytes(profile.Store.FilePath);
        TextBox source = new() { Text = string.Join('\n', Enumerable.Range(1, 100).Select(number => $"row {number} α🙂")) };
        using DiffView view = new(source) { Mode = DiffViewMode.PlainText }; Window window = Host(view, DesktopFonts.Active, false); window.Show(); Pump(window);
        try
        {
            int offset = view.Text.IndexOf("α🙂", StringComparison.Ordinal); view.Select(offset, "α🙂".Length);
            double size = view.Editor.FontSize, margin = view.Editor.TextArea.LeftMargins.Sum(control => control.Bounds.Width);
            string selected = view.Editor.SelectedText; Point point = view.Editor.TextArea.TranslatePoint(new Point(100, 40), window)!.Value;
            window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control); Pump(window);
            Assert.That(view.Editor.FontSize, Is.EqualTo(size + 1)); Assert.That(view.Editor.FontStyle, Is.EqualTo(FontStyle.Italic));
            Assert.That(view.Editor.FontWeight, Is.EqualTo(FontWeight.Bold)); Assert.That(view.Editor.SelectedText, Is.EqualTo(selected));
            Assert.That(view.Editor.TextArea.LeftMargins.Sum(control => control.Bounds.Width), Is.GreaterThan(margin));
            Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(before));
        }
        finally { window.Close(); }
    }

    private static Rect AssertVerticallyContained(Control host, Control child)
    {
        Point? position = child.TranslatePoint(default, host); Assert.That(position, Is.Not.Null);
        Rect bounds = new(position!.Value, child.Bounds.Size);
        Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(-.01), child.Name + " starts within its host");
        Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(host.Bounds.Height + .01), child.Name + " ends within its host");
        return bounds;
    }

    private static void AssertTextFits(Control zone, TextBlock text)
    {
        Assert.That(text.Bounds.Height, Is.GreaterThanOrEqualTo(text.DesiredSize.Height - .01), text.Name + " keeps its full measured line");
        Point? position = text.TranslatePoint(default, zone); Assert.That(position, Is.Not.Null);
        Assert.That(position!.Value.Y, Is.GreaterThanOrEqualTo(-.01), text.Name + " starts inside its real text zone");
        Assert.That(position.Value.Y + text.Bounds.Height, Is.LessThanOrEqualTo(zone.Bounds.Height + .01), text.Name + " ends inside its real text zone");
    }

    private static Window Host(Control child, DesktopFontSession fonts, bool dark)
    {
        Window window = new() { Content = child, Width = 1000, Height = 700, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Styles.Add(ClassicStyles.Create(fonts)); window.Styles.Add(ClassicMenuAppearance.Create(fonts)); return window;
    }
    private static void AssertFont(Control control, ResolvedDesktopFont font)
    {
        FontFamily family; double size; FontStyle style; FontWeight weight;
        if (control is TextBlock text) { family = text.FontFamily; size = text.FontSize; style = text.FontStyle; weight = text.FontWeight; }
        else { TemplatedControl textControl = (TemplatedControl)control; family = textControl.FontFamily; size = textControl.FontSize; style = textControl.FontStyle; weight = textControl.FontWeight; }
        Assert.That(family, Is.EqualTo(font.Family), control.Name); Assert.That(size, Is.EqualTo(font.SizeInDips).Within(.000001), control.Name);
        Assert.That(style, Is.EqualTo(font.Style), control.Name); Assert.That(weight, Is.EqualTo(font.Weight), control.Name);
    }
    private static string[] Markers(TextEditor editor, int number)
    {
        VisualLine line = editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(number)); List<string> result = [];
        foreach (TextRun run in line.TextLines.SelectMany(textLine => textLine.TextRuns))
        {
            VisualLineElement? element = run is FormattedTextRun formatted ? formatted.Element
                : run.GetType().GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(run) as VisualLineElement;
            if (element is null) { continue; }
            TextLine? shaped = element is FormattedTextElement ? typeof(FormattedTextElement).GetField("TextLine", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(element) as TextLine
                : element.GetType().GetField("Text", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(element) as TextLine;
            if (shaped is not null) { string text = string.Concat(shaped.TextRuns.Where(item => !item.Text.IsEmpty).Select(item => item.Text.ToString())); if (text.Length > 0) { result.Add(text); } }
        }
        return result.ToArray();
    }
    private static T Field<T>(TopLevel root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Pump(Control control) { Dispatcher.UIThread.RunJobs(); control.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void RightClick(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!; for (int frame = 0; frame < 10; frame++) { Pump(top); }
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        Assert.That((top.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(control), Is.True);
        top.MouseMove(point); top.MouseDown(point, MouseButton.Right); top.MouseUp(point, MouseButton.Right); Pump(top);
    }
    private static void UntilSynchronous(Func<bool> predicate, Control root)
    { for (int i = 0; i < 15 && !predicate(); i++) { Pump(root); } Assert.That(predicate(), Is.True); }
    private static async Task Until(Func<bool> predicate, Control root)
    { DateTime end = DateTime.UtcNow.AddSeconds(15); while (!predicate() && DateTime.UtcNow < end) { Pump(root); await Task.Delay(10); } Assert.That(predicate(), Is.True); }
    private static async Task Idle(MainWindow main) { await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted, main); await main.WindowWorkCompletion; Pump(main); Assert.That(main.LastException, Is.Null); }
    private static void Capture(TopLevel window, string name)
    {
        Pump(window); using var image = window.CaptureRenderedFrame(); Assert.That(image, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory; Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); image!.Save(path); TestContext.AddTestAttachment(path);
    }
    private sealed class Profile : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-font-consumers-" + Guid.NewGuid().ToString("N"));
        internal FontPreferencesStore Store { get; }
        internal Profile(FontPreferences preferences) { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); Store = new(); Store.SaveChanges(new(), preferences); }
        public void Dispose() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous); if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}
