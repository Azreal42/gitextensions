using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class WindowsBrowseReferenceTests
{
    [AvaloniaTest]
    public async Task KeyboardOverflowFieldSurvivesMappingHoverUntilThePointerActuallyMoves()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = fixture.Window; main.Width = 860; main.Height = 752;
        CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(main, "RepositoryToolbar");
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; await Layout(main);
            Field<ListBox>(main, "RevisionHistory").Focus();
            main.MouseMove(new Point(650, 107));
            main.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.Control); main.KeyReleaseQwerty(PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
            TextBox branch = toolbar.OverflowFlyout.ItemsSource!.Cast<Control>().SelectMany(item => item.GetVisualDescendants().OfType<TextBox>()).Single(text => text.Name == "BranchHistoryFilterOverflow");
            MenuItem filter = toolbar.OverflowFlyout.ItemsSource!.Cast<MenuItem>().Single(item => item.Header as string == "Filter:");
            // This is the framework signal emitted when a popup maps beneath the
            // stationary mouse. It must not select Filter or start its 400 ms timer.
            filter.RaiseEvent(new RoutedEventArgs(MenuItem.PointerEnteredItemEvent) { Source = filter });
            await Task.Delay(450); await Layout(main);
            Assert.That(branch.IsFocused, Is.True);
            Assert.That(filter.IsSubMenuOpen, Is.False);
            main.KeyTextInput("feature");
            Assert.That(branch.Text, Is.EqualTo("feature"));
            Assert.That(Field<TextBox>(main, "BranchHistoryFilter").Text, Is.EqualTo("feature"));
            main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until(() => !main.IsBusy && !toolbar.OverflowFlyout.IsOpen);
            Assert.That(Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Subject), Is.EqualTo(new[] { "Feature branch change", "Initial commit" }));

            Field<ListBox>(main, "RevisionHistory").Focus();
            main.MouseMove(new Point(650, 107));
            main.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.Control); main.KeyReleaseQwerty(PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
            filter.RaiseEvent(new RoutedEventArgs(MenuItem.PointerEnteredItemEvent) { Source = filter });
            main.MouseMove(new Point(651, 107));
            await Task.Delay(450); await Layout(main);
            Assert.That(filter.IsSubMenuOpen, Is.True, "The first actual mouse move must restore normal menu hover, including its submenu.");
            toolbar.OverflowFlyout.Hide(); await Layout(main);
        }
        Assert.That(main.LastException, Is.Null);
        Assert.That((await new GitRepository(fixture.Repository).GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task NavigationCategoryButtonsKeepTheirRenderedBluePaletteWithAnOrangePlatformAccent()
    {
        FluentTheme fluent = Application.Current!.Styles.OfType<FluentTheme>().Single();
        fluent.Palettes.TryGetValue(ThemeVariant.Light, out ColorPaletteResources? previousLight);
        fluent.Palettes.TryGetValue(ThemeVariant.Dark, out ColorPaletteResources? previousDark);
        Color orange = Color.Parse("#e95420");
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = orange };
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = orange };
        try
        {
            using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
            MainWindow main = fixture.Window;
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                main.RequestedThemeVariant = theme; await Layout(main);
                Assert.That(((IResourceNode)fluent).TryGetResource("SystemAccentColor", theme, out object? accent), Is.True);
                Assert.That(accent, Is.EqualTo(orange));
                Color fill = Color.Parse(theme == ThemeVariant.Dark ? "#2864b4" : "#c2e0ff");
                Color border = Color.Parse(theme == ThemeVariant.Dark ? "#2864b4" : "#0078d7");
                foreach (string category in new[] { "Branches", "Remotes", "Tags", "Submodules", "Stashes", "Worktrees" })
                {
                    ToggleButton toggle = Field<ToggleButton>(main, "NavigationShow" + category);
                    toggle.IsChecked = true; await Layout(main);
                    ContentPresenter presenter = toggle.GetVisualDescendants().OfType<ContentPresenter>().Single(item => item.Name == "PART_ContentPresenter");
                    AssertChecked();
                    Point point = toggle.TranslatePoint(new Point(12, 12), main)!.Value;
                    main.MouseMove(point); await Layout(main); AssertChecked();
                    main.MouseDown(point, MouseButton.Left); await Layout(main); AssertChecked();
                    Point outside = new(main.Width - 4, main.Height - 4);
                    main.MouseMove(outside); main.MouseUp(outside, MouseButton.Left); await Layout(main);
                    toggle.IsChecked = false; await Layout(main);
                    Assert.That(((ISolidColorBrush)presenter.Background!).Color, Is.EqualTo(Color.Parse(theme == ThemeVariant.Dark ? "#202020" : "#f0f0f0")));
                    Assert.That(toggle.Bounds.Size, Is.EqualTo(new Size(24, 24)));
                    Assert.That(presenter.CornerRadius, Is.EqualTo(new CornerRadius(0)));
                    toggle.IsChecked = true; await Layout(main);

                    void AssertChecked()
                    {
                        Assert.That(((ISolidColorBrush)presenter.Background!).Color, Is.EqualTo(fill), "The actual checked/hover/pressed template must ignore Ubuntu's accent.");
                        Assert.That(((ISolidColorBrush)presenter.BorderBrush!).Color, Is.EqualTo(border));
                    }
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
    public async Task PhotoSizedBrowseKeepsOneToolbarInlineReferencesAndTheOriginalPaneProportions()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true, commitInfoPosition: null);
        for (int i = 0; i < 18; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), $"update {i}\n");
            await Git(fixture, "add", "--", "file.txt");
            await Git(fixture, "-c", "user.name=" + (i % 3 == 0 ? "Gregory Godin" : "Thomas Boulenger"), "-c", "user.email=" + (i % 3 == 0 ? "gregory@example.org" : "thomas@example.org"),
                "commit", "--date=" + DateTimeOffset.Now.AddDays(-2).ToString("O"), "-m", "fix(preprocessing): improve handling of contract " + i);
        }
        await Git(fixture, "checkout", "-b", "preprocessing/fix-front-expiry");
        await Git(fixture, "update-ref", "refs/remotes/origin/preprocessing/fix-front-expiry", "HEAD");
        MainWindow main = fixture.Window;
        main.Width = 1280; main.Height = 752;
        await main.OpenRepositoryAsync(fixture.Repository); await Layout(main);
        Field<TabControl>(main, "DetailsTabs").SelectedItem = Field<TabControl>(main, "DetailsTabs").Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
        ListBox initialHistory = Field<ListBox>(main, "RevisionHistory");
        GitCommit currentHead = initialHistory.Items.OfType<GitCommit>().First();
        initialHistory.SelectedItem = currentHead;
        await Until(() => !main.IsBusy);
        initialHistory.ScrollIntoView(currentHead); await Layout(main);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; await Layout(main);
            CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(main, "RepositoryToolbar");
            Assert.That(toolbar.Bounds.Height, Is.EqualTo(26));
            Assert.That(toolbar.InlineItems.Where(item => item.IsEffectivelyVisible).Select(item => InWindow(main, item).Y).Distinct().Count(), Is.EqualTo(1));
            Assert.That(Field<Control>(main, "RepositoryNavigationPanel").Bounds.Width, Is.EqualTo(194).Within(1));
            Assert.That(main.GetVisualDescendants().Any(control => control is Control { Name: "RevisionHeader" }), Is.False);
            CommitGraph graph = Field<CommitGraph>(main, null);
            Assert.That(graph.ShowReferenceLabels, Is.False);
            Assert.That(graph.Bounds.Width, Is.LessThanOrEqualTo(40));
            ListBox history = Field<ListBox>(main, "RevisionHistory");
            Assert.That(history.GetVisualDescendants().OfType<ListBoxItem>().Select(row => row.Bounds.Height), Is.All.EqualTo(20));
            Assert.That(history.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "RevisionDate" && text.Text == "2 days ago"), Is.True);
            GitCommit first = history.Items.OfType<GitCommit>().First();
            Control firstRow = history.GetVisualDescendants().OfType<Control>().Single(row => row.Name == "RevisionHistoryRow" && (row.Tag as GitCommit)?.Hash == first.Hash);
            TextBlock subject = firstRow.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RevisionSubject");
            Assert.That(InWindow(main, subject).X, Is.GreaterThan(InWindow(main, history).X + 180), "Long original reference labels precede the actual subject in the same row.");
            Rect top = InWindow(main, Field<Control>(main, "RevisionTable"));
            Rect bottom = InWindow(main, Field<TabControl>(main, "DetailsTabs"));
            Assert.That(top.Height / (top.Height + bottom.Height), Is.InRange(.51, .55));
            using Avalonia.Media.Imaging.WriteableBitmap? frame = main.CaptureRenderedFrame();
            Assert.That(frame, Is.Not.Null);
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"windows-browse-photo-reference-{theme}.png");
            frame!.Save(path); TestContext.AddTestAttachment(path, $"1280 × 752 Browse from the Windows photo reference, {theme}, with actual Git objects");
        }
        Assert.That(main.LastException, Is.Null);
        Assert.That((await new GitRepository(fixture.Repository).GetStatusAsync()).Changes, Is.Empty);
    }

    [AvaloniaTest]
    public async Task NarrowToolbarKeepsBranchAndQuickFiltersReachableWithoutChangingTheQueryOnOpeningScope()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true, commitInfoPosition: null);
        MainWindow main = fixture.Window; main.Width = 860; await Layout(main);
        CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(main, "RepositoryToolbar");
        Assert.That(toolbar.Overflow.IsVisible, Is.True);
        Click(main, toolbar.Overflow); await Layout(main);
        Assert.That(toolbar.OverflowFlyout.IsOpen, Is.True, "Overflow must open on the first real pointer click.");
        Assert.That(toolbar.OverflowFlyout.ItemsSource!.Cast<Control>().OfType<MenuItem>().Any(item => item.Header as string == "Branches:" || item.Header is Grid), Is.True);
        toolbar.OverflowFlyout.Hide();
        main.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.Control); main.KeyReleaseQwerty(PhysicalKey.E, RawInputModifiers.Control); await Layout(main);
        Assert.That(toolbar.OverflowFlyout.IsOpen, Is.True);
        TextBox overflowBranch = toolbar.OverflowFlyout.ItemsSource!.Cast<Control>().SelectMany(item => item.GetVisualDescendants().OfType<TextBox>()).Single(text => text.Name == "BranchHistoryFilterOverflow");
        Assert.That(overflowBranch.IsFocused, Is.True, "Ctrl+E must focus the usable overflow field at narrow widths.");
        overflowBranch.Text = "feature";
        main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Until(() => !main.IsBusy && !toolbar.OverflowFlyout.IsOpen);
        Assert.That(Field<TextBox>(main, "BranchHistoryFilter").Text, Is.EqualTo("feature"));
        Assert.That(Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Subject), Is.EqualTo(new[] { "Feature branch change", "Initial commit" }));
        main.Width = 1440; await Layout(main);
        Button scope = Field<Button>(main, "BrowseHistoryScope");
        string[] before = Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
        Click(main, scope); await Layout(main);
        MenuFlyout scopeMenu = (MenuFlyout)scope.Flyout!;
        Assert.That(scopeMenu.IsOpen, Is.True);
        Assert.That(Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(before));
        scopeMenu.ItemsSource!.Cast<MenuItem>().Single(item => item.Header as string == "All branches").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        scopeMenu.Hide(); await Until(() => !main.IsBusy); await Layout(main);
        TextBox quick = Field<TextBox>(main, "RevisionQuickFilter"); quick.Focus(); quick.Text = "Main branch change";
        main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); await Until(() => !main.IsBusy); await Layout(main);
        Assert.That(Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Subject), Is.EqualTo(new[] { "Main branch change" }));
        Assert.That((await new GitRepository(fixture.Repository).GetStatusAsync()).Changes, Is.Empty);
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task SidebarFoldersSearchAndCategoryButtonsPreserveTheActualSelectedReferenceAndHistory()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        await Git(fixture, "branch", "preprocessing/api/search"); await Git(fixture, "branch", "preprocessing/ui/layout");
        await Git(fixture, "update-ref", "refs/remotes/origin/preprocessing/api/search", "HEAD");
        MainWindow main = fixture.Window; await main.OpenRepositoryAsync(fixture.Repository); await Layout(main);
        TreeView tree = Field<TreeView>(main, "RepositoryNavigation");
        TreeViewItem leaf = BrowseTestHelpers.NavigationNodes(tree).Single(node => node.Tag is GitReference reference && reference.Name == "refs/remotes/origin/preprocessing/api/search");
        Assert.That(leaf.GetVisualAncestors().OfType<TreeViewItem>().Count(), Is.EqualTo(4), "Remotes are nested under the remote name and branch folders.");
        Click(main, leaf); await Until(() => !main.IsBusy && Field<TextBox>(main, "BranchHistoryFilter").Text == "refs/remotes/origin/preprocessing/api/search"); await Layout(main);
        string[] before = Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
        TextBox search = Field<TextBox>(main, "RepositoryNavigationSearch"); search.Text = "api/search"; await Layout(main);
        Assert.That(BrowseTestHelpers.NavigationNodes(tree).Where(node => node.Tag is GitReference).Select(node => ((GitReference)node.Tag!).ShortName), Is.EquivalentTo(new[] { "preprocessing/api/search", "origin/preprocessing/api/search" }));
        Assert.That(((TreeViewItem?)tree.SelectedItem)?.Tag, Is.TypeOf<GitReference>());
        search.Text = "no such reference"; await Layout(main); Assert.That(tree.Items, Is.Empty);
        search.Text = ""; await Layout(main);
        Assert.That(((tree.SelectedItem as TreeViewItem)?.Tag as GitReference)?.Name, Is.EqualTo("refs/remotes/origin/preprocessing/api/search"));
        Avalonia.Controls.Primitives.ToggleButton remotes = Field<Avalonia.Controls.Primitives.ToggleButton>(main, "NavigationShowRemotes");
        Click(main, remotes); await Layout(main);
        Assert.That(BrowseTestHelpers.NavigationNodes(tree).Any(node => node.Tag is GitReference { IsRemote: true }), Is.False);
        Click(main, remotes); await Layout(main);
        Assert.That(((tree.SelectedItem as TreeViewItem)?.Tag as GitReference)?.Name, Is.EqualTo("refs/remotes/origin/preprocessing/api/search"));
        Assert.That(Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(before), "Sidebar filtering changes the tree presentation, without changing the actual revision query.");
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task WorkingAndIndexRowsOpenTheirRealDiffsWithoutStagingOrWritingFiles()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "working change\n", commitInfoPosition: null);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "indexed.txt"), "index only\n"); await Git(fixture, "add", "--", "indexed.txt");
        MainWindow main = fixture.Window; await main.OpenRepositoryAsync(fixture.Repository); await Layout(main);
        GitRepository repository = new(fixture.Repository); GitStatus before = await repository.GetStatusAsync();
        foreach ((string action, string content) in new[] { ("WorkingDirectoryRevision", "+working change"), ("IndexRevision", "+index only") })
        {
            Click(main, Field<Button>(main, action));
            await Until(() => !main.IsBusy && main.GetVisualDescendants().OfType<DiffView>().Any(view => view.IsEffectivelyVisible && view.Text.Contains(content, StringComparison.Ordinal)));
            Assert.That((Field<TabControl>(main, "DetailsTabs").SelectedItem as TabItem)?.Name, Is.EqualTo("ChangesTab"));
        }
        GitStatus after = await repository.GetStatusAsync(); Assert.That(after.Head, Is.EqualTo(before.Head)); Assert.That(after.Changes, Is.EqualTo(before.Changes));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo("working change\n"));
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task BranchNamesRemainLiteralWhenTheyMatchTranslatedCommandLabels()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n");
        await Git(fixture, "branch", "Refresh"); await Git(fixture, "branch", "Author");
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentUICulture;
        MainWindow? french = null;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("fr");
            french = new MainWindow(); french.Show(); await Until(() => !french.IsBusy);
            await french.OpenRepositoryAsync(fixture.Repository); await Until(() => !french.IsBusy); await Layout(french);
            TreeView tree = Field<TreeView>(french, "RepositoryNavigation");
            foreach (string name in new[] { "Refresh", "Author" })
            {
                TreeViewItem node = BrowseTestHelpers.NavigationNodes(tree).Single(item => item.Tag is GitReference reference && reference.ShortName == name);
                Assert.That(((StackPanel)node.Header!).Children.OfType<TextBlock>().Single().Text, Is.EqualTo(name), "Git reference names are data, even when they coincide with a translated command.");
            }
            Assert.That(french.LastException, Is.Null);
            Assert.That((await new GitRepository(fixture.Repository).GetStatusAsync()).Changes, Is.Empty);
        }
        finally { french?.Close(); System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [AvaloniaTest]
    public async Task RefreshUpdatesInlineReferencesWhenTheCommitItselfHasNotChanged()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", commitInfoPosition: null);
        MainWindow main = fixture.Window;
        string head = await new GitRepository(fixture.Repository).ResolveRevisionAsync("HEAD");
        await Git(fixture, "branch", "feature/new-reference");
        await Git(fixture, "tag", "v-reference");
        main.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.F5, RawInputModifiers.None);
        await Until(() => !main.IsBusy); await Layout(main);
        ListBox history = Field<ListBox>(main, "RevisionHistory");
        Assert.That(history.Items.OfType<GitCommit>().Single().Hash, Is.EqualTo(head));
        string[] Names() => history.GetVisualDescendants().OfType<Border>().Where(badge => badge.Name == "RevisionReference").Select(badge => ((GitReference)badge.Tag!).Name).ToArray();
        Assert.That(Names(), Does.Contain("refs/heads/feature/new-reference").And.Contain("refs/tags/v-reference"));
        await Git(fixture, "branch", "-m", "feature/new-reference", "feature/renamed-reference");
        await Git(fixture, "tag", "-d", "v-reference");
        main.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.F5, RawInputModifiers.None);
        await Until(() => !main.IsBusy); await Layout(main);
        Assert.That(Names(), Does.Contain("refs/heads/feature/renamed-reference").And.Not.Contain("refs/heads/feature/new-reference").And.Not.Contain("refs/tags/v-reference"));
        Assert.That((await new GitRepository(fixture.Repository).GetStatusAsync()).Changes, Is.Empty);
        Assert.That(main.LastException, Is.Null);
    }

    private static T Field<T>(Window main, string? name) where T : Control => main.GetVisualDescendants().OfType<T>().Single(control => name is null || control.Name == name);
    private static Rect InWindow(Window main, Control control) => new(control.TranslatePoint(default, main)!.Value, control.Bounds.Size);
    private static void Click(Window main, Control control)
    { Assert.That(control.IsEffectivelyVisible && control.IsEnabled, Is.True, control.Name); Point point = control.TranslatePoint(new Point(control is TreeViewItem ? 48 : 8, Math.Min(10, control.Bounds.Height / 2)), main)!.Value; main.MouseMove(point); main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left); }
    private static async Task Git(Fixture fixture, params string[] args) { GitResult result = await fixture.Git.RunAsync(fixture.Repository, args); Assert.That(result.Success, Is.True, result.StandardError); }
    private static async Task Layout(Window window) { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static async Task Until(Func<bool> condition) { for (int i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); } Assert.That(condition(), Is.True); }
}
