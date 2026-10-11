using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Extensibility.Git;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class FileStatusListViewTests
{
    [AvaloniaTest]
    public async Task RegexMatchesOldOrCurrentLiteralNamesAndInvalidInputRetainsLastGoodListAndSelection()
    {
        using Fixture fixture = new();
        const string literal = "src/λ[1]; $(touch nope)\n?.cs";
        object tag = new(), range = new();
        fixture.View.SetGroups([new(new object(), "first", new TextBlock { Text = "First" }, [new(tag, literal, "legacy/old?.cs", 'R', 100), Entry("src/other.txt")]),
            new(range, "range", new TextBlock { Text = "Range diff" }, [], AlwaysVisible: true)]);
        fixture.Window.Show(); fixture.View.SelectTags([tag]);
        await Filter(fixture.View, "^legacy/old\\?\\.cs$", 1);
        Assert.That(fixture.View.FilteredEntries.Single().Path, Is.EqualTo(literal)); Assert.That(fixture.View.SelectedTags, Is.EqualTo(new[] { tag }));
        fixture.View.FilterText = "["; await Until(() => fixture.View.FilterError is not null);
        Assert.That(fixture.View.FilteredEntries.Single().Tag, Is.SameAs(tag)); Assert.That(fixture.View.SelectedTags, Is.EqualTo(new[] { tag }));
        Assert.That(fixture.View.FindItemByTag(range), Is.Not.Null); Assert.That(fixture.View.FilterHistory, Does.Not.Contain("["));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Window.RequestedThemeVariant = theme; using WriteableBitmap frame = Capture(fixture.Window, "file-status-filter-invalid-" + theme + ".png");
            AssertColor(frame, theme == ThemeVariant.Dark ? "#6e1919" : "#ffc8c8", 100);
        }
        await Filter(fixture.View, "^" + Regex.Escape(literal) + "$", 1);
        Assert.That(fixture.View.FindItemByTag(tag)!.Tag, Is.SameAs(tag));
        await Filter(fixture.View, "does-not-match", 0);
        Assert.That(fixture.View.FindItemByTag(range), Is.Not.Null); Assert.That(fixture.View.FilteredEntries, Is.Empty);
        Assert.That(fixture.View.SelectedTags, Is.Empty);
        fixture.View.ClearFilter(); Assert.That(fixture.View.FilteredEntries.Count, Is.EqualTo(2)); Assert.That(fixture.View.FilterError, Is.Null);
    }

    [AvaloniaTest]
    public async Task EditableKeyboardDropdownAndSessionHistoryKeepTenMatchedExpressionsWithoutPromotingDuplicates()
    {
        using Fixture fixture = new();
        fixture.View.SetGroups([Group("files", Enumerable.Range(0, 12).Select(index => Entry($"nested/file{index:D2}.txt")).ToArray())]);
        fixture.Window.Show();
        ComboBox combo = Field<ComboBox>(fixture.Window, "FilesFilter"); TextBox editor = combo.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.That(combo.IsEditable, Is.True); editor.Focus(); fixture.Window.KeyTextInput("file00");
        await Until(() => fixture.View.FilterHistory.First() == "file00");
        for (int index = 1; index < 12; index++) { await Filter(fixture.View, $"file{index:D2}", 1); }
        string[] before = fixture.View.FilterHistory.ToArray();
        Assert.That(before, Is.EqualTo(Enumerable.Range(2, 10).Reverse().Select(index => $"file{index:D2}")));
        combo.SelectedItem = before[4]; await Until(() => fixture.View.FilterText == before[4] && fixture.View.FilteredEntries.Single().Path.EndsWith(before[4] + ".txt", StringComparison.Ordinal));
        Assert.That(fixture.View.FilterHistory, Is.EqualTo(before));
        await Filter(fixture.View, "no-match", 0); Assert.That(fixture.View.FilterHistory, Is.EqualTo(before));
        Click(Field<Button>(fixture.Window, "FilesClearFilter")); Assert.That(fixture.View.FilteredEntries.Count, Is.EqualTo(12));
        Assert.That(fixture.View.FilterHistory, Is.EqualTo(before), "Clearing the display filter does not create an empty MRU item.");
        int changes = 0; fixture.View.DisplayChanged += (_, _) => changes++;
        fixture.View.FilterText = "file01"; fixture.View.Dispose(); await Task.Delay(350); Dispatcher.UIThread.RunJobs(); Assert.That(changes, Is.Zero, "A closed view must not execute a delayed rebuild.");
    }

    [AvaloniaTest]
    public void LogicalFolderAndLeafSelectionIgnoresCollapseAndPreservesRawGroupIdentityAcrossRefreshAndSort()
    {
        using Fixture fixture = new();
        object first = new(), second = new(), otherGroup = new();
        fixture.View.SetGroups([Group("A", [new(first, "src/one.txt"), new(second, "src/two.txt"), Entry("unused/other.txt")]), Group("B", [new(otherGroup, "src/one.txt")])]);
        fixture.Window.Show(); fixture.View.ClearSelection();
        TreeViewItem folder = Rows(fixture.View.Tree).Single(item => item.Tag is FileStatusListNodeTag { GroupKey: "A", Kind: FileStatusListNodeKind.Folder, Key: "src" });
        folder.IsExpanded = false; fixture.View.Tree.SelectedItems!.Add(folder); fixture.View.Tree.SelectedItems.Add(fixture.View.FindItemByTag(first)!);
        Assert.That(fixture.View.SelectedTags, Is.EquivalentTo(new[] { first, second })); Assert.That(fixture.View.FilteredEntries.Count, Is.EqualTo(4));
        fixture.View.SelectTags([first]); Assert.That(folder.IsExpanded, Is.True);
        TreeViewItem unused = Rows(fixture.View.Tree).Single(item => item.Tag is FileStatusListNodeTag { GroupKey: "A", Kind: FileStatusListNodeKind.Folder, Key: "unused" }); unused.IsExpanded = false;
        object replacement = new();
        fixture.View.SetGroups([Group("A", [new(replacement, "src/one.txt"), new(second, "src/two.txt"), Entry("unused/other.txt")]), Group("B", [new(otherGroup, "src/one.txt")])]);
        Assert.That(fixture.View.SelectedTags, Is.EqualTo(new[] { replacement }));
        Assert.That(Rows(fixture.View.Tree).Single(item => item.Tag is FileStatusListNodeTag { GroupKey: "A", Key: "unused" }).IsExpanded, Is.False);
        fixture.View.SortMode = FileStatusListSortMode.FilePathFlat; Assert.That(fixture.View.SelectedTags, Is.EqualTo(new[] { replacement }));
        fixture.View.SortMode = FileStatusListSortMode.FilePath;
        Assert.That(fixture.View.SelectedTags, Is.EqualTo(new[] { replacement }));
        Assert.That(Rows(fixture.View.Tree).Single(item => item.Tag is FileStatusListNodeTag { GroupKey: "A", Key: "unused" }).IsExpanded, Is.False);
    }

    [AvaloniaTest]
    public async Task SharedPersistedOptionsUpdateExistingViewsButFiltersAndStatusTogglesRemainLocal()
    {
        using Fixture fixture = new(); TreeView otherTree = new() { Name = "Other" };
        using FileStatusListView other = new(otherTree, preferencesStore: new FileStatusListPreferencesStore(fixture.Directory));
        FileStatusListEntry[] entries = [Entry("nested/a.cs", DiffBranchStatus.OnlyAChange), Entry("nested/b.txt", DiffBranchStatus.OnlyBChange)];
        fixture.View.SetGroups([Group("files", entries) with { IsBaseComparison = true }]); other.SetGroups([Group("files", entries) with { IsBaseComparison = true }]);
        fixture.Window.Content = null;
        Grid columns = new() { ColumnDefinitions = new ColumnDefinitions("*,*") }; columns.Children.Add(fixture.View); Grid.SetColumn(other, 1); columns.Children.Add(other); fixture.Window.Content = columns; fixture.Window.Show();
        fixture.View.SortMode = FileStatusListSortMode.FileExtensionFlat; fixture.View.DenseTree = true; fixture.View.ShowGroupNodesInFlatList = true; fixture.View.FilterFileNameOnly = true;
        Assert.That(other.SortMode, Is.EqualTo(FileStatusListSortMode.FileExtensionFlat)); Assert.That(other.DenseTree && other.ShowGroupNodesInFlatList && other.FilterFileNameOnly, Is.True);
        await Filter(fixture.View, "^a\\.cs$", 1); Assert.That(other.FilteredEntries.Count, Is.EqualTo(2));
        ToggleButton a = Field<ToggleButton>(fixture.Window, "FilesOnlyA"); a.IsChecked = false; Click(a);
        Assert.That(fixture.View.FilteredEntries, Is.Empty); Assert.That(other.FilteredEntries.Count, Is.EqualTo(2));
        using FileStatusListView reopened = new(new TreeView(), preferencesStore: new FileStatusListPreferencesStore(fixture.Directory));
        Assert.That(reopened.SortMode, Is.EqualTo(FileStatusListSortMode.FileExtensionFlat)); Assert.That(reopened.DenseTree && reopened.FilterFileNameOnly, Is.True);
        Assert.That(reopened.FilterText, Is.Empty); Assert.That(reopened.FilterHistory.Count, Is.EqualTo(2));
        Assert.That(File.ReadAllText(fixture.Store.FilePath), Does.Not.Contain("^a"));
    }

    [AvaloniaTest]
    public async Task RepeatedFiltersAndAllSortModesReparentHeadersSafelyAndReportShownTotalWithOriginalSmallGroupExpansion()
    {
        using Fixture fixture = new(); object range = new();
        FileStatusListGroup[] groups = Enumerable.Range(0, 3).Select(index => Group("group" + index, [Entry($"src{index}/a.cs"), Entry($"src{index}/b.txt")]) with
        { HeaderFactory = (shown, total) => new TextBlock { Text = $"({shown}/{total}) group{index}" } }).Concat([new FileStatusListGroup(range, "range", new TextBlock { Text = "Range" }, [], AlwaysVisible: true)]).ToArray();
        fixture.View.SetGroups(groups); fixture.Window.Show();
        Assert.That(fixture.View.Tree.Items.OfType<TreeViewItem>().Take(3).All(item => item.IsExpanded), Is.True);
        foreach (FileStatusListSortMode mode in Enum.GetValues<FileStatusListSortMode>())
        {
            fixture.View.SortMode = mode; await Filter(fixture.View, "\\.cs$", 3);
            Assert.That(fixture.View.Tree.Items.OfType<TreeViewItem>().Take(3).Select(item => ((TextBlock)item.Header!).Text), Is.EqualTo(new[] { "(1/2) group0", "(1/2) group1", "(1/2) group2" }));
            fixture.View.ClearFilter(); Assert.That(fixture.View.FilteredEntries.Count, Is.EqualTo(6)); Assert.That(fixture.View.FindItemByTag(range), Is.Not.Null);
        }
        // Also exercise a caller-supplied Control reused by identity, rather than only fresh header factories.
        FileStatusListGroup first = Group("stable", [Entry("stable/a.cs"), Entry("stable/b.txt")]);
        fixture.View.SetGroups([first, Group("other", [Entry("other/a.cs")])]);
        await Filter(fixture.View, @"a\.cs$", 2); fixture.View.SortMode = FileStatusListSortMode.FilePath; fixture.View.ClearFilter();
        Assert.That(fixture.View.Tree.Items.OfType<TreeViewItem>().First().Header, Is.SameAs(first.Header));
    }

    [AvaloniaTest]
    public async Task OriginalCompactToolbarFilterAndIconsRenderClearlyInBothThemesAndMenusContainNoLiteralAmpersands()
    {
        using Fixture fixture = new(); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        using FileStatusListView other = new(new TreeView { Name = "Other" }, preferencesStore: new FileStatusListPreferencesStore(fixture.Directory));
        other.SetGroups([Group("other", [Entry("src/other.cs")])]);
        fixture.Window.Content = null; fixture.Window.Width = 1040;
        Grid columns = new() { ColumnDefinitions = new ColumnDefinitions("*,*") };
        columns.Children.Add(fixture.View); Grid.SetColumn(other, 1); columns.Children.Add(other); fixture.Window.Content = columns;
        int hostActionInvocations = 0;
        MenuItem hostAction = new() { Header = "Host option" };
        hostAction.Click += (_, _) => hostActionInvocations++;
        fixture.View.SettingsMenu.Items.Add(hostAction);
        fixture.View.SetGroups([Group("A", [Entry("src/modified.cs", DiffBranchStatus.UnequalChange), Entry("src/first.cs", DiffBranchStatus.OnlyAChange)] ) with { IsBaseComparison = true }, Group("B", [Entry("docs/readme.txt", DiffBranchStatus.OnlyBChange), Entry("docs/same.txt", DiffBranchStatus.SameChange)])]);
        fixture.Window.Show();
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            fixture.Window.RequestedThemeVariant = theme; using WriteableBitmap frame = Capture(fixture.Window, "file-status-list-" + theme + ".png");
            Assert.That(fixture.View.Toolbar.Bounds.Height, Is.EqualTo(24));
            ComboBox combo = Field<ComboBox>(fixture.Window, "FilesFilter"); Assert.That(combo.Bounds.Height, Is.EqualTo(24)); Assert.That(combo.Bounds.Width, Is.GreaterThan(350));
            TextBlock watermark = combo.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Name == "PART_Watermark");
            Assert.That(watermark.Text, Is.EqualTo("Filter files using a regular expression..."));
            Assert.That(watermark.IsEffectivelyVisible && watermark.Bounds.Height > 0, Is.True, "The editable combo must render the original filter hint.");
            Rect hintBounds = new(watermark.TranslatePoint(default, fixture.Window)!.Value, watermark.Bounds.Size);
            AssertColorGlyph(frame, hintBounds, ((ISolidColorBrush)watermark.Foreground!).Color, theme == ThemeVariant.Dark ? Color.Parse("#323232") : Colors.White, minimum: 100);
            foreach (string role in new[] { "Unequal", "OnlyB", "OnlyA", "Same" })
            {
                ToggleButton toggle = Field<ToggleButton>(fixture.Window, "Files" + role);
                ContentPresenter presenter = toggle.GetVisualDescendants().OfType<ContentPresenter>().Single(item => item.Name == "PART_ContentPresenter");
                Color foreground = ((ISolidColorBrush)presenter.Foreground!).Color, background = ((ISolidColorBrush)presenter.Background!).Color;
                Assert.That(toggle.IsChecked, Is.True); Assert.That(foreground, Is.Not.EqualTo(Colors.White));
                Assert.That(background, Is.EqualTo(theme == ThemeVariant.Dark ? Color.Parse("#323232") : Colors.White), "Status indicators use original neutral toolbar surfaces so their semantic hues stay visible.");
                Assert.That(((ISolidColorBrush)presenter.BorderBrush!).Color, Is.EqualTo(theme == ThemeVariant.Dark ? Color.Parse("#2864b4") : Color.Parse("#0078d7")), "A checked status indicator remains visibly marked by the original highlight border.");
                Assert.That(role switch { "Unequal" => foreground.R > foreground.G && foreground.R > foreground.B,
                    "OnlyB" => foreground.B > foreground.R && foreground.R > foreground.G,
                    "OnlyA" => foreground.R == foreground.G && foreground.G > foreground.B,
                    _ => foreground.G > foreground.R && foreground.G > foreground.B }, Is.True, "Each checked indicator must keep its original status hue.");
                Assert.That(Contrast(foreground, background), Is.GreaterThanOrEqualTo(4.5));
                Rect glyphBounds = new(presenter.TranslatePoint(default, fixture.Window)!.Value, presenter.Bounds.Size);
                AssertColorGlyph(frame, glyphBounds.Deflate(3), foreground, background, minimum: 4);
            }
            Assert.That(Rows(fixture.View.Tree).Where(item => item.IsEffectivelyVisible).All(item => item.Bounds.Height >= 24), Is.True);
            SplitButton split = Field<SplitButton>(fixture.Window, "FilesViewMode");
            Assert.That(split.GetVisualDescendants().OfType<Image>().Single().Bounds.Width, Is.EqualTo(16));
            split.Flyout!.ShowAt(split); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            MenuFlyout menu = (MenuFlyout)split.Flyout;
            Assert.That(menu.ItemsSource!.OfType<MenuItem>().Select(item => item.Header).OfType<AccessText>().All(text => !text.Text!.Contains('&')), Is.True);
            AccessText caption = menu.ItemsSource!.OfType<MenuItem>().Select(item => item.Header).OfType<AccessText>().First();
            TopLevel popupHost = (TopLevel)caption.GetVisualRoot()!;
            using WriteableBitmap menuFrame = Capture(popupHost, "file-status-sort-popup-" + theme + ".png");
            Assert.That(caption.Bounds.Height, Is.GreaterThan(0)); split.Flyout.Hide(); Dispatcher.UIThread.RunJobs(); await Task.Delay(100);
            Button settings = Field<Button>(fixture.Window, "FilesSettings");
            settings.Flyout!.ShowAt(settings); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            MenuItem toolbar = fixture.View.SettingsMenu.Items.OfType<MenuItem>().Single(item => item.Name == "FilesToolbarOptions");
            await Until(() => toolbar.GetVisualRoot() is TopLevel && toolbar.Bounds.Height > 0 && settings.Flyout.IsOpen);
            Assert.That(toolbar.IsEffectivelyVisible, Is.True);
            using WriteableBitmap settingsFrame = Capture((TopLevel)toolbar.GetVisualRoot()!, "file-status-settings-popup-" + theme + ".png");
            Assert.That(hostAction.Bounds.Height, Is.GreaterThan(0), "Items appended by the host must mount in the same visible flyout.");
            toolbar.IsSubMenuOpen = true; fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            MenuItem byStatus = toolbar.Items.OfType<MenuItem>().Single(item => item.Header as string == "Group by diff status");
            await Until(() => byStatus.GetVisualRoot() is TopLevel && byStatus.Bounds.Height > 0);
            Assert.That(byStatus.IsEffectivelyVisible, Is.True);
            using WriteableBitmap visibilityFrame = Capture((TopLevel)byStatus.GetVisualRoot()!, "file-status-toolbar-popup-" + theme + ".png");
            ClickRendered(byStatus);
            Assert.That(Field<ToggleButton>(fixture.Window, "FilesByStatus").IsVisible, Is.False);
            Assert.That(Field<ToggleButton>(fixture.Window, "OtherByStatus").IsVisible, Is.False, "Toolbar visibility is shared by existing instances.");
            settings.Flyout.Hide();
            settings.Flyout.ShowAt(settings); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            toolbar.IsSubMenuOpen = true; fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            byStatus = toolbar.Items.OfType<MenuItem>().Single(item => item.Header as string == "Group by diff status");
            await Until(() => byStatus.GetVisualRoot() is TopLevel && byStatus.Bounds.Height > 0);
            Assert.That(byStatus.IsChecked, Is.False); ClickRendered(byStatus);
            Assert.That(Field<ToggleButton>(fixture.Window, "FilesByStatus").IsVisible, Is.True);
            Assert.That(Field<ToggleButton>(fixture.Window, "OtherByStatus").IsVisible, Is.True);
            settings.Flyout.Hide(); settings.Flyout.ShowAt(settings); fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            await Until(() => hostAction.GetVisualRoot() is TopLevel && hostAction.Bounds.Height > 0);
            ClickRendered(hostAction);
            Assert.That(hostActionInvocations, Is.EqualTo(theme == ThemeVariant.Light ? 1 : 2));
            settings.Flyout.Hide();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    public void OrangeFluentAccentCannotReplaceRenderedSortButtonStatesInEitherTheme()
    {
        FluentTheme fluent = Application.Current!.Styles.OfType<FluentTheme>().Single();
        fluent.Palettes.TryGetValue(ThemeVariant.Light, out ColorPaletteResources? previousLight);
        fluent.Palettes.TryGetValue(ThemeVariant.Dark, out ColorPaletteResources? previousDark);
        Color orange = Color.Parse("#e95420");
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = orange };
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = orange };
        try
        {
            using Fixture fixture = new();
            fixture.View.SetGroups([Group("files", [Entry("src/a.cs", DiffBranchStatus.OnlyAChange), Entry("src/b.txt", DiffBranchStatus.OnlyBChange)]) with { IsBaseComparison = true }]);
            fixture.Window.Show();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                fixture.Window.RequestedThemeVariant = theme; fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.That(((IResourceNode)fluent).TryGetResource("SystemAccentColor", theme, out object? accent), Is.True);
                Assert.That(accent, Is.EqualTo(orange), "The underlying Fluent palette must really supply the Ubuntu accent.");
                Color highlight = Color.Parse(theme == ThemeVariant.Dark ? "#2864b4" : "#0078d7");
                fixture.View.SortMode = FileStatusListSortMode.FileStatus;
                foreach (string role in new[] { "ByPath", "ByExtension", "ByStatus" })
                {
                    ToggleButton button = Field<ToggleButton>(fixture.Window, "Files" + role);
                    ClickRendered(button);
                    fixture.Window.MouseMove(new Point(fixture.Window.Width - 4, fixture.Window.Height - 4));
                    using WriteableBitmap frame = Capture(fixture.Window, "file-status-orange-accent-" + role + "-" + theme + ".png");
                    ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single(item => item.Name == "PART_ContentPresenter");
                    Assert.That(button.IsChecked, Is.True);
                    Assert.That(((ISolidColorBrush)presenter.Background!).Color, Is.EqualTo(highlight), "The actual checked template must ignore the Fluent accent.");
                    Rect bounds = new(presenter.TranslatePoint(default, fixture.Window)!.Value, presenter.Bounds.Size);
                    Assert.That(CountColor(frame, bounds, highlight), Is.GreaterThan(80));
                    Assert.That(CountColor(frame, bounds, orange), Is.Zero);
                    Assert.That(button.Bounds.Size, Is.EqualTo(new Size(24, 24)));
                    Assert.That(presenter.CornerRadius, Is.EqualTo(new CornerRadius(0)));
                    Assert.That(button.GetVisualDescendants().OfType<Image>().Single().Bounds.Size, Is.EqualTo(new Size(16, 16)));
                    Point point = button.TranslatePoint(new Point(12, 12), fixture.Window)!.Value;
                    fixture.Window.MouseMove(point);
                    AssertRenderedState("hover");
                    fixture.Window.MouseDown(point, MouseButton.Left);
                    try { AssertRenderedState("pressed"); }
                    finally
                    {
                        fixture.Window.MouseMove(new Point(fixture.Window.Width - 4, fixture.Window.Height - 4));
                        fixture.Window.MouseUp(new Point(fixture.Window.Width - 4, fixture.Window.Height - 4), MouseButton.Left);
                    }

                    void AssertRenderedState(string state)
                    {
                        using WriteableBitmap stateFrame = Capture(fixture.Window, "file-status-orange-accent-" + role + "-" + state + "-" + theme + ".png");
                        Assert.That(((ISolidColorBrush)presenter.Background!).Color, Is.EqualTo(highlight), "Checked " + state + " must use the original palette too.");
                        Assert.That(CountColor(stateFrame, bounds, highlight), Is.GreaterThan(80));
                        Assert.That(CountColor(stateFrame, bounds, orange), Is.Zero);
                    }
                }
                Color neutral = theme == ThemeVariant.Dark ? Color.Parse("#323232") : Colors.White;
                foreach (string role in new[] { "Unequal", "OnlyB", "OnlyA", "Same" })
                {
                    ToggleButton button = Field<ToggleButton>(fixture.Window, "Files" + role);
                    ContentPresenter presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single(item => item.Name == "PART_ContentPresenter");
                    Assert.That(((ISolidColorBrush)presenter.Background!).Color, Is.EqualTo(neutral), "The four semantic indicators retain their separate neutral surfaces.");
                    Assert.That(((ISolidColorBrush)presenter.BorderBrush!).Color, Is.EqualTo(highlight));
                }
            }
        }
        finally
        {
            if (previousLight is null) { fluent.Palettes.Remove(ThemeVariant.Light); } else { fluent.Palettes[ThemeVariant.Light] = previousLight; }
            if (previousDark is null) { fluent.Palettes.Remove(ThemeVariant.Dark); } else { fluent.Palettes[ThemeVariant.Dark] = previousDark; }
        }
    }

    [AvaloniaTest]
    public async Task LiteralFilteredGitPathsRemainTheOnlyMutationTargetsEvenWhenTheirFoldersAreCollapsed()
    {
        using Fixture fixture = new(); string repository = Path.Combine(fixture.Directory, "repository"); Directory.CreateDirectory(repository);
        GitRunner git = new();
        async Task<GitResult> Run(params string[] arguments)
        { GitResult result = await git.RunAsync(repository, arguments); Assert.That(result.Success, Is.True, result.StandardError); return result; }
        await Run("init", "--initial-branch=main"); await Run("config", "user.name", "File status tester"); await Run("config", "user.email", "test@example.invalid"); await Run("config", "commit.gpgsign", "false");
        const string literal = "nested/λ[1]; $(touch nope)\n?.txt", hidden = "nested/λ1; $(touch nope)X.txt";
        Directory.CreateDirectory(Path.Combine(repository, "nested"));
        foreach (string path in new[] { literal, hidden }) { await File.WriteAllTextAsync(Path.Combine(repository, path), "before\n"); }
        await Run("add", "--all"); await Run("commit", "-m", "Initial");
        foreach (string path in new[] { literal, hidden }) { await File.WriteAllTextAsync(Path.Combine(repository, path), "after\n"); }
        GitRepository backend = await GitRepository.OpenAsync(repository);
        IReadOnlyList<GitChange> changes = (await backend.GetStatusAsync()).Changes;
        fixture.View.SetGroups([Group("working", changes.Select(change => new FileStatusListEntry(change, change.Path, change.OriginalPath, change.WorkTreeStatus)).ToArray())]); fixture.Window.Show();
        await Filter(fixture.View, "^" + Regex.Escape(literal) + "$", 1);
        foreach (TreeViewItem item in Rows(fixture.View.Tree)) { item.IsExpanded = false; }
        await backend.StageAsync(fixture.View.FilteredEntries.Select(entry => entry.Path));
        Assert.That((await Run("diff", "--cached", "--name-only", "-z")).StandardOutput, Is.EqualTo(literal + "\0"));
        Assert.That((await Run("show", ":" + hidden)).StandardOutput, Is.EqualTo("before\n"));
        await backend.StageAsync([hidden]);
        await backend.UnstageAsync(fixture.View.FilteredEntries.Select(entry => entry.Path));
        Assert.That((await Run("diff", "--cached", "--name-only", "-z")).StandardOutput, Is.EqualTo(hidden + "\0"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(repository, literal)), Is.EqualTo("after\n")); Assert.That(File.Exists(Path.Combine(repository, "nope")), Is.False);
    }

    [Test]
    public void MalformedPreferencesAndExplicitNullToolbarOptionsUseOriginalDefaults()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "gitextensions-file-status-preferences-" + Guid.NewGuid().ToString("N"));
        FileStatusListPreferencesStore store = new(temporary);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, "{bad json"); Assert.That(store.Load().SortMode, Is.EqualTo(FileStatusListSortMode.FilePath));
            File.WriteAllText(store.FilePath, "{\"SortMode\":3,\"HiddenToolbarItems\":null}"); Assert.That(store.Load().HiddenToolbarItems, Is.Empty);
            File.WriteAllText(store.FilePath, "{\"SortMode\":99}"); Assert.That(store.Load().SortMode, Is.EqualTo(FileStatusListSortMode.FilePath));
            Assert.That(store.Load().DenseTree || store.Load().ShowGroupNodesInFlatList || store.Load().FilterFileNameOnly, Is.False);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static FileStatusListEntry Entry(string path, DiffBranchStatus diff = DiffBranchStatus.Unknown) => new(new object(), path, DiffStatus: diff);
    private static FileStatusListGroup Group(string key, FileStatusListEntry[] entries) => new(new object(), key, new TextBlock { Text = key }, entries);
    private static IEnumerable<TreeViewItem> Rows(TreeView tree) => tree.Items.OfType<TreeViewItem>().SelectMany(Descendants);
    private static IEnumerable<TreeViewItem> Descendants(TreeViewItem item)
    { yield return item; foreach (TreeViewItem child in item.Items.OfType<TreeViewItem>().SelectMany(Descendants)) { yield return child; } }
    private static T Field<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void ClickRendered(Control control)
    {
        TopLevel host = (TopLevel)control.GetVisualRoot()!;
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host)!.Value;
        host.MouseMove(point); host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
    private static async Task Filter(FileStatusListView view, string expression, int count)
    { view.FilterText = expression; await Until(() => view.FilterError is null && view.FilteredEntries.Count == count && view.FilterText == expression && (count == 0 || view.FilterHistory.Contains(expression))); }
    private static async Task Until(Func<bool> predicate)
    { for (int i = 0; i < 300 && !predicate(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.That(predicate(), Is.True, "File-status operation did not complete within three seconds."); }
    private static WriteableBitmap Capture(TopLevel window, string name)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new AssertionException("No rendered file-status frame.");
        DirectoryInfo? root = new(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/linux/GitExtensions.Desktop/GitExtensions.Desktop.csproj"))) { root = root.Parent; }
        string directory = Path.Combine(root?.FullName ?? TestContext.CurrentContext.WorkDirectory, "artifacts/linux/screenshots"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name); frame.Save(path); TestContext.AddTestAttachment(path); return frame;
    }
    private static void AssertColor(WriteableBitmap frame, string color, int minimum)
    {
        using ILockedFramebuffer pixels = frame.Lock(); byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        Color expected = Color.Parse(color); bool bgra = pixels.Format == PixelFormat.Bgra8888; int count = 0;
        for (int y = 0; y < pixels.Size.Height; y++) { for (int x = 0; x < pixels.Size.Width; x++) { int offset = y * pixels.RowBytes + x * 4; if (bytes[offset + (bgra ? 2 : 0)] == expected.R && bytes[offset + 1] == expected.G && bytes[offset + (bgra ? 0 : 2)] == expected.B) { count++; } } }
        Assert.That(count, Is.GreaterThan(minimum), "The original validation background must reach the rendered editable text box.");
    }
    private static int CountColor(WriteableBitmap frame, Rect bounds, Color color)
    {
        using ILockedFramebuffer pixels = frame.Lock(); byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888; int count = 0;
        for (int y = Math.Max(0, (int)Math.Floor(bounds.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(bounds.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(bounds.Right)); x++)
            {
                int offset = y * pixels.RowBytes + x * 4;
                if (bytes[offset + (bgra ? 2 : 0)] == color.R && bytes[offset + 1] == color.G && bytes[offset + (bgra ? 0 : 2)] == color.B) { count++; }
            }
        }
        return count;
    }
    private static void AssertColorGlyph(WriteableBitmap frame, Rect bounds, Color foreground, Color background, int minimum)
    {
        using ILockedFramebuffer pixels = frame.Lock(); byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height]; Marshal.Copy(pixels.Address, bytes, 0, bytes.Length);
        bool bgra = pixels.Format == PixelFormat.Bgra8888;
        double dr = foreground.R - background.R, dg = foreground.G - background.G, db = foreground.B - background.B, magnitude = dr * dr + dg * dg + db * db;
        int count = 0;
        for (int y = Math.Max(0, (int)Math.Floor(bounds.Y)); y < Math.Min(pixels.Size.Height, (int)Math.Ceiling(bounds.Bottom)); y++)
        {
            for (int x = Math.Max(0, (int)Math.Floor(bounds.X)); x < Math.Min(pixels.Size.Width, (int)Math.Ceiling(bounds.Right)); x++)
            {
                int offset = y * pixels.RowBytes + x * 4; double r = bytes[offset + (bgra ? 2 : 0)], g = bytes[offset + 1], b = bytes[offset + (bgra ? 0 : 2)];
                double coverage = ((r - background.R) * dr + (g - background.G) * dg + (b - background.B) * db) / magnitude;
                if (coverage >= .25 && coverage <= 1.05 && Math.Abs(r - (background.R + coverage * dr)) <= 3
                    && Math.Abs(g - (background.G + coverage * dg)) <= 3 && Math.Abs(b - (background.B + coverage * db)) <= 3) { count++; }
            }
        }
        Assert.That(count, Is.GreaterThan(minimum), "Original label colors must reach real glyph pixels, including antialiasing.");
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
    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "gitextensions-file-status-" + Guid.NewGuid().ToString("N"));
        internal FileStatusListPreferencesStore Store { get; }
        internal FileStatusListView View { get; }
        internal Window Window { get; }
        internal Fixture()
        { Store = new(Directory); View = new(new TreeView { Name = "Files", SelectionMode = SelectionMode.Multiple }, preferencesStore: Store); Window = new() { Width = 520, Height = 340, Content = View }; }
        public void Dispose() { Window.Close(); View.Dispose(); if (System.IO.Directory.Exists(Directory)) { System.IO.Directory.Delete(Directory, recursive: true); } }
    }
}
