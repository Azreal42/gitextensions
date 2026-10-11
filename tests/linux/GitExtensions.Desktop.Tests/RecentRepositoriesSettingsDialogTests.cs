using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RecentRepositoriesSettingsDialogTests
{
    [Test]
    public void TopSelectionKeepsBothAnchorKindsPinsOverrideCapacityAndFavoritesStayIndependent()
    {
        RepositoryHistoryState state = new()
        {
            Recent = [new("/work/a"), new RepositoryHistoryEntry("/work/b").WithAnchor(RepositoryHistoryAnchor.Recent), new RepositoryHistoryEntry("/work/c").WithAnchor(RepositoryHistoryAnchor.Top), new("/work/d")],
            Favorites = [new("/work/d", " category with spaces ")], MaxTopRepositories = 1, HideTopRepositoriesFromRecentList = true
        };
        string before = JsonSerializer.Serialize(state);
        RepositoryHistorySelection selection = RepositoryHistoryPresentation.Select(state, homeDirectory: "/different/home");
        Assert.That(selection.Top.Select(row => row.Path), Is.EqualTo(new[] { "/work/c" }));
        Assert.That(selection.Recent.Select(row => row.Path), Is.EqualTo(new[] { "/work/a", "/work/b", "/work/d" }));
        Assert.That(RepositoryHistoryPresentation.Select(state with { MaxTopRepositories = 0 }).Top.Select(row => row.Path), Is.EqualTo(new[] { "/work/c" }));
        selection = RepositoryHistoryPresentation.Select(state.Anchor("/work/a", RepositoryHistoryAnchor.Top).Anchor("/work/d", RepositoryHistoryAnchor.Top));
        Assert.That(selection.Top.Select(row => row.Path), Is.EqualTo(new[] { "/work/a", "/work/c", "/work/d" }));
        Assert.That(selection.Recent.Select(row => row.Path), Is.EqualTo(new[] { "/work/b" }));
        Assert.That(JsonSerializer.Serialize(state), Is.EqualTo(before));
        Assert.That(new RepositoryHistoryEntry("/old/native", Pinned: true).EffectiveAnchor, Is.EqualTo(RepositoryHistoryAnchor.Recent));
    }

    [Test]
    public void SignificantNamesDisambiguateDuplicateLeafDirectoriesAndKeepEveryLiteralPath()
    {
        string[] paths = ["/home/alice/code/Project", "/home/alice/other/Project", "/home/alice/name\n\t\"$(literal);雪", "/home/alice-not-home/repository"];
        RepositoryHistoryState state = new() { Recent = paths.Select(path => new RepositoryHistoryEntry(path)).ToList(), MaxTopRepositories = 4 };
        RepositoryHistorySelection plain = RepositoryHistoryPresentation.Select(state, homeDirectory: "/home/alice");
        Assert.That(plain.Recent[0].Caption, Is.EqualTo("~/code/Project"));
        Assert.That(plain.Recent[3].Caption, Is.EqualTo(paths[3]), "A common prefix is not membership of the home directory.");
        RepositoryHistorySelection shortened = RepositoryHistoryPresentation.Select(state with { ShorteningStrategy = RepositoryPathShorteningStrategy.MostSignDir }, homeDirectory: "/home/alice");
        Assert.That(shortened.Top.Select(row => row.Caption), Is.EqualTo(new[] { "Project (code)", "Project (other)", "name\n\t\"$(literal);雪", "repository" }));
        Assert.That(shortened.Top.Select(row => row.Path), Is.EqualTo(paths));
        RepositoryHistorySelection sorted = RepositoryHistoryPresentation.Select(state with { ShorteningStrategy = RepositoryPathShorteningStrategy.MostSignDir, SortTopRepos = true, SortRecent = true });
        Assert.That(sorted.Top.Select(row => row.Caption), Is.Ordered.Using((IComparer<string>)StringComparer.CurrentCulture));
        Assert.That(sorted.Top.Select(row => row.Path), Is.EquivalentTo(paths));
    }

    [Test]
    public void MiddleDotsRetainTheOriginalSignificantDirectoriesAndUseInjectedPixelMeasurements()
    {
        const string path = "/home/alice/Company/Projects/git/Product/Sources/Repo/Work雪";
        RepositoryHistoryState state = new() { Recent = [new(path)], ShorteningStrategy = RepositoryPathShorteningStrategy.MiddleDots };
        string automatic = RepositoryHistoryPresentation.Select(state, homeDirectory: "/home/alice").Recent.Single().Caption;
        Assert.That(automatic, Is.EqualTo("~/Company/../Repo/Work雪"));
        string measured = RepositoryHistoryPresentation.Select(state with { RecentReposComboMinWidth = 110 }, text => text.Length * 4, "/home/alice").Recent.Single().Caption;
        Assert.That(measured, Does.Contain("..").And.EndWith("/Work雪"));
        Assert.That(measured.Length * 4, Is.LessThanOrEqualTo(100));
        const string emoji = "/home/alice/😀😀😀😀😀😀😀😀/LongRepository/WorkingDirectory";
        string unicode = RepositoryHistoryPresentation.Select(state with { Recent = [new(emoji)], RecentReposComboMinWidth = 130 }, text => text.Length * 4, "/home/alice").Recent.Single().Caption;
        Assert.That(unicode, Does.EndWith("/WorkingDirectory"));
        for (int index = 0; index < unicode.Length; index++)
        {
            if (char.IsHighSurrogate(unicode[index])) { Assert.That(index + 1 < unicode.Length && char.IsLowSurrogate(unicode[++index]), Is.True); }
            else { Assert.That(char.IsLowSurrogate(unicode[index]), Is.False); }
        }
        Assert.That(state.Recent.Single().Path, Is.EqualTo(path));
    }

    [Test]
    public void HistoryCapacityCountsAnchorsNeverEvictsPinsAndSerializesTheNewOptionsWithoutLosingLegacyPins()
    {
        string root = Path.Combine(Path.GetTempPath(), "gitextensions-history-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            RepositoryHistoryStore store = new(root);
            RepositoryHistoryState state = new()
            {
                Recent = [new("/work/A"), new RepositoryHistoryEntry("/work/pin").WithAnchor(RepositoryHistoryAnchor.Top), new("/work/recent", Pinned: true), new("/work/a")],
                Favorites = [new("/work/A", " literal category ")], HistorySize = 2, MaxTopRepositories = 17,
                RecentReposComboMinWidth = 140, ShorteningStrategy = RepositoryPathShorteningStrategy.MiddleDots, SortTopRepos = true, SortRecent = true,
                HideTopRepositoriesFromRecentList = true
            };
            Assert.That(state.Visit("/new/visit").Recent.Select(entry => entry.Path), Is.EqualTo(new[] { "/work/pin", "/work/recent" }));
            store.Save(state); RepositoryHistoryState loaded = store.Load();
            Assert.That(loaded.Recent.Select(entry => entry.Path), Is.EqualTo(new[] { "/work/pin", "/work/recent" }));
            Assert.That(loaded.Favorites.Single().Category, Is.EqualTo(" literal category "));
            Assert.That(loaded.MaxTopRepositories, Is.EqualTo(17)); Assert.That(loaded.ShorteningStrategy, Is.EqualTo(RepositoryPathShorteningStrategy.MiddleDots));
            Assert.That(loaded.RecentReposComboMinWidth, Is.EqualTo(140)); Assert.That(loaded.SortTopRepos && loaded.SortRecent && loaded.HideTopRepositoriesFromRecentList, Is.True);
            Assert.That(File.ReadAllText(store.FilePath), Does.Contain("\"Top\"").And.Contain("\"MiddleDots\"").And.Not.Contain("EffectiveAnchor"));
            RepositoryHistoryState duplicates = new() { Recent = [new("/work/duplicate"), new("/work/duplicate", Pinned: true), new("/work/Case"), new("/work/case")] };
            Assert.That(duplicates.Normalize().Recent.Count, Is.EqualTo(3));
            Assert.That(duplicates.Normalize().Recent[0].EffectiveAnchor, Is.EqualTo(RepositoryHistoryAnchor.Recent));
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, "{\"Recent\":[{\"Path\":\"/legacy/pinned\",\"Pinned\":true}],\"HistorySize\":30}");
            Assert.That(store.Load().Recent.Single().EffectiveAnchor, Is.EqualTo(RepositoryHistoryAnchor.Recent));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [AvaloniaTest]
    public async Task OriginalOptionsAndTwoPreviewListsRenderRealRepositoryNamesAndPinsInBothThemes()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        string literal = Path.Combine(fixture.Root, "repo\n\t\"$(literal);雪");
        GitRepository repository = await GitRepository.InitAsync(literal);
        string missing = Path.Combine(fixture.Root, "missing directory");
        RepositoryHistoryState state = new()
        {
            Recent = [new RepositoryHistoryEntry(fixture.Repository.Path).WithAnchor(RepositoryHistoryAnchor.Top), new RepositoryHistoryEntry(repository.Path).WithAnchor(RepositoryHistoryAnchor.Recent), new(missing)],
            Favorites = [new(repository.Path, " category ")], MaxTopRepositories = 1, HideTopRepositoriesFromRecentList = true,
            ShorteningStrategy = RepositoryPathShorteningStrategy.MostSignDir
        };
        string before = JsonSerializer.Serialize(state);
        RecentRepositoriesSettingsDialog dialog = new(state); dialog.Show();
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                dialog.RequestedThemeVariant = theme; Capture(dialog, $"classic-recent-settings-{theme}.png");
                ListBox top = Field<ListBox>(dialog, "TopLB"), recent = Field<ListBox>(dialog, "AllRecentLB");
                Assert.That(top.Bounds.X + ((Control)top.Parent!).Bounds.X, Is.GreaterThanOrEqualTo(350));
                Assert.That(recent.Bounds.Y, Is.GreaterThan(top.Bounds.Bottom));
                Assert.That(top.Items.OfType<RepositoryHistoryRow>().Single().Path, Is.EqualTo(fixture.Repository.Path));
                Assert.That(recent.Items.OfType<RepositoryHistoryRow>().Select(row => row.Path), Is.EqualTo(new[] { repository.Path, missing }));
                TextBlock anchored = recent.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text?.Contains("$(literal)", StringComparison.Ordinal) == true);
                Assert.That(anchored.FontWeight, Is.EqualTo(FontWeight.Bold));
                Assert.That(anchored.Text, Does.Contain("\\n\\t"));
                Assert.That(anchored.Bounds.Width, Is.GreaterThanOrEqualTo(anchored.DesiredSize.Width - anchored.Margin.Left - anchored.Margin.Right));
                Point noteOrigin = Field<TextBlock>(dialog, "comboMinWidthNote").TranslatePoint(default, dialog)!.Value;
                Point buttonOrigin = Field<Button>(dialog, "Ok").TranslatePoint(default, dialog)!.Value;
                Assert.That(noteOrigin.Y + Field<TextBlock>(dialog, "comboMinWidthNote").Bounds.Height, Is.LessThan(buttonOrigin.Y), "The original compact fields must fit above the action buttons.");
                foreach (Control option in dialog.GetVisualDescendants().Where(control => control is CheckBox or RadioButton).Cast<Control>())
                {
                    Control mark = option.GetVisualDescendants().OfType<Control>().Single(control => control.Name == (option is CheckBox ? "NormalRectangle" : "OuterEllipse"));
                    Point origin = mark.TranslatePoint(default, option)!.Value;
                    Assert.That(origin.Y, Is.GreaterThanOrEqualTo(0));
                    Assert.That(origin.Y + mark.Bounds.Height, Is.LessThanOrEqualTo(option.Bounds.Height), "The full selection glyph must remain visible within the compact option.");
                }
                Assert.That(dialog.GetVisualDescendants().OfType<NumericUpDown>().Count(), Is.EqualTo(3));
                Assert.That(Field<NumericUpDown>(dialog, "_NO_TRANSLATE_RecentRepositoriesHistorySize").Minimum, Is.EqualTo(10));
                Assert.That(Field<NumericUpDown>(dialog, "_NO_TRANSLATE_RecentRepositoriesHistorySize").Maximum, Is.EqualTo(999));
                Assert.That(Field<NumericUpDown>(dialog, "_NO_TRANSLATE_maxRecentRepositories").Maximum, Is.EqualTo(1_000_000));
                foreach (NumericUpDown number in dialog.GetVisualDescendants().OfType<NumericUpDown>())
                {
                    TextBox value = number.GetVisualDescendants().OfType<TextBox>().Single();
                    Assert.That(value.Bounds.Width, Is.GreaterThanOrEqualTo(40), "Spinner chrome consumed the number's entire input area.");
                    Assert.That(value.Text, Is.EqualTo(number.Value!.Value.ToString("0", CultureInfo.CurrentCulture)));
                    AssertNumberGlyphs(dialog, value);
                }
            }
            Assert.That(JsonSerializer.Serialize(state), Is.EqualTo(before));
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task MultiSelectionAnchorRemovalAndCancelChangeOnlyTheDraftAndNeverDeleteRepositoryFiles()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        string second = Path.Combine(fixture.Root, " second literal repo "); Directory.CreateDirectory(second);
        string retained = Path.Combine(second, "kept.txt"); File.WriteAllText(retained, "bytes stay unchanged");
        RepositoryHistoryState state = new() { Recent = [new(fixture.Repository.Path), new(second)], Favorites = [new(second, " category ")] };
        RepositoryHistoryStore store = new(); store.Save(state); string stored = File.ReadAllText(store.FilePath);
        RecentRepositoriesSettingsDialog dialog = new(state); dialog.Show();
        try
        {
            ListBox recent = Field<ListBox>(dialog, "AllRecentLB"); Select(recent, fixture.Repository.Path, second);
            ClickMenu(recent, "anchorToTopReposToolStripMenuItem");
            Assert.That(dialog.CurrentHistory.Recent.All(entry => entry.EffectiveAnchor == RepositoryHistoryAnchor.Top), Is.True);
            Assert.That(dialog.Presentation.Top.Count, Is.EqualTo(2));
            ListBox top = Field<ListBox>(dialog, "TopLB"); Select(top, second); ClickMenu(top, "removeAnchorToolStripMenuItem");
            Assert.That(dialog.CurrentHistory.Recent.Single(entry => entry.Path == second).EffectiveAnchor, Is.EqualTo(RepositoryHistoryAnchor.None));
            Select(recent, second); ClickMenu(recent, "removeRecentToolStripMenuItem");
            Assert.That(dialog.CurrentHistory.Recent.Select(entry => entry.Path), Is.EqualTo(new[] { fixture.Repository.Path }));
            Assert.That(dialog.CurrentHistory.Favorites.Single().Path, Is.EqualTo(second));
            Click(dialog, "Abort");
            Assert.That(dialog.ReturnedHistory, Is.Null); Assert.That(state.Recent.Count, Is.EqualTo(2));
            Assert.That(File.ReadAllText(store.FilePath), Is.EqualTo(stored)); Assert.That(File.ReadAllText(retained), Is.EqualTo("bytes stay unchanged"));
            Assert.That((await fixture.Repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task OkReturnsIndependentHistoryAndCallerPersistsAllOptionsAndLiteralFavorites()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        RepositoryHistoryState state = new() { Recent = [new(fixture.Repository.Path)], Favorites = [new(fixture.Repository.Path, " catégorie literal ")] };
        RepositoryHistoryStore store = new(); store.Save(state); string stored = File.ReadAllText(store.FilePath);
        Window owner = new() { Width = 300, Height = 100 }; owner.Show();
        RecentRepositoriesSettingsDialog dialog = new(state);
        Task<RepositoryHistoryState?> modal = dialog.ShowDialog<RepositoryHistoryState?>(owner);
        try
        {
            Field<NumericUpDown>(dialog, "_NO_TRANSLATE_maxRecentRepositories").Value = 7;
            Field<NumericUpDown>(dialog, "_NO_TRANSLATE_RecentRepositoriesHistorySize").Value = 12;
            Field<NumericUpDown>(dialog, "comboMinWidthEdit").Value = 120;
            Field<CheckBox>(dialog, "sortTopRepos").IsChecked = true; Field<CheckBox>(dialog, "sortRecentRepos").IsChecked = true;
            Field<CheckBox>(dialog, "hideTopRepositoriesFromRecentList").IsChecked = true;
            Field<RadioButton>(dialog, "middleDotRB").IsChecked = true;
            Assert.That(File.ReadAllText(store.FilePath), Is.EqualTo(stored));
            Click(dialog, "Ok"); RepositoryHistoryState result = (await modal)!;
            Assert.That(result, Is.SameAs(dialog.ReturnedHistory)); Assert.That(result, Is.Not.SameAs(state));
            Assert.That(result.Recent, Is.Not.SameAs(state.Recent)); Assert.That(result.HistorySize, Is.EqualTo(12));
            Assert.That(result.MaxTopRepositories, Is.EqualTo(7)); Assert.That(result.RecentReposComboMinWidth, Is.EqualTo(120));
            Assert.That(result.ShorteningStrategy, Is.EqualTo(RepositoryPathShorteningStrategy.MiddleDots));
            Assert.That(result.SortTopRepos && result.SortRecent && result.HideTopRepositoriesFromRecentList, Is.True);
            Assert.That(result.Favorites.Single().Category, Is.EqualTo(" catégorie literal "));
            Assert.That(File.ReadAllText(store.FilePath), Is.EqualTo(stored), "The dialog returns state; its owner decides when to persist it.");
            store.Save(result); Assert.That(store.Load().RecentReposComboMinWidth, Is.EqualTo(120));
            Assert.That(state.HistorySize, Is.EqualTo(30)); Assert.That(state.ShorteningStrategy, Is.EqualTo(RepositoryPathShorteningStrategy.None));
        }
        finally { if (dialog.IsVisible) dialog.Close(); owner.Close(); }
    }

    [AvaloniaTest]
    public async Task MinimumWidthSkipsForbiddenRangeAndEscapeAndWindowCloseDiscardTheirEdits()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        RepositoryHistoryState state = new() { Recent = [new(fixture.Repository.Path)] };
        RecentRepositoriesSettingsDialog dialog = new(state); dialog.Show();
        try
        {
            NumericUpDown width = Field<NumericUpDown>(dialog, "comboMinWidthEdit");
            width.Value = 1; Assert.That(width.Value, Is.EqualTo(30));
            width.Value = 29; Assert.That(width.Value, Is.EqualTo(0));
            NumericUpDown top = Field<NumericUpDown>(dialog, "_NO_TRANSLATE_maxRecentRepositories");
            Render(dialog);
            top.GetVisualDescendants().OfType<RepeatButton>().Single(button => button.Name == "PART_IncreaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(top.Value, Is.EqualTo(1));
            top.GetVisualDescendants().OfType<RepeatButton>().Single(button => button.Name == "PART_DecreaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(top.Value, Is.EqualTo(0));
            width.Value = 800; Assert.That(dialog.CurrentHistory.RecentReposComboMinWidth, Is.EqualTo(800));
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            if (dialog.IsVisible) { dialog.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); }
            Assert.That(dialog.IsVisible, Is.False); Assert.That(dialog.ReturnedHistory, Is.Null);
            RecentRepositoriesSettingsDialog closed = new(state); closed.Show();
            Field<CheckBox>(closed, "sortTopRepos").IsChecked = true; closed.Close();
            Assert.That(closed.ReturnedHistory, Is.Null); Assert.That(state.RecentReposComboMinWidth, Is.EqualTo(0)); Assert.That(state.SortTopRepos, Is.False);
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }

    [AvaloniaTest]
    public async Task DoubleClickMovesAnAnchorBetweenOriginalTopAndRecentLists()
    {
        using ProxyGourceFixture fixture = await ProxyGourceFixture.CreateAsync();
        RepositoryHistoryState state = new() { Recent = [new RepositoryHistoryEntry(fixture.Repository.Path).WithAnchor(RepositoryHistoryAnchor.Top)], HideTopRepositoriesFromRecentList = true };
        RecentRepositoriesSettingsDialog dialog = new(state); dialog.Show();
        try
        {
            DoubleClick(Field<ListBox>(dialog, "TopLB"));
            Assert.That(dialog.CurrentHistory.Recent.Single().EffectiveAnchor, Is.EqualTo(RepositoryHistoryAnchor.Recent));
            Assert.That(dialog.Presentation.Top, Is.Empty);
            DoubleClick(Field<ListBox>(dialog, "AllRecentLB"));
            Assert.That(dialog.CurrentHistory.Recent.Single().EffectiveAnchor, Is.EqualTo(RepositoryHistoryAnchor.Top));
            Assert.That(dialog.Presentation.Recent, Is.Empty);
        }
        finally { dialog.Close(); }
        void DoubleClick(ListBox list)
        {
            Render(dialog);
            ListBoxItem row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
            Point point = row.TranslatePoint(new Point(12, 10), dialog)!.Value;
            dialog.MouseMove(point);
            for (int click = 0; click < 2; click++) { dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left); }
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Field<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window dialog, string name) => Field<Button>(dialog, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Select(ListBox list, params string[] paths)
    {
        list.SelectedItems!.Clear(); foreach (RepositoryHistoryRow row in list.Items.OfType<RepositoryHistoryRow>().Where(row => paths.Contains(row.Path, StringComparer.Ordinal))) list.SelectedItems.Add(row);
    }
    private static void ClickMenu(ListBox list, string name)
        => list.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Render(Window dialog) { dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Capture(Window dialog, string name)
    {
        Render(dialog); using var frame = dialog.CaptureRenderedFrame() ?? throw new AssertionException("No native frame.");
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame.Save(path); TestContext.AddTestAttachment(path);
    }

    private static void AssertNumberGlyphs(Window dialog, TextBox box)
    {
        using var frame = dialog.CaptureRenderedFrame() ?? throw new AssertionException("No numeric frame.");
        using MemoryStream stream = new(); frame.Save(stream); stream.Position = 0;
        using SkiaSharp.SKBitmap pixels = SkiaSharp.SKBitmap.Decode(stream);
        Point origin = box.TranslatePoint(default, dialog)!.Value;
        Color foreground = ((ISolidColorBrush)box.Foreground!).Color;
        SkiaSharp.SKColor text = new(foreground.R, foreground.G, foreground.B, foreground.A);
        int count = 0;
        for (int y = (int)origin.Y + 3; y < origin.Y + box.Bounds.Height - 3; y++)
            for (int x = (int)origin.X + 2; x < origin.X + box.Bounds.Width - 2; x++)
            {
                SkiaSharp.SKColor pixel = pixels.GetPixel(x, y);
                // Thin 12px numerals can contain only antialiased pixels; the empty field cannot.
                if (Math.Abs(pixel.Red - text.Red) <= 128 && Math.Abs(pixel.Green - text.Green) <= 128 && Math.Abs(pixel.Blue - text.Blue) <= 128) count++;
            }
        Assert.That(count, Is.GreaterThan(5), $"The actual number glyphs must be visible inside the compact text area in each theme ({text}, {box.Text}).");
    }
}
