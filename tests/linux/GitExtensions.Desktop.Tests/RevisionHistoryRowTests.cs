using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop.Localization;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;
using Helpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionHistoryRowTests
{
    [Test]
    public void RelativeDatesUseOriginalThresholdsAndRealFrenchAndRussianPluralTranslations()
    {
        DateTimeOffset now = new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);
        TranslationCatalog english = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("en"));
        string Relative(TimeSpan age) => RevisionHistoryRow.FormatRelativeDate(now - age, now, english);
        Assert.Multiple(() =>
        {
            Assert.That(Relative(TimeSpan.FromSeconds(1)), Is.EqualTo("1 second ago"));
            Assert.That(Relative(TimeSpan.FromSeconds(59)), Is.EqualTo("59 seconds ago"));
            Assert.That(Relative(TimeSpan.FromMinutes(1)), Is.EqualTo("1 minute ago"));
            Assert.That(Relative(TimeSpan.FromMinutes(44)), Is.EqualTo("44 minutes ago"));
            Assert.That(Relative(TimeSpan.FromMinutes(45)), Is.EqualTo("1 hour ago"));
            Assert.That(Relative(TimeSpan.FromHours(23)), Is.EqualTo("23 hours ago"));
            Assert.That(Relative(TimeSpan.FromDays(2)), Is.EqualTo("2 days ago"));
            Assert.That(Relative(TimeSpan.FromDays(9)), Is.EqualTo("9 days ago"), "The revision grid disables the weeks form.");
            Assert.That(Relative(TimeSpan.FromDays(30)), Is.EqualTo("1 month ago"));
            Assert.That(Relative(TimeSpan.FromDays(45)), Is.EqualTo("2 months ago"));
            Assert.That(Relative(TimeSpan.FromDays(365)), Is.EqualTo("1 year ago"));
            Assert.That(Relative(TimeSpan.FromDays(-2)), Is.EqualTo("-2 days ago"));
            Assert.That(RevisionHistoryRow.FormatRelativeDate(DateTimeOffset.MinValue, now, english), Is.Empty);
        });
        TranslationCatalog french = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("fr"));
        TranslationCatalog russian = TranslationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Translation"), CultureInfo.GetCultureInfo("ru"));
        Assert.That(french.LoadedFiles, Is.Not.Empty); Assert.That(russian.LoadedFiles, Is.Not.Empty);
        Assert.That(RevisionHistoryRow.FormatRelativeDate(now.AddDays(-1), now, french), Is.EqualTo("Il y a 1 jour"));
        Assert.That(RevisionHistoryRow.FormatRelativeDate(now.AddDays(-2), now, french), Is.EqualTo("Il y a 2 jours"));
        Assert.That(RevisionHistoryRow.FormatRelativeDate(now.AddDays(-2), now, russian), Is.EqualTo("2 дня назад"));
        Assert.That(RevisionHistoryRow.FormatRelativeDate(now.AddDays(-5), now, russian), Is.EqualTo("5 дней назад"));
    }

    [AvaloniaTest]
    public async Task ActualGitReferencesShareTheMessageColumnAndRemainReadableAtWideAndNarrowWidthsInBothThemes()
    {
        using Fixture fixture = await Fixture.CreateAsync("root\n", "root\n", createMerge: true);
        const string longBranch = "preprocessing/fix-front-expiry-with-the-entire-original-branch-name";
        await Helpers.Git(fixture, "branch", longBranch, "HEAD");
        await Helpers.Git(fixture, "tag", "dashboard-v1.3.2-long-release-description", "HEAD");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        IReadOnlyList<GitCommit> commits = await repository.GetLogAsync(30);
        IReadOnlyList<GitReference> references = await repository.GetReferencesAsync();
        string head = await repository.ResolveRevisionAsync("HEAD");
        ListBox history = History(commits, references);
        Window window = new() { Width = 1280, Height = 330, Content = history }; window.Show();
        try
        {
            history.SelectedItem = commits.Single(commit => commit.Hash == head); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Control row = Row(history, head);
            Border[] badges = row.GetVisualDescendants().OfType<Border>().Where(control => control.Name == "RevisionReference").ToArray();
            Assert.That(badges.Select(badge => ((GitReference)badge.Tag!).Name), Is.EquivalentTo(references.Where(reference => reference.Hash == head).Select(reference => reference.Name)));
            Border longBadge = badges.Single(badge => ((GitReference)badge.Tag!).ShortName == longBranch);
            Assert.That(longBadge.Bounds.Width, Is.GreaterThan(105), "A branch label must use the flexible message column, without the old 105px cap.");
            TextBlock label = longBadge.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.That(label.Text, Is.EqualTo(longBranch));
            Assert.That(ToolTip.GetTip(longBadge), Is.EqualTo(longBranch));
            TextBlock subject = row.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "RevisionSubject");
            Assert.That(subject.Text, Is.EqualTo(commits.Single(commit => commit.Hash == head).Subject));
            Assert.That(subject.FontWeight, Is.EqualTo(FontWeight.Bold));
            Assert.That(badges.All(badge => badge.TranslatePoint(default, row)!.Value.X + badge.Bounds.Width <= subject.TranslatePoint(default, row)!.Value.X), Is.True);
            Grid columns = (Grid)row;
            Assert.That(columns.ColumnDefinitions.Select(column => column.ActualWidth).Skip(1), Is.EqualTo(new[] { 150d, 100d, 75d }));
            Assert.That(history.GetVisualDescendants().OfType<ListBoxItem>().Select(item => item.Bounds.Height), Is.All.EqualTo(20));
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; window.Width = 1280;
                using (WriteableBitmap wide = Helpers.Capture(window, "revision-history-inline-wide-" + theme + ".png"))
                { Assert.That(PixelCount(wide, window, longBadge, ((ISolidColorBrush)longBadge.BorderBrush!).Color), Is.GreaterThan(8)); }
                window.Width = 650; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.That(badges.All(badge => badge.Bounds.Width > 10), Is.True, "All references must share the constrained cell rather than disappear behind the graph.");
                Assert.That(subject.Bounds.Width, Is.GreaterThan(25));
                Assert.That(label.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
                Rect message = In(window, row.GetVisualDescendants().OfType<Panel>().Single(control => control.Name == "RevisionMessage"));
                Assert.That(badges.All(badge => In(window, badge).Right <= message.Right + .1), Is.True);
                using WriteableBitmap narrow = Helpers.Capture(window, "revision-history-inline-narrow-" + theme + ".png");
                Assert.That(PixelCount(narrow, window, longBadge, ((ISolidColorBrush)longBadge.BorderBrush!).Color), Is.GreaterThan(8));
            }
            Assert.That(await repository.ResolveRevisionAsync("HEAD"), Is.EqualTo(head));
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void ClickingTheReferenceStillSelectsItsCommitAndControlClickAndArrowKeysKeepListSelectionSemantics()
    {
        GitCommit[] commits = [Commit("M", "Merge feature"), Commit("A", "Side feature"), Commit("R", "Root commit")];
        GitReference[] references = [new("refs/heads/main", "M", null, true), new("refs/heads/feature", "A", null, false)];
        ListBox history = History(commits, references);
        Window window = new() { Width = 1000, Height = 160, Content = history }; window.Show();
        try
        {
            window.UpdateLayout();
            Border[] badges = history.GetVisualDescendants().OfType<Border>().Where(control => control.Name == "RevisionReference").ToArray();
            PointerClick(window, badges.Single(badge => ((GitReference)badge.Tag!).Hash == "A"));
            Assert.That(history.SelectedItem, Is.SameAs(commits[1]));
            PointerClick(window, badges.Single(badge => ((GitReference)badge.Tag!).Hash == "M"), RawInputModifiers.Control);
            Assert.That(history.SelectedItems!.Cast<GitCommit>(), Is.EquivalentTo(commits.Take(2)));
            history.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Assert.That(history.SelectedItems!.Cast<GitCommit>().Count(), Is.EqualTo(1));
            Assert.That(history.SelectedItem, Is.SameAs(commits[1]));
            Assert.That(history.GetVisualDescendants().OfType<Control>().Where(control => control.Name == "RevisionReference").All(control => !control.Focusable), Is.True);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void InitialsUseTheOriginalOpaquePaletteAndForegroundAndStayIdenticalAcrossThemesAndSelection()
    {
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        GitCommit[] commits =
        [
            new("M", [], "Desktop Tester", "desktop@example.com", DateTimeOffset.Now.AddDays(-2), "Original purple DT avatar", ""),
            new("A", [], "Gregory Godin", "gregory@example.invalid", DateTimeOffset.Now.AddDays(-2), "Original olive GG avatar", "")
        ];
        ListBox history = History(commits, []);
        Window window = new() { Width = 1000, Height = 160, Content = history }; window.Show();
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; history.SelectedItem = commits[0];
                using WriteableBitmap frame = Helpers.Capture(window, "revision-history-initials-" + theme + ".png");
                Control purple = Row(history, "M").GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionAuthorAvatar");
                Control olive = Row(history, "A").GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionAuthorAvatar");
                Assert.That(purple.Bounds.Size, Is.EqualTo(new Size(16, 16)));
                Assert.That(PixelCount(frame, window, purple, Color.Parse("#800080")), Is.GreaterThan(80));
                Assert.That(PixelCount(frame, window, olive, Color.Parse("#6b8e23")), Is.GreaterThan(80));
                Assert.That(BlendedInk(frame, window, purple, Colors.White, Color.Parse("#800080")), Is.GreaterThan(8));
                Assert.That(BlendedInk(frame, window, olive, Colors.Black, Color.Parse("#6b8e23")), Is.GreaterThan(8));
                Assert.That(AlphaValues(frame, window, purple), Is.All.EqualTo(255));
                Assert.That(AlphaValues(frame, window, olive), Is.All.EqualTo(255));
                TextBlock date = Row(history, "M").GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "RevisionDate");
                Assert.That(date.Text, Is.EqualTo("2 days ago"));
                Assert.That(ToolTip.GetTip(date), Is.EqualTo(commits[0].AuthoredAt.LocalDateTime.ToString("G", CultureInfo.CurrentCulture)));
            }
            history.SelectedItem = commits[1];
            using WriteableBitmap changed = Helpers.Capture(window, "revision-history-initials-selected-Dark.png");
            Control avatar = Row(history, "A").GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionAuthorAvatar");
            Assert.That(PixelCount(changed, window, avatar, Color.Parse("#6b8e23")), Is.GreaterThan(80));
            Grid absolute = (Grid)RevisionHistoryRow.Create(commits[0], [], relativeDates: false);
            Assert.That(absolute.Children.OfType<TextBlock>().Single(control => control.Name == "RevisionDate").Text,
                Is.EqualTo(commits[0].AuthoredAt.LocalDateTime.ToString("G", CultureInfo.CurrentCulture)));
        }
        finally { window.Close(); CultureInfo.CurrentUICulture = previousCulture; }
    }

    [AvaloniaTest]
    public void SelectedAndUnselectedReferenceBadgesFollowTheOriginalThemeWithoutReplacingListSelectionColors()
    {
        GitCommit[] commits = [Commit("M", "Selected current commit"), Commit("A", "Unselected tag")];
        GitReference[] references = [new("refs/heads/main", "M", null, true), new("refs/tags/v1.3.2", "A", null, false)];
        ListBox history = History(commits, references);
        Window window = new() { Width = 1000, Height = 170, Content = history }; window.Show();
        try
        {
            history.SelectedItem = commits[0]; history.Focus(); window.UpdateLayout();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                using WriteableBitmap frame = Helpers.Capture(window, "revision-history-reference-states-" + theme + ".png");
                Border current = Row(history, "M").GetVisualDescendants().OfType<Border>().Single(control => control.Name == "RevisionReference");
                Border tag = Row(history, "A").GetVisualDescendants().OfType<Border>().Single(control => control.Name == "RevisionReference");
                Color background = Color.Parse(theme == ThemeVariant.Dark ? "#323232" : "#ffffff");
                Assert.That(((ISolidColorBrush)current.Background!).Color, Is.EqualTo(background));
                Assert.That(tag.Background, Is.EqualTo(Brushes.Transparent));
                Assert.That(current.GetVisualDescendants().OfType<TextBlock>().Single().FontWeight, Is.EqualTo(FontWeight.Bold));
                Control arrow = current.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionCurrentBranchArrow");
                Assert.That(PixelCount(frame, window, arrow, Color.Parse(theme == ThemeVariant.Dark ? "#7FE28A" : "#008000")), Is.GreaterThan(5));
                Assert.That(PixelCount(frame, window, current, background), Is.GreaterThan(20));
                TextBlock subject = Row(history, "M").GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "RevisionSubject");
                Assert.That(((ISolidColorBrush)subject.Foreground!).Color, Is.EqualTo(theme == ThemeVariant.Dark ? Color.Parse("#f0f0f0") : Colors.White));
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void ActualSelectionAndFocusKeepGraphAndAllHistoryCellsOnTheOriginalBackgroundsAcrossThemes()
    {
        DateTimeOffset date = DateTimeOffset.Now.AddDays(-2);
        GitCommit[] commits =
        [
            new("M", ["A"], "Author Alpha", "alpha@example.invalid", date, "Selected author revision", ""),
            new("A", ["R"], "Author Alpha", "ALPHA@example.invalid", date, "Other revision by the highlighted author", ""),
            new("R", ["S"], "Author Beta", "beta@example.invalid", date, "Alternating row", ""),
            new("S", [], "Author Gamma", "gamma@example.invalid", date, "Ordinary row", "")
        ];
        ListBox history = History(commits, []);
        CommitGraph graph = new() { Commits = commits, ShowReferenceLabels = false, LineHeight = 20,
            DrawAlternateBackground = true, AuthoredEmail = "alpha@example.invalid" };
        Button otherFocus = new() { Content = "Other control", Height = 24 };
        Grid body = new() { RowDefinitions = new RowDefinitions("*,Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(graph); Grid.SetColumn(history, 1); body.Children.Add(history);
        Grid.SetRow(otherFocus, 1); Grid.SetColumnSpan(otherFocus, 2); body.Children.Add(otherFocus);
        Window window = new() { Width = 1000, Height = 150, Content = body }; window.Show();
        void Update()
        {
            graph.SelectedHashes = history.SelectedItems!.Cast<GitCommit>().Select(commit => commit.Hash).ToArray();
            graph.SelectionFocused = history.IsKeyboardFocusWithin;
            for (int index = 0; index < commits.Length; index++)
            {
                RevisionHistoryRow.UpdateState(Row(history, commits[index].Hash), graph.SelectedHashes.Contains(commits[index].Hash),
                    graph.SelectionFocused, graph.AuthoredEmail, index);
            }
        }
        void SelectionChanged(object? sender, SelectionChangedEventArgs args) => Update();
        void FocusChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
        { if (change.Property == InputElement.IsKeyboardFocusWithinProperty) { Update(); } }
        history.SelectionChanged += SelectionChanged;
        history.PropertyChanged += FocusChanged;
        try
        {
            window.UpdateLayout(); history.SelectedItem = commits[0]; Assert.That(history.Focus(), Is.True); Update();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme; Update();
                Color active = Color.Parse(theme == ThemeVariant.Dark ? "#2864b4" : "#0078d7");
                Color inactive = Color.Parse(theme == ThemeVariant.Dark ? "#436689" : "#e6e6e6");
                Color authored = Color.Parse(theme == ThemeVariant.Dark ? "#393920" : "#eaf1ff");
                Color alternate = Color.Parse(theme == ThemeVariant.Dark ? "#363636" : "#f9f9f9");
                Color normal = Color.Parse(theme == ThemeVariant.Dark ? "#323232" : "#ffffff");
                Color ordinaryText = theme == ThemeVariant.Dark ? Color.Parse("#f0f0f0") : Colors.Black;
                Color selectedText = theme == ThemeVariant.Dark ? ordinaryText : Colors.White;
                Assert.That(history.Focus(), Is.True); Update();
                using (WriteableBitmap focused = Helpers.Capture(window, "revision-history-row-backgrounds-focused-" + theme + ".png"))
                {
                    AssertRows(focused, [active, authored, alternate, normal], selectedText, ordinaryText);
                }
                PointerClick(window, otherFocus); Dispatcher.UIThread.RunJobs();
                Assert.That(history.IsKeyboardFocusWithin, Is.False); Assert.That(graph.SelectionFocused, Is.False);
                using (WriteableBitmap unfocused = Helpers.Capture(window, "revision-history-row-backgrounds-inactive-" + theme + ".png"))
                {
                    AssertRows(unfocused, [inactive, authored, alternate, normal], ordinaryText, ordinaryText);
                }
                PointerClick(window, Row(history, "R")); Dispatcher.UIThread.RunJobs();
                Assert.That(history.SelectedItem, Is.SameAs(commits[2])); Assert.That(graph.SelectionFocused, Is.True);
                using (WriteableBitmap selectedAlternate = Helpers.Capture(window, "revision-history-row-backgrounds-selected-alternate-" + theme + ".png"))
                {
                    AssertRows(selectedAlternate, [authored, authored, active, normal], ordinaryText, selectedText);
                }
                history.SelectedItem = commits[0]; Update();

                void AssertRows(WriteableBitmap frame, Color[] backgrounds, Color firstText, Color thirdText)
                {
                    for (int index = 0; index < commits.Length; index++)
                    {
                        Control row = Row(history, commits[index].Hash);
                        Assert.That(((ISolidColorBrush)((Grid)row).Background!).Color, Is.EqualTo(backgrounds[index]));
                        foreach (string name in new[] { "RevisionSubject", "RevisionAuthorName", "RevisionDate", "RevisionHash" })
                        {
                            TextBlock cell = row.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == name);
                            Assert.That(PixelCount(frame, window, cell, backgrounds[index]), Is.GreaterThan(15), name + " needs the same actual row background as the graph.");
                        }
                        Rect graphRow = new(graph.TranslatePoint(new Point(0, index * 20), window)!.Value, new Size(graph.Bounds.Width, 20));
                        Assert.That(PixelCount(frame, graphRow, backgrounds[index]), Is.GreaterThan(100));
                        TextBlock author = row.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "RevisionAuthorName");
                        Assert.That(author.FontWeight, Is.EqualTo(index < 2 ? FontWeight.Bold : FontWeight.Normal));
                        Color foreground = index == 0 ? firstText : index == 2 ? thirdText : ordinaryText;
                        Assert.That(((ISolidColorBrush)author.Foreground!).Color, Is.EqualTo(foreground));
                        Assert.That(BlendedInk(frame, window, author, foreground, backgrounds[index]), Is.GreaterThan(8));
                    }
                }
            }
            Assert.That(history.ItemsSource!.Cast<GitCommit>(), Is.EqualTo(commits));
            Assert.That(graph.OrderedCommits, Is.EqualTo(commits));
        }
        finally
        {
            history.SelectionChanged -= SelectionChanged; history.PropertyChanged -= FocusChanged;
            window.Close();
        }
    }

    internal static ListBox History(IReadOnlyList<GitCommit> commits, IReadOnlyList<GitReference> references)
    {
        ListBox history = new() { Focusable = true, SelectionMode = SelectionMode.Multiple, ItemsSource = commits,
            ItemTemplate = new FuncDataTemplate<GitCommit>((commit, _) => RevisionHistoryRow.Create(commit, references)) };
        history.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(Layoutable.HeightProperty, 20d), new Setter(Layoutable.MinHeightProperty, 20d), new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, default(Thickness)) } });
        return history;
    }
    private static Control Row(ListBox history, string hash) => history.GetVisualDescendants().OfType<ListBoxItem>()
        .Single(item => (item.DataContext as GitCommit)?.Hash == hash).GetVisualDescendants().OfType<Control>().Single(control => control.Name == "RevisionHistoryRow");
    private static GitCommit Commit(string hash, string subject) => new(hash, [], "Desktop Tester", "desktop@example.com", DateTimeOffset.Now.AddDays(-2), subject, "");
    private static Rect In(Window window, Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void PointerClick(Window window, Control control, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.UpdateLayout(); Rect bounds = In(window, control); Point point = bounds.Center;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left, modifiers); window.MouseUp(point, MouseButton.Left, modifiers);
    }
    private static IEnumerable<(byte R, byte G, byte B, byte A)> Pixels(WriteableBitmap frame, Window owner, Control control)
        => Pixels(frame, In(owner, control));
    private static IEnumerable<(byte R, byte G, byte B, byte A)> Pixels(WriteableBitmap frame, Rect bounds)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        byte[] bytes = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        List<(byte R, byte G, byte B, byte A)> pixels = [];
        for (int y = Math.Max(0, (int)Math.Ceiling(bounds.Y)); y < Math.Min(buffer.Size.Height, (int)Math.Floor(bounds.Bottom)); y++)
        for (int x = Math.Max(0, (int)Math.Ceiling(bounds.X)); x < Math.Min(buffer.Size.Width, (int)Math.Floor(bounds.Right)); x++)
        {
            int offset = y * buffer.RowBytes + x * 4;
            pixels.Add((bytes[offset + (buffer.Format == PixelFormat.Rgba8888 ? 0 : 2)], bytes[offset + 1], bytes[offset + (buffer.Format == PixelFormat.Rgba8888 ? 2 : 0)], bytes[offset + 3]));
        }
        return pixels;
    }
    private static int PixelCount(WriteableBitmap frame, Window owner, Control control, Color color)
        => Pixels(frame, owner, control).Count(pixel => pixel.R == color.R && pixel.G == color.G && pixel.B == color.B && pixel.A == 255);
    private static int PixelCount(WriteableBitmap frame, Rect bounds, Color color)
        => Pixels(frame, bounds).Count(pixel => pixel.R == color.R && pixel.G == color.G && pixel.B == color.B && pixel.A == 255);
    private static byte[] AlphaValues(WriteableBitmap frame, Window owner, Control control) => Pixels(frame, owner, control).Select(pixel => pixel.A).ToArray();
    private static int BlendedInk(WriteableBitmap frame, Window owner, Control control, Color foreground, Color background)
    {
        double dr = foreground.R - background.R, dg = foreground.G - background.G, db = foreground.B - background.B;
        double norm = dr * dr + dg * dg + db * db;
        return Pixels(frame, owner, control).Count(pixel =>
        {
            double coverage = ((pixel.R - background.R) * dr + (pixel.G - background.G) * dg + (pixel.B - background.B) * db) / norm;
            return coverage >= .25 && coverage <= 1 && pixel.A == 255 &&
                Math.Abs(pixel.R - background.R - coverage * dr) <= 3 && Math.Abs(pixel.G - background.G - coverage * dg) <= 3 && Math.Abs(pixel.B - background.B - coverage * db) <= 3;
        });
    }
}
