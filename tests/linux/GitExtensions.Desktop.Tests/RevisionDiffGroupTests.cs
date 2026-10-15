using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class RevisionDiffGroupTests
{
    [AvaloniaTest]
    public async Task MergeTreeKeepsBothParentsAndCombinedConflictBesideTheClassicViewer()
    {
        using Fixture fixture = await ConflictAsync();
        MainWindow main = fixture.Window;
        TreeView tree = Files(main);
        GitCommit merge = (GitCommit)History(main).SelectedItem!;
        TreeViewItem[] groups = tree.Items.OfType<TreeViewItem>().ToArray();
        Assert.That(groups.Select(Group).Select(group => group.Kind),
            Is.EqualTo(new[] { GitRevisionDiffGroupKind.Parent, GitRevisionDiffGroupKind.Parent, GitRevisionDiffGroupKind.Combined }));
        Assert.That(groups.Take(2).Select(Group).Select(group => group.BaseRevision), Is.EqualTo(merge.Parents));
        TreeViewItem combined = groups.Last();
        combined.IsExpanded = true;
        TreeViewItem file = combined.Items.OfType<TreeViewItem>().Single(item => File(item).Path == "file.txt");
        tree.SelectedItem = file;
        await Idle(main);
        DiffView viewer = Visible(main);
        Assert.That(viewer.EffectiveMode, Is.EqualTo(DiffViewMode.Combined));
        Assert.That(viewer.Text, Does.Contain("diff --combined file.txt").And.Contain("@@@").And.Contain("++resolved λ🙂"));
        Assert.That(viewer.CanStagePatch, Is.False);
        main.UpdateLayout();
        Grid split = Find<Grid>(main, "RevisionDiffSplit");
        Assert.That(split.ColumnDefinitions[0].Width.Value, Is.EqualTo(420));
        Assert.That(Find<GridSplitter>(main, "RevisionDiffSplitter").Bounds.Width, Is.EqualTo(4));
        Assert.That(tree.TranslatePoint(default, main)!.Value.X + tree.Bounds.Width,
            Is.LessThan(viewer.TranslatePoint(default, main)!.Value.X));
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme;
            Capture(main, "classic-combined-browser-" + theme + ".png");
        }
        tree.SelectedItem = groups[1].Items.OfType<TreeViewItem>().Single(item => File(item).Path == "file.txt");
        await Idle(main);
        Assert.That(viewer.EffectiveMode, Is.EqualTo(DiffViewMode.Unified));
        Assert.That(viewer.Text, Does.Contain("@@ ").And.Not.Contain("@@@"));
        Assert.That((await Git(fixture, "status", "--porcelain=v1")).StandardOutput, Is.Empty);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task LiteralCombinedPathOnlyReadsThatFileAndNeverTurnsIntoAGlob()
    {
        using Fixture fixture = await ConflictAsync();
        TreeView tree = Files(fixture.Window);
        TreeViewItem combined = tree.Items.OfType<TreeViewItem>().Single(item => Group(item).Kind == GitRevisionDiffGroupKind.Combined);
        const string literal = "literal λ[1]\n.txt";
        Assert.That(combined.Items.OfType<TreeViewItem>().Select(File).Select(file => file.Path), Contains.Item(literal));
        combined.IsExpanded = true;
        TreeViewItem literalItem = combined.Items.OfType<TreeViewItem>().Single(item => File(item).Path == literal);
        string caption = string.Concat(((Grid)literalItem.Header!).Children.OfType<TextBlock>().Single().Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text));
        Assert.That(caption, Does.Contain("\\n").And.Not.Contain("\n"));
        tree.SelectedItem = literalItem;
        await Idle(fixture.Window);
        DiffView viewer = Visible(fixture.Window);
        Assert.That(viewer.Text, Does.Contain("++resolved literal").And.Not.Contain("++resolved λ🙂"));
        Assert.That((await Git(fixture, "show", "HEAD:" + literal)).StandardOutput, Is.EqualTo("resolved literal\n"));
        Assert.That(fixture.Window.LastException, Is.Null, fixture.Window.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task AllParentsPreferenceUsesOriginalDefaultsAndSurvivesAReopenedBrowser()
    {
        using Fixture fixture = await ConflictAsync();
        MainWindow main = fixture.Window;
        RevisionDiffPreferencesStore store = new();
        Assert.That(store.Load(), Is.EqualTo(new RevisionDiffPreferences()));
        Assert.That(System.IO.File.Exists(store.FilePath), Is.False, "Opening Browse must not manufacture a stored choice.");
        MenuItem option = Setting(main, "RevisionDiffAllParents");
        option.IsChecked = false;
        option.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Idle(main);
        Assert.That(store.Load().ShowDiffForAllParents, Is.False);
        Assert.That(FileStatusTestHelpers.Rows(Files(main)).Any(item => item.Tag is RevisionDiffFileSelection), Is.True);
        Assert.That(FileStatusTestHelpers.Rows(Files(main)).Select(item => item.Tag).OfType<RevisionDiffFileSelection>().Select(item => item.Group.BaseRevision).Distinct(),
            Is.EqualTo(new[] { ((GitCommit)History(main).SelectedItem!).Parents[0] }));
        MainWindow reopened = new();
        try
        {
            reopened.Show();
            await Idle(reopened); // Startup may already be opening this recent repository.
            await reopened.OpenRepositoryAsync(fixture.Repository);
            await Idle(reopened);
            Find<TabControl>(reopened, "DetailsTabs").SelectedItem = Find<TabControl>(reopened, "DetailsTabs").Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
            reopened.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.That(Setting(reopened, "RevisionDiffAllParents").IsChecked, Is.False);
            Assert.That(FileStatusTestHelpers.Rows(Files(reopened)).Any(item => item.Tag is RevisionDiffFileSelection), Is.True);
            MenuItem restored = Setting(reopened, "RevisionDiffAllParents");
            restored.IsChecked = true; restored.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Idle(reopened);
            Assert.That(Files(reopened).Items.OfType<TreeViewItem>().Select(Group).Select(group => group.Kind),
                Contains.Item(GitRevisionDiffGroupKind.Combined));
            Assert.That(reopened.LastException, Is.Null);
        }
        finally { reopened.Close(); }
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "revision-diff-preferences.json.*.tmp"), Is.Empty);
    }

    [AvaloniaTest]
    public async Task UninterestingMergeHunksKeepTheSelectedFileAndShowTheOriginalExplanation()
    {
        using Fixture fixture = await ConflictAsync(useParentHunks: true);
        MainWindow main = fixture.Window;
        TreeView tree = Files(main);
        TreeViewItem combined = tree.Items.OfType<TreeViewItem>().Single(item => Group(item).Kind == GitRevisionDiffGroupKind.Combined);
        combined.IsExpanded = true;
        tree.SelectedItem = combined.Items.OfType<TreeViewItem>().Single(item => File(item).Path == "file.txt");
        await Idle(main);
        Assert.That(Visible(main).Text, Does.Contain("@@@"));
        MenuItem omit = Setting(main, "RevisionDiffOmitUninteresting");
        omit.IsChecked = true; omit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Idle(main);
        Assert.That(((RevisionDiffFileSelection)((TreeViewItem)tree.SelectedItem!).Tag!).File.Path, Is.EqualTo("file.txt"));
        Assert.That(Visible(main).Text, Is.EqualTo("Uninteresting diff hunks are omitted."));
        Assert.That(Visible(main).EffectiveMode, Is.EqualTo(DiffViewMode.Combined));
        Assert.That(new RevisionDiffPreferencesStore().Load().OmitUninterestingDiff, Is.True);
        omit.IsChecked = false; omit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Idle(main);
        Assert.That(Visible(main).Text, Does.Contain("@@@"));
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task CompactViewerControlsApplyContextAndWholeFileToInitialCommits()
    {
        string contents = string.Join('\n', Enumerable.Range(1, 30).Select(index => "line " + index)) + "\n";
        using Fixture fixture = await Fixture.CreateAsync(contents, contents);
        MainWindow main = fixture.Window;
        for (int i = 0; i < 3; i++) { Click(main, "RevisionDiffDecreaseContext"); await Idle(main); }
        Assert.That(Visible(main).Text, Does.Contain("+line 30"), "A root commit still displays its additions at zero context.");
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        await System.IO.File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), contents.Replace("line 15\n", "edited λ\n", StringComparison.Ordinal));
        await repository.StageAllAsync(); await repository.CommitAsync("Middle edit");
        await main.OpenRepositoryAsync(fixture.Repository);
        History(main).SelectedItem = History(main).Items.OfType<GitCommit>().Single(commit => commit.Subject == "Middle edit");
        await Idle(main);
        Assert.That(Visible(main).Text, Does.Contain("@@ -15 +15 @@").And.Not.Contain("+line 30"));
        Click(main, "RevisionDiffIncreaseContext"); await Idle(main);
        Assert.That(Visible(main).Text, Does.Contain(" line 14\n").And.Contain(" line 16\n"));
        Click(main, "RevisionDiffEntireFile"); await Idle(main);
        Assert.That(Visible(main).Text, Does.Contain(" line 1\n").And.Contain(" line 30\n"));
        Assert.That(main.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RevisionDiffIncreaseContext").IsEnabled, Is.False);
        Assert.That(main.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RevisionDiffDecreaseContext").IsEnabled, Is.False);
        Assert.That(Visible(main).ComparisonMenu.Options.ContextLines, Is.EqualTo(1));
        Click(main, "RevisionDiffEntireFile"); await Idle(main);
        Assert.That(Visible(main).Text, Does.Contain(" line 14\n").And.Contain(" line 16\n").And.Not.Contain(" line 30\n"));
        Assert.That(Visible(main).ComparisonMenu.Options.ContextLines, Is.EqualTo(1));
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }

    [AvaloniaTest]
    public async Task RepositorySwitchClearsCombinedGroupsAndImagesAndCorruptPreferencesUseDefaults()
    {
        using Fixture fixture = await ConflictAsync();
        RevisionDiffPreferencesStore store = new();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await System.IO.File.WriteAllTextAsync(store.FilePath, "{broken");
        Assert.That(store.Load(), Is.EqualTo(new RevisionDiffPreferences()));
        store.Save(new(false, true));
        Assert.That(store.Load(), Is.EqualTo(new RevisionDiffPreferences(false, true)));
        string empty = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "empty-diff");
        await GitRepository.InitAsync(empty);
        await fixture.Window.OpenRepositoryAsync(empty);
        Assert.That(Files(fixture.Window).Items, Is.Empty);
        Assert.That(Visible(fixture.Window).EffectiveMode, Is.EqualTo(DiffViewMode.Unified));
        Assert.That(Visible(fixture.Window).Text, Does.Contain("no commits yet"));
        Assert.That(fixture.Window.LastException, Is.Null);
    }

    private static async Task<Fixture> ConflictAsync(bool useParentHunks = false)
    {
        string[] basis = Enumerable.Range(1, 30).Select(index => "line " + index).ToArray();
        Fixture fixture = await Fixture.CreateAsync(string.Join('\n', basis) + "\n", string.Join('\n', basis) + "\n");
        try
        {
            const string literal = "literal λ[1]\n.txt";
            await System.IO.File.WriteAllTextAsync(Path.Combine(fixture.Repository, literal), "basis literal\n");
            await Git(fixture, "add", "--", literal); await Git(fixture, "commit", "-m", "Literal basis");
            await Git(fixture, "checkout", "-b", "topic");
            string[] topic = (string[])basis.Clone(); topic[0] = "topic first"; topic[19] = "topic second";
            await System.IO.File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), string.Join('\n', topic) + "\n");
            await System.IO.File.WriteAllTextAsync(Path.Combine(fixture.Repository, literal), "topic literal\n");
            await Git(fixture, "add", "--all"); await Git(fixture, "commit", "-m", "Topic edits");
            await Git(fixture, "checkout", "main");
            string[] main = (string[])basis.Clone(); main[0] = "main first"; main[19] = "main second";
            await System.IO.File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), string.Join('\n', main) + "\n");
            await System.IO.File.WriteAllTextAsync(Path.Combine(fixture.Repository, literal), "main literal\n");
            await Git(fixture, "add", "--all"); await Git(fixture, "commit", "-m", "Main edits");
            GitResult merge = await fixture.Git.RunAsync(fixture.Repository, ["merge", "--no-ff", "--no-edit", "topic"]);
            Assert.That(merge.ExitCode, Is.EqualTo(1), merge.StandardError);
            string[] resolved = (string[])basis.Clone();
            resolved[0] = useParentHunks ? "main first" : "resolved λ🙂";
            resolved[19] = useParentHunks ? "topic second" : "resolved second";
            await System.IO.File.WriteAllTextAsync(Path.Combine(fixture.Repository, "file.txt"), string.Join('\n', resolved) + "\n");
            await System.IO.File.WriteAllTextAsync(Path.Combine(fixture.Repository, literal), "resolved literal\n");
            await Git(fixture, "add", "--all"); await Git(fixture, "commit", "-m", "Resolved merge");
            await fixture.Window.OpenRepositoryAsync(fixture.Repository);
            History(fixture.Window).SelectedItem = History(fixture.Window).Items.OfType<GitCommit>().Single(commit => commit.Subject == "Resolved merge");
            await Idle(fixture.Window);
            Assert.That(fixture.Window.LastException, Is.Null, fixture.Window.LastException?.ToString());
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    private static async Task<GitResult> Git(Fixture fixture, params string[] args)
    {
        GitResult result = await fixture.Git.RunAsync(fixture.Repository, args);
        Assert.That(result.Success, Is.True, result.StandardError);
        return result;
    }
    private static TreeView Files(MainWindow main) => Find<TreeView>(main, "RevisionFiles");
    private static ListBox History(MainWindow main) => Find<ListBox>(main, "RevisionHistory");
    private static GitRevisionDiffGroup Group(TreeViewItem item) => (GitRevisionDiffGroup)item.Tag!;
    private static GitRevisionFile File(TreeViewItem item) => ((RevisionDiffFileSelection)item.Tag!).File;
    private static DiffView Visible(MainWindow main) => main.GetVisualDescendants().OfType<DiffView>().Single(view => view.IsEffectivelyVisible);
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static MenuItem Setting(MainWindow main, string name) => Find<FileStatusListView>(main, "RevisionFileStatusList").SettingsMenu.Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static void Click(MainWindow main, string name) => Find<Button>(main, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Idle(MainWindow main)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (main.IsBusy && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(main.IsBusy, Is.False, "Revision diff operation did not finish.");
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }
    private static void Capture(MainWindow main, string name)
    {
        main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        using WriteableBitmap frame = main.CaptureRenderedFrame() ?? throw new AssertionException("No browser frame rendered.");
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(Path.Combine(root.FullName, "src/linux/GitExtensions.Desktop/GitExtensions.Desktop.csproj"))) { root = root.Parent; }
        string directory = Path.Combine(root?.FullName ?? TestContext.CurrentContext.WorkDirectory, "artifacts/linux/screenshots");
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, name); frame.Save(path); TestContext.AddTestAttachment(path);
    }
}
