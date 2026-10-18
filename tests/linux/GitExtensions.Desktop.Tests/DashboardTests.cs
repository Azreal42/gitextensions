using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;
using Helpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class DashboardTests
{
    [AvaloniaTest]
    public async Task ClosingTheRepositoryReturnsToTheOriginalDashboardAndContextMenusPersistFavoritesAndCategories()
    {
        using Fixture fixture = await Fixture.CreateAsync("original\n", "original\n");
        MainWindow main = fixture.Window;
        main.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);
        main.KeyReleaseQwerty(PhysicalKey.W, RawInputModifiers.Control);
        DashboardControl dashboard = await DashboardAsync(main);
        Assert.Multiple(() =>
        {
            Assert.That(main.IsVisible, Is.True, "Ctrl+W closes the repository rather than the application.");
            Assert.That(main.Title, Is.EqualTo("Git Extensions"));
            Assert.That(Field<ListBox>(main, "RevisionHistory").IsEffectivelyVisible, Is.False);
            Assert.That(Field<ListBox>(main, "RevisionHistory").Items, Is.Empty);
            Assert.That(Field<TextBox>(dashboard, "DashboardSearch").IsFocused, Is.True);
        });
        Control tile = Tile(dashboard, fixture.Repository, false);
        ClickContext(tile, "Add to favorites");
        await Helpers.Until(() => dashboard.History.Favorites.Count == 1);
        tile = Tile(dashboard, fixture.Repository, true);
        MenuItem categories = tile.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == "Categories");
        categories.Items.OfType<MenuItem>().Single(item => item.Header as string == "Add new...").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Helpers.Until(() => main.OwnedWindows.Any());
        Window dialog = main.OwnedWindows.Single();
        dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "Travail [été] \"literal\"";
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Helpers.Until(() => !main.OwnedWindows.Any());
        Assert.That(dashboard.History.Favorites.Single().Category, Is.EqualTo("Travail [été] \"literal\""));
        RepositoryHistoryState saved = new RepositoryHistoryStore().Load();
        Assert.That(saved.Favorites, Is.EqualTo(dashboard.History.Favorites));
        MenuItem favoritesMenu = Group(main, "Start").Items.OfType<MenuItem>().Single(item => item.Header as string == "Favorite repositories");
        MenuItem category = favoritesMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Travail [été] \"literal\"");
        category.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Helpers.Until(() => !main.IsBusy && !dashboard.IsVisible);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        Assert.That(Field<ListBox>(main, "RevisionHistory").Items.OfType<GitCommit>().Count(), Is.EqualTo(1));
        Assert.That((await GitRepository.OpenAsync(fixture.Repository)).Path, Is.EqualTo(saved.Favorites.Single().Path));
    }

    [AvaloniaTest]
    public async Task LiteralPathsBareRepositoriesAndInvalidFoldersAreListedWithoutMutatingRepositories()
    {
        using Fixture fixture = await Fixture.CreateAsync("original\n", "original\n");
        string root = Path.GetDirectoryName(fixture.Repository)!;
        string literal = Path.Combine(root, "été \"[*]\"\nrepository");
        string bare = Path.Combine(root, "bare [literal].git");
        string invalid = Path.Combine(root, "directory without git");
        GitRepository cloned = await GitRepository.CloneAsync(fixture.Repository, literal);
        await GitRepository.CloneAsync(fixture.Repository, bare, new GitCloneOptions(Bare: true));
        Directory.CreateDirectory(invalid);
        string descendant = Path.Combine(literal, "nested [path]"); Directory.CreateDirectory(descendant);
        MainWindow main = fixture.Window;
        await main.OpenRepositoryAsync(descendant);
        await main.OpenRepositoryAsync(bare);
        ClickMenu(main, "Start", "Close");
        DashboardControl dashboard = await DashboardAsync(main);
        dashboard.SetFavorite(invalid, true);
        await dashboard.MetadataLoading;
        Assert.That(dashboard.History.Recent.Select(entry => entry.Path), Is.EqualTo(new[] { bare, literal, fixture.Repository }));
        Assert.That(Field<TextBlock>(Tile(dashboard, bare, false), "DashboardBranch").Text, Is.Empty, "The original dashboard does not show a worktree branch for bare repositories.");
        Assert.That(Field<TextBlock>(Tile(dashboard, literal, false), "DashboardBranch").Text, Is.EqualTo("main"));
        Image invalidIcon = Tile(dashboard, invalid, true).GetVisualDescendants().OfType<Image>().Single();
        Assert.That(AutomationProperties.GetName(invalidIcon), Is.EqualTo("Invalid repository"));
        ClickContext(Tile(dashboard, invalid, true), "Open repository");
        await Helpers.Until(() => !main.IsBusy && main.LastException is not null);
        Assert.That(dashboard.IsVisible, Is.True);
        Assert.That(dashboard.History.Favorites.Single().Path, Is.EqualTo(invalid), "A failed open does not silently remove the history entry.");
        Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.Contain("Could not open repository"));
        ClickContext(Tile(dashboard, invalid, true), "Remove missing projects from the list");
        await Helpers.Until(() => dashboard.History.Favorites.Count == 0);
        Assert.That(Directory.Exists(invalid), Is.True, "Removing an invalid history entry must leave the directory intact.");
        TextBox search = Field<TextBox>(dashboard, "DashboardSearch"); search.Text = "été";
        dashboard.UpdateLayout();
        Assert.That(dashboard.GetVisualDescendants().OfType<ListBox>().SelectMany(list => list.Items.OfType<DashboardRepository>()).Select(entry => entry.Path), Is.EqualTo(new[] { literal }));
        search.Focus(); main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Helpers.Until(() => !main.IsBusy && !dashboard.IsVisible);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
        Assert.That(dashboard.History.Recent.First().Path, Is.EqualTo(literal));
        Assert.That((await cloned.GetStatusAsync()).Changes, Is.Empty);
        Assert.That(await cloned.ResolveRevisionAsync("HEAD"), Is.EqualTo(await (await GitRepository.OpenAsync(fixture.Repository)).ResolveRevisionAsync("HEAD")));
    }

    [AvaloniaTest]
    public async Task ClearRecentConfirmationPreservesFavoritesAndFilesAndTheOriginalPaletteRendersInBothThemes()
    {
        using Fixture fixture = await Fixture.CreateAsync("original\n", "original\n");
        MainWindow main = fixture.Window;
        ClickMenu(main, "Start", "Close"); DashboardControl dashboard = await DashboardAsync(main);
        dashboard.SetFavorite(fixture.Repository, true);
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme;
            using var frame = Helpers.Capture(main, "classic-dashboard-" + theme + ".png");
            Assert.That(dashboard.GetVisualDescendants().OfType<Border>().Any(border => border.Height == 68 && border.Background is ISolidColorBrush brush && brush.Color == Color.Parse(theme == ThemeVariant.Light ? "#137AD4" : "#5A5A5A")), Is.True);
            Assert.That(AssetLoader.Exists(new Uri("avares://GitExtensions/Assets/Backgrounds/DashboardBackground" + (theme == ThemeVariant.Light ? "Blue" : "Grey") + ".png")), Is.True);
            Assert.That(dashboard.GetVisualDescendants().OfType<Grid>().Any(grid => grid.ColumnDefinitions.Any(column => column.Width.Value == 213)), Is.True);
            Assert.That(Field<TextBox>(dashboard, "DashboardSearch").Height, Is.EqualTo(24));
        }
        main.RequestedThemeVariant = ThemeVariant.Light;
        async Task<Window> ClearPrompt()
        {
            dashboard.UpdateLayout();
            TextBlock heading = dashboard.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "Recent repositories" && text.ContextMenu is not null);
            ClickContext(heading, "Clear all recent repositories");
            await Helpers.Until(() => main.OwnedWindows.Any()); return main.OwnedWindows.Single();
        }
        Window cancel = await ClearPrompt();
        cancel.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Helpers.Until(() => !main.OwnedWindows.Any());
        Assert.That(dashboard.History.Recent.Count, Is.EqualTo(1));
        Window accept = await ClearPrompt();
        accept.GetVisualDescendants().OfType<Button>().Single(button => !button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Helpers.Until(() => !main.OwnedWindows.Any());
        Assert.That(dashboard.History.Recent, Is.Empty);
        Assert.That(dashboard.History.Favorites.Single().Path, Is.EqualTo(fixture.Repository));
        Assert.That(new RepositoryHistoryStore().Load().Favorites.Single().Path, Is.EqualTo(fixture.Repository));
        Assert.That(File.ReadAllText(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo("original\n"));
        Assert.That((await (await GitRepository.OpenAsync(fixture.Repository)).GetStatusAsync()).Changes, Is.Empty);
    }

    [Test]
    public void LegacyHistoryMigrationDeduplicatesLinuxPathsAndAtomicPersistenceKeepsCategories()
    {
        string root = Path.Combine(Path.GetTempPath(), "gitextensions-repository-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            RepositoryHistoryStore store = new(root);
            string first = Path.Combine(root, "Repo [*]\nété"), second = Path.Combine(root, "repo [*]\nété");
            RepositoryHistoryState migrated = store.Load([first, first, second, "relative"]);
            Assert.That(migrated.Recent.Select(entry => entry.Path), Is.EqualTo(new[] { first, second }));
            RepositoryHistoryState state = migrated.Favorite(first, true).AssignCategory(first, " Work ");
            store.Save(state);
            Assert.That(store.Load().Favorites, Is.EqualTo(new[] { new RepositoryHistoryEntry(first, " Work ") }));
            Assert.That(store.Load().Recent, Is.EqualTo(migrated.Recent));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"), Is.Empty);
            Assert.That(Directory.Exists(first), Is.False, "Saving history must not create, rename or remove repository directories.");
            store.Save(state.Remove(first, false));
            Assert.That(store.Load().Recent.Single().Path, Is.EqualTo(second));
            Assert.That(store.Load().Favorites.Single().Path, Is.EqualTo(first));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [AvaloniaTest]
    public async Task TheOriginalCategoryDialogRejectsDuplicatesAndCancelPreservesTheFavoriteCategory()
    {
        using Fixture fixture = await Fixture.CreateAsync("original\n", "original\n");
        DashboardCategoryDialog dialog = new(["Work", "Private"], "Draft");
        Task<string?> result = dialog.ShowDialog<string?>(fixture.Window);
        TextBox name = Field<TextBox>(dialog, "DashboardCategoryName");
        Assert.That(Field<Button>(dialog, "DashboardCategoryAccept").IsEnabled, Is.False);
        name.Text = "Work";
        Field<Button>(dialog, "DashboardCategoryAccept").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(result.IsCompleted, Is.False);
        Assert.That(Field<TextBlock>(dialog, "DashboardCategoryError").Text, Is.EqualTo("Category name already exists"));
        name.Text = " ";
        Field<Button>(dialog, "DashboardCategoryAccept").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(Field<TextBlock>(dialog, "DashboardCategoryError").Text, Is.EqualTo("Category name is required"));
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(await result, Is.Null);
        dialog = new(["Work"], "Draft"); result = dialog.ShowDialog<string?>(fixture.Window);
        Field<TextBox>(dialog, "DashboardCategoryName").Text = " Case preserving [é] ";
        Field<Button>(dialog, "DashboardCategoryAccept").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(await result, Is.EqualTo(" Case preserving [é] "));
    }

    [AvaloniaTest]
    public async Task SettingsMenuCommitsOnlyOkAndTheToolbarUsesShortCaptionsWithLiteralRepositoryPaths()
    {
        using Fixture fixture = await Fixture.CreateAsync("original\n", "original\n");
        MainWindow main = fixture.Window;
        string before = File.ReadAllText(new RepositoryHistoryStore().FilePath);
        async Task<RecentRepositoriesSettingsDialog> OpenSettings()
        {
            ClickMenu(main, "Start", "Recent repositories settings");
            await Helpers.Until(() => main.OwnedWindows.OfType<RecentRepositoriesSettingsDialog>().Any());
            return main.OwnedWindows.OfType<RecentRepositoriesSettingsDialog>().Single();
        }
        RecentRepositoriesSettingsDialog settings = await OpenSettings();
        Field<NumericUpDown>(settings, "comboMinWidthEdit").Value = 160;
        Field<RadioButton>(settings, "mostSigDirRB").IsChecked = true;
        Field<Button>(settings, "Abort").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Helpers.Until(() => !main.OwnedWindows.Any());
        Assert.That(File.ReadAllText(new RepositoryHistoryStore().FilePath), Is.EqualTo(before));
        settings = await OpenSettings();
        Field<NumericUpDown>(settings, "comboMinWidthEdit").Value = 160;
        Field<RadioButton>(settings, "mostSigDirRB").IsChecked = true;
        Field<Button>(settings, "Ok").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Helpers.Until(() => !main.OwnedWindows.Any());
        RepositoryHistoryState saved = new RepositoryHistoryStore().Load();
        Assert.That(saved.RecentReposComboMinWidth, Is.EqualTo(160));
        Assert.That(saved.ShorteningStrategy, Is.EqualTo(RepositoryPathShorteningStrategy.MostSignDir));
        ComboBox recent = main.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "RecentRepositoriesDropdown");
        Assert.That(recent.Width, Is.EqualTo(24), "The compact original toolbar shows the repository drop-down arrow.");
        Assert.That(recent.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single().MinWidth, Is.EqualTo(160), "The configured width still applies to the opened repository list.");
        RepositoryHistoryRow item = recent.Items.OfType<RepositoryHistoryRow>().Single();
        Assert.That(item.Caption, Is.EqualTo("repository"));
        Assert.That(item.Path, Is.EqualTo(fixture.Repository));
        await main.RepositoryBranchLoading.WaitAsync(TimeSpan.FromSeconds(10));
        MenuItem recentMenu = Group(main, "Start").Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Recent repositories");
        MenuItem recentEntry = recentMenu.Items.OfType<MenuItem>().Single();
        Assert.That(recentEntry.Header, Is.EqualTo("repository (main)"));
        Assert.That(recentEntry.Tag, Is.EqualTo(fixture.Repository));
        Assert.That(ToolTip.GetTip(recentEntry), Is.EqualTo(fixture.Repository));
        ClickMenu(main, "Start", "Close"); DashboardControl dashboard = await DashboardAsync(main);
        Assert.That(((DashboardRepository)Tile(dashboard, fixture.Repository, false).Tag!).Caption, Is.EqualTo("repository"));
        ClickContext(Tile(dashboard, fixture.Repository, false), "Open repository");
        await Helpers.Until(() => !main.IsBusy && !dashboard.IsVisible);
        Assert.That(main.Title, Is.EqualTo(Path.GetFileName(fixture.Repository) + " (main) — Git Extensions"));
    }

    private static async Task<DashboardControl> DashboardAsync(MainWindow main)
    {
        await Helpers.Until(() => !main.IsBusy && main.GetVisualDescendants().OfType<DashboardControl>().Any(control => control.IsEffectivelyVisible));
        DashboardControl dashboard = main.GetVisualDescendants().OfType<DashboardControl>().Single();
        await dashboard.MetadataLoading; dashboard.UpdateLayout(); return dashboard;
    }
    private static Control Tile(DashboardControl dashboard, string path, bool favorite)
    {
        dashboard.UpdateLayout();
        return dashboard.GetVisualDescendants().OfType<Control>().Single(control => control.Tag is DashboardRepository entry && entry.Path == path && entry.IsFavorite == favorite);
    }
    private static T Field<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static MenuItem Group(MainWindow main, string group) => main.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().Single(item => item.Header as string == group);
    private static void ClickMenu(MainWindow main, string group, string action) => Group(main, group).Items.OfType<MenuItem>().Single(item => item.Header as string == action).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void ClickContext(Control control, string action) => control.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == action).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
}
