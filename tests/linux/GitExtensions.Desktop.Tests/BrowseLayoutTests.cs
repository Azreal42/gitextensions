using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class BrowseLayoutTests
{
    [AvaloniaTest]
    public async Task NewProfileUsesTheOriginalCommitTabBelowHistoryAndKeepsItOnResize()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true, commitInfoPosition: null);
        MainWindow main = fixture.Window;
        Assert.That(File.Exists(new BrowserLayoutPreferencesStore().FilePath), Is.False, "Opening a new profile must not manufacture a user choice.");
        Assert.That(Details(main).Items.OfType<TabItem>().First().Name, Is.EqualTo("CommitInfoTab"));
        AssertLayout(main, CommitInfoPosition.BelowList);
        await SelectCommitAsync(main, "Main branch change");
        string? information = Information(main).Text;
        string[] selection = SelectedHashes(main);
        main.Width = 1080; main.Height = 720;
        await LayoutAsync(main);
        AssertLayout(main, CommitInfoPosition.BelowList);
        Assert.That(Information(main).Text, Is.EqualTo(information));
        Assert.That(SelectedHashes(main), Is.EqualTo(selection));

        Details(main).SelectedItem = Details(main).Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
        await LayoutAsync(main);
        Assert.That(main.GetVisualDescendants().OfType<DiffView>().Single(view => view.IsEffectivelyVisible).Text, Does.Contain("+Main branch content"));
        main.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);
        main.KeyReleaseQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);
        await LayoutAsync(main);
        Assert.That((Details(main).SelectedItem as TabItem)?.Name, Is.EqualTo("CommitInfoTab"));
        Assert.That(Information(main).IsFocused, Is.True, "Ctrl+2 must expose and focus information when it is below the history.");
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task CompactSplitButtonCyclesOriginalPositionsAndItsMenuUsesOriginalIcons()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", commitInfoPosition: null);
        MainWindow main = fixture.Window;
        Button primary = Field<Button>(main, "CommitInfoPosition");
        foreach (CommitInfoPosition position in new[] { CommitInfoPosition.LeftwardFromList, CommitInfoPosition.RightwardFromList, CommitInfoPosition.BelowList })
        {
            Click(main, primary);
            await LayoutAsync(main);
            AssertLayout(main, position);
            Assert.That(new BrowserLayoutPreferencesStore().Load().CommitInfoPosition, Is.EqualTo(position));
            Image image = (Image)primary.Content!;
            Assert.That(image.Bounds.Size, Is.EqualTo(new Size(16, 16)));
            Assert.That(image.Tag, Is.EqualTo(Icon(position)));
            Assert.That(ToolTip.GetTip(primary), Is.EqualTo(Label(position)));
        }
        Assert.That(Field<Grid>(main, "CommitInfoPositionControl").Bounds.Size, Is.EqualTo(new Size(32, 24)));
        Button dropdown = Field<Button>(main, "CommitInfoPositionMenu");
        MenuFlyout menu = (MenuFlyout)dropdown.Flyout!;
        try
        {
            Click(main, dropdown);
            await LayoutAsync(main);
            Assert.That(menu.IsOpen, Is.True, "The arrow must open the choices without cycling the selected position.");
            Assert.That(new BrowserLayoutPreferencesStore().Load().CommitInfoPosition, Is.EqualTo(CommitInfoPosition.BelowList));
            MenuItem[] choices = menu.ItemsSource!.Cast<MenuItem>().ToArray();
            Assert.That(choices.Select(item => item.Header), Is.EqualTo(new[] { Label(CommitInfoPosition.BelowList), Label(CommitInfoPosition.LeftwardFromList), Label(CommitInfoPosition.RightwardFromList) }));
            foreach (MenuItem item in choices)
            {
                Image image = (Image)item.Icon!;
                Assert.That(image.Source, Is.InstanceOf<Bitmap>());
                Assert.That(image.Bounds.Size, Is.EqualTo(new Size(16, 16)), "The opened menu must actually lay out the original PNG at 16 px.");
                Assert.That(image.Tag, Is.EqualTo(Icon((CommitInfoPosition)item.Tag!)));
            }
            choices.Single(item => Equals(item.Tag, CommitInfoPosition.LeftwardFromList)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await LayoutAsync(main);
            AssertLayout(main, CommitInfoPosition.LeftwardFromList);
            Assert.That(choices.Where(item => item.IsChecked).Select(item => item.Tag), Is.EqualTo(new object[] { CommitInfoPosition.LeftwardFromList }));
        }
        finally { menu.Hide(); }
    }

    [AvaloniaTest]
    public async Task MovingInformationPreservesTheSelectedReferenceCommitsDiffAndWorkingDirectory()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "uncommitted work\n", createMerge: true);
        MainWindow main = fixture.Window;
        TreeView navigation = Field<TreeView>(main, "RepositoryNavigation");
        TreeViewItem branch = BrowseTestHelpers.NavigationNodes(navigation).Single(item => item.Tag is GitReference reference && reference.Name == "refs/heads/main");
        main.UpdateLayout();
        Point branchPoint = branch.TranslatePoint(new Point(48, 10), main)!.Value;
        main.MouseMove(branchPoint); main.MouseDown(branchPoint, MouseButton.Left); main.MouseUp(branchPoint, MouseButton.Left);
        await UntilAsync(() => !main.IsBusy && (navigation.SelectedItem as TreeViewItem)?.Tag is GitReference reference && reference.Name == "refs/heads/main");
        await SelectCommitAsync(main, "Main branch change");
        ListBox history = History(main);
        history.SelectedItems!.Add(history.Items.OfType<GitCommit>().Single(commit => commit.Subject == "Initial commit"));
        await UntilAsync(() => !main.IsBusy);
        string[] selected = SelectedHashes(main);
        string? information = Information(main).Text;
        string diff = main.GetVisualDescendants().OfType<DiffView>().Single(view => view.IsEffectivelyVisible).Text;
        GitStatus before = await new GitRepository(fixture.Repository).GetStatusAsync();
        foreach (CommitInfoPosition position in new[] { CommitInfoPosition.BelowList, CommitInfoPosition.LeftwardFromList, CommitInfoPosition.RightwardFromList })
        {
            SelectPosition(main, position);
            await LayoutAsync(main);
            Assert.That(SelectedHashes(main), Is.EqualTo(selected));
            Assert.That(Information(main).Text, Is.EqualTo(information));
            Assert.That(((navigation.SelectedItem as TreeViewItem)?.Tag as GitReference)?.Name, Is.EqualTo("refs/heads/main"));
            Assert.That(History(main), Is.SameAs(history), "Changing placement must retain the existing revision list and its selection.");
            TabItem diffTab = Details(main).Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
            Details(main).SelectedItem = diffTab;
            await LayoutAsync(main);
            Assert.That(main.GetVisualDescendants().OfType<DiffView>().Single(view => view.IsEffectivelyVisible).Text, Is.EqualTo(diff));
        }
        GitStatus after = await new GitRepository(fixture.Repository).GetStatusAsync();
        Assert.That(after.Head, Is.EqualTo(before.Head));
        Assert.That(after.Changes.Select(change => (change.Path, change.IndexStatus, change.WorkTreeStatus)), Is.EqualTo(before.Changes.Select(change => (change.Path, change.IndexStatus, change.WorkTreeStatus))));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "file.txt")), Is.EqualTo("uncommitted work\n"));
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task SideAndDetailsSplittersRemainUsableAndRememberTheirWidthsAcrossPlacementChanges()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true);
        MainWindow main = fixture.Window;
        await SelectCommitAsync(main, "Main branch change");
        foreach (CommitInfoPosition position in new[] { CommitInfoPosition.RightwardFromList, CommitInfoPosition.LeftwardFromList })
        {
            SelectPosition(main, position); await LayoutAsync(main);
            GridSplitter splitter = Field<GridSplitter>(main, "CommitInfoSplitter");
            double before = Information(main).Bounds.Width;
            Drag(main, splitter, new Vector(position == CommitInfoPosition.RightwardFromList ? -45 : 45, 0));
            await LayoutAsync(main);
            double resized = Information(main).Bounds.Width;
            Assert.That(resized, Is.GreaterThan(before + 20), "A real pointer drag must resize the information panel on either side.");
            SelectPosition(main, CommitInfoPosition.BelowList); await LayoutAsync(main);
            SelectPosition(main, position); await LayoutAsync(main);
            Assert.That(Information(main).Bounds.Width, Is.EqualTo(resized).Within(2));
            main.Width -= 100; await LayoutAsync(main);
            AssertLayout(main, position);
            Assert.That(Information(main).Bounds.Width, Is.EqualTo(resized).Within(2), "The side panel must retain its fixed size when there is sufficient space.");
            main.Width += 100; await LayoutAsync(main);
        }
        SelectPosition(main, CommitInfoPosition.BelowList); await LayoutAsync(main);
        GridSplitter detailsSplitter = Field<GridSplitter>(main, "DetailsSplitter");
        double originalHeight = History(main).Bounds.Height;
        Drag(main, detailsSplitter, new Vector(0, 40)); await LayoutAsync(main);
        Assert.That(History(main).Bounds.Height, Is.GreaterThan(originalHeight + 20));
        AssertLayout(main, CommitInfoPosition.BelowList);
    }

    [AvaloniaTest]
    public async Task EveryPositionRendersRealMergeHistoryInLightAndDarkThemes()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", createMerge: true, commitInfoPosition: null);
        MainWindow main = fixture.Window;
        await SelectCommitAsync(main, "Main branch change");
        foreach (CommitInfoPosition position in Enum.GetValues<CommitInfoPosition>())
        {
            SelectPosition(main, position);
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                main.RequestedThemeVariant = theme; await LayoutAsync(main);
                AssertLayout(main, position);
                Assert.That(Field<CommitGraph>(main, null).Commits.Any(commit => commit.Parents.Count == 2), Is.True);
                Assert.That(Information(main).Text, Does.Contain("Main branch change"));
                if (position != CommitInfoPosition.BelowList)
                {
                    Assert.That(main.GetVisualDescendants().OfType<DiffView>().Single(view => view.IsEffectivelyVisible).Text,
                        Does.Contain("+Main branch content"), "Exposing Diff by moving Commit information must also load the selected revision.");
                }
                using WriteableBitmap? frame = main.CaptureRenderedFrame();
                Assert.That(frame, Is.Not.Null);
                string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"classic-browser-layout-{position}-{theme}.png");
                frame!.Save(path);
                TestContext.AddTestAttachment(path, $"Original Browse {position} layout rendered in {theme} with real merge history");
            }
        }
    }

    [AvaloniaTest]
    public async Task XdgChoiceIsRestoredForAllThreePositionsWhenTheApplicationWindowReopens()
    {
        using Fixture fixture = await Fixture.CreateAsync("initial\n", "initial\n", commitInfoPosition: null);
        MainWindow original = fixture.Window;
        foreach (CommitInfoPosition position in Enum.GetValues<CommitInfoPosition>())
        {
            SelectPosition(original, position);
            BrowserLayoutPreferencesStore store = new();
            Assert.That(store.FilePath, Is.EqualTo(Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!, "gitextensions", "browser-layout-preferences.json")));
            Assert.That(await File.ReadAllTextAsync(store.FilePath), Does.Contain(position.ToString()));
            MainWindow reopened = new() { Width = 1440, Height = 900 };
            try
            {
                reopened.Show();
                await UntilAsync(() => !reopened.IsBusy);
                await reopened.OpenRepositoryAsync(fixture.Repository);
                await UntilAsync(() => !reopened.IsBusy);
                await LayoutAsync(reopened);
                AssertLayout(reopened, position);
                Assert.That(Information(reopened).Text, Does.Contain("Initial commit"));
                Assert.That(reopened.LastException, Is.Null);
            }
            finally { reopened.Close(); await LayoutAsync(original); }
        }
        Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(new BrowserLayoutPreferencesStore().FilePath)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public void MalformedOrUnknownSavedPositionsFallBackToTheOriginalBelowLayout()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "gitextensions-browser-preferences-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", temporary);
            BrowserLayoutPreferencesStore store = new();
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            foreach (string json in new[] { "{", "{\"CommitInfoPosition\":99}", "{\"CommitInfoPosition\":\"unrecognised\"}", "null" })
            {
                File.WriteAllText(store.FilePath, json);
                Assert.That(store.Load().CommitInfoPosition, Is.EqualTo(CommitInfoPosition.BelowList));
            }
            store.Save(new(CommitInfoPosition.LeftwardFromList));
            Assert.That(store.Load().CommitInfoPosition, Is.EqualTo(CommitInfoPosition.LeftwardFromList));
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); if (Directory.Exists(temporary)) { Directory.Delete(temporary, recursive: true); } }
    }

    private static T Field<T>(Window owner, string? name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => name is null || control.Name == name);
    private static ListBox History(Window owner) => Field<ListBox>(owner, "RevisionHistory");
    private static TextBox Information(Window owner) => Field<TextBox>(owner, "CommitInformation");
    private static TabControl Details(Window owner) => Field<TabControl>(owner, "DetailsTabs");
    private static string[] SelectedHashes(Window owner) => History(owner).SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash).OrderBy(hash => hash, StringComparer.Ordinal).ToArray();
    private static string Icon(CommitInfoPosition position) => position switch { CommitInfoPosition.BelowList => "LayoutFooterTab.png", CommitInfoPosition.LeftwardFromList => "LayoutSidebarTopLeft.png", _ => "LayoutSidebarTopRight.png" };
    private static string Label(CommitInfoPosition position) => position switch { CommitInfoPosition.BelowList => "Commit info below graph", CommitInfoPosition.LeftwardFromList => "Commit info left of graph", _ => "Commit info right of graph" };

    private static async Task SelectCommitAsync(MainWindow main, string subject)
    {
        GitCommit commit = History(main).Items.OfType<GitCommit>().Single(item => item.Subject == subject);
        History(main).SelectedItem = commit;
        await UntilAsync(() => !main.IsBusy && Information(main).Text?.Contains(commit.Hash, StringComparison.Ordinal) is true);
        await LayoutAsync(main);
    }

    private static void SelectPosition(MainWindow main, CommitInfoPosition position)
    {
        MenuFlyout menu = (MenuFlyout)Field<Button>(main, "CommitInfoPositionMenu").Flyout!;
        menu.ItemsSource!.Cast<MenuItem>().Single(item => Equals(item.Tag, position)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static void AssertLayout(MainWindow main, CommitInfoPosition position)
    {
        main.UpdateLayout();
        Rect history = Bounds(main, History(main)), information = Bounds(main, Information(main)), details = Bounds(main, Details(main));
        Assert.That(Information(main).IsEffectivelyVisible, Is.True);
        Assert.That(Information(main).Bounds.Width, Is.GreaterThan(120));
        Assert.That(Field<GridSplitter>(main, "DetailsSplitter").IsEffectivelyVisible, Is.True);
        Assert.That(Field<GridSplitter>(main, "CommitInfoSplitter").IsEffectivelyVisible, Is.EqualTo(position != CommitInfoPosition.BelowList));
        if (position == CommitInfoPosition.BelowList)
        {
            Assert.That((Details(main).SelectedItem as TabItem)?.Name, Is.EqualTo("CommitInfoTab"));
            Assert.That(history.Bottom, Is.LessThanOrEqualTo(information.Y));
            Assert.That(information.Width, Is.GreaterThanOrEqualTo(history.Width - 16), "Information must fill the lower panel, allowing its original content padding.");
        }
        else
        {
            Assert.That(Details(main).Items.OfType<TabItem>().Any(tab => tab.Name == "CommitInfoTab"), Is.False);
            Assert.That(information.Bottom, Is.LessThanOrEqualTo(details.Y));
            if (position == CommitInfoPosition.LeftwardFromList) { Assert.That(information.Right, Is.LessThanOrEqualTo(history.X)); }
            else { Assert.That(history.Right, Is.LessThanOrEqualTo(information.X)); }
        }
        Assert.That(information.Right, Is.LessThanOrEqualTo(main.Bounds.Width));
    }

    private static Rect Bounds(Window owner, Control control) => new(control.TranslatePoint(default, owner)!.Value, control.Bounds.Size);
    private static void Click(Window owner, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), owner)!.Value;
        owner.MouseMove(point); owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left);
    }
    private static void Drag(Window owner, Control control, Vector delta)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), owner)!.Value;
        owner.MouseMove(point); owner.MouseDown(point, MouseButton.Left); owner.MouseMove(point + delta); owner.MouseUp(point + delta, MouseButton.Left);
    }
    private static async Task LayoutAsync(MainWindow main)
    {
        await Task.Yield(); Dispatcher.UIThread.RunJobs(); main.UpdateLayout();
        await UntilAsync(() => !main.IsBusy);
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < deadline) { await Task.Delay(15); }
        Assert.That(predicate(), Is.True, "Native Browse did not settle within ten seconds.");
    }
}
