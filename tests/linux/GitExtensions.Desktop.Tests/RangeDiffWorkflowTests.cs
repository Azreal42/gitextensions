using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
public class RangeDiffWorkflowTests
{
    [AvaloniaTest]
    public async Task ContextMenuUsesTheRealTwoSeriesAndOriginalGroupedViewerInBothThemes()
    {
        using SeriesFixture fixture = await SeriesFixture.CreateAsync();
        MainWindow main = fixture.Main;
        string before = await SnapshotAsync(fixture);
        await SelectAsync(main, fixture.SecondTip, fixture.FirstTip);
        MenuItem menu = RangeAction(main);
        Assert.That(menu.Icon, Is.TypeOf<Image>());
        Assert.That(((Image)menu.Icon!).Bounds.Size.Width, Is.LessThanOrEqualTo(16));
        Assert.That(((Image)menu.Icon!).Width, Is.EqualTo(16));
        Assert.That(Details(main).SelectedItem, Is.TypeOf<TabItem>());
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme;
            Details(main).SelectedItem = Details(main).Items.OfType<TabItem>().Single(tab => tab.Name == "CommitInfoTab");
            main.UpdateLayout();
            Invoke(menu);
            await RangeIdleAsync(main);
            GitRevisionDiffGroup group = SelectedRange(main);
            Assert.That(group.RangeDiff!.First.TipRevision, Is.EqualTo(fixture.FirstTip));
            Assert.That(group.RangeDiff.Second.TipRevision, Is.EqualTo(fixture.SecondTip));
            Assert.That(group.RangeDiff.First.CommitCount, Is.EqualTo(2));
            Assert.That(group.RangeDiff.Second.CommitCount, Is.EqualTo(2));
            Assert.That(GroupKinds(main), Is.EqualTo(new[]
            {
                GitRevisionDiffGroupKind.Comparison, GitRevisionDiffGroupKind.RangeDiff,
                GitRevisionDiffGroupKind.BaseToSecond, GitRevisionDiffGroupKind.BaseToFirst
            }));
            DiffView view = Viewer(main);
            Assert.That(view.Text, Is.EqualTo(await RawRangeAsync(fixture, group.RangeDiff)));
            Assert.That(view.Text, Does.Contain("old option").And.Contain("new option"));
            Assert.That(view.Text, Does.Not.Contain('\u001b'));
            Assert.That(view.EffectiveMode, Is.EqualTo(DiffViewMode.RangeDiff));
            Assert.That(view.CanStagePatch, Is.False);
            Assert.That((Details(main).SelectedItem as TabItem)?.Name, Is.EqualTo("DiffTab"));
            TreeView tree = Files(main);
            main.UpdateLayout();
            Assert.That(tree.TranslatePoint(default, main)!.Value.X + tree.Bounds.Width,
                Is.LessThanOrEqualTo(view.TranslatePoint(default, main)!.Value.X));
            foreach (TreeViewItem node in tree.Items.OfType<TreeViewItem>())
            {
                Assert.That(node.Header, Is.TypeOf<StackPanel>());
                Image icon = ((StackPanel)node.Header!).Children.OfType<Image>().Single();
                Assert.That(icon.Width, Is.EqualTo(16));
                Assert.That(icon.Height, Is.EqualTo(16));
            }
            await CaptureAsync(main, "classic-range-diff-" + theme + ".png");
        }
        Assert.That(await SnapshotAsync(fixture), Is.EqualTo(before));
        Assert.That(main.OwnedWindows, Is.Empty, "Range diff belongs to the existing grouped viewer.");
    }

    [AvaloniaTest]
    public async Task ThreeActualClicksKeepTheExplicitCommonBaseEvenWhenTheGraphPlacesItLast()
    {
        using SeriesFixture fixture = await SeriesFixture.CreateAsync();
        MainWindow main = fixture.Main;
        await SelectAsync(main, fixture.SecondTip, fixture.EarlierBase, fixture.FirstTip);
        string[] selectedInGraph = History(main).Items.OfType<GitCommit>()
            .Where(commit => History(main).SelectedItems!.OfType<GitCommit>().Any(selected => selected.Hash == commit.Hash))
            .Select(commit => commit.Hash).ToArray();
        Assert.That(selectedInGraph, Is.Not.EqualTo(new[] { fixture.SecondTip, fixture.EarlierBase, fixture.FirstTip }),
            "The fixture must distinguish real selection order from sorting the graph.");
        Invoke(RangeAction(main)); await RangeIdleAsync(main);
        GitRangeDiffComparison comparison = SelectedRange(main).RangeDiff!;
        Assert.That(comparison.SelectionCount, Is.EqualTo(3));
        Assert.That(comparison.First.BaseRevision, Is.EqualTo(fixture.EarlierBase));
        Assert.That(comparison.Second.BaseRevision, Is.EqualTo(fixture.EarlierBase));
        Assert.That(comparison.CommonAncestor, Is.EqualTo(fixture.EarlierBase));
        Assert.That(comparison.First.CommitCount, Is.EqualTo(3));
        Assert.That(comparison.Second.CommitCount, Is.EqualTo(3));
        Assert.That(Viewer(main).Text, Is.EqualTo(await RawRangeAsync(fixture, comparison)));
        Assert.That(Viewer(main).Text, Does.Contain("Shared change before the two series"),
            "Using only tip...tip would silently omit the explicitly selected earlier base.");
        Assert.That((History(main).SelectedItem as GitCommit)?.Hash, Is.EqualTo(fixture.SecondTip));
        await AssertRangeRefreshAsync(main, fixture);
    }

    [AvaloniaTest]
    public async Task FourActualClicksRetainBothBoundsAndTheOlderPrimaryInsteadOfReorderingTheGraph()
    {
        using SeriesFixture fixture = await SeriesFixture.CreateAsync();
        MainWindow main = fixture.Main;
        // The older first-series tip is the primary B for this comparison.
        await SelectAsync(main, fixture.FirstTip, fixture.FirstBase, fixture.SecondTip, fixture.SecondBase);
        Invoke(RangeAction(main)); await RangeIdleAsync(main);
        GitRangeDiffComparison comparison = SelectedRange(main).RangeDiff!;
        Assert.That(comparison.SelectionCount, Is.EqualTo(4));
        Assert.That(comparison.First.TipRevision, Is.EqualTo(fixture.SecondTip));
        Assert.That(comparison.First.BaseRevision, Is.EqualTo(fixture.SecondBase));
        Assert.That(comparison.Second.TipRevision, Is.EqualTo(fixture.FirstTip));
        Assert.That(comparison.Second.BaseRevision, Is.EqualTo(fixture.FirstBase));
        Assert.That(comparison.First.CommitCount, Is.EqualTo(1));
        Assert.That(comparison.Second.CommitCount, Is.EqualTo(1));
        Assert.That(Viewer(main).Text, Is.EqualTo(await RawRangeAsync(fixture, comparison)));
        Assert.That(Viewer(main).Text, Does.Contain("Refine the literal file").And.Not.Contain("Implement the option"));
        Assert.That((History(main).SelectedItem as GitCommit)?.Hash, Is.EqualTo(fixture.FirstTip));
        Assert.That(History(main).SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash),
            Is.EqualTo(new[] { fixture.FirstTip, fixture.FirstBase, fixture.SecondTip, fixture.SecondBase }));
        await AssertRangeRefreshAsync(main, fixture);
    }

    [AvaloniaTest]
    public async Task ZeroContextIsTheOriginalSummaryAndHeaderNavigationNeverMakesItStageable()
    {
        using SeriesFixture fixture = await SeriesFixture.CreateAsync();
        MainWindow main = fixture.Main;
        await SelectAsync(main, fixture.SecondTip, fixture.FirstTip);
        Invoke(RangeAction(main)); await RangeIdleAsync(main);
        GitRangeDiffComparison comparison = SelectedRange(main).RangeDiff!;
        DiffView view = Viewer(main);
        Assert.That(view.Text, Is.EqualTo(await RawRangeAsync(fixture, comparison)));
        Assert.That(view.Text, Does.Contain("old option").And.Contain("new option"));
        view.Select(0, 0); view.Editor.TextArea.Focus();
        main.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        main.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Assert.That(view.CaretOffset, Is.GreaterThan(0));
        Assert.That(view.Editor.Document.GetText(view.Editor.Document.GetLineByOffset(view.CaretOffset)),
            Does.Match(@"^\s*(\d+|-):"));
        for (int count = 0; count < 3; count++)
        {
            Button button = Field<Button>(main, "RevisionDiffDecreaseContext");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await MainIdleAsync(main);
        }
        Assert.That(view.Text, Is.EqualTo(await RawRangeAsync(fixture, comparison, context: 0)));
        Assert.That(view.Text, Does.Not.Contain("old option").And.Not.Contain("new option"));
        Assert.That(view.Text, Does.Contain("Implement the option").And.Contain("Refine the literal file"));
        Assert.That(view.CanStagePatch, Is.False);
        Button increase = Field<Button>(main, "RevisionDiffIncreaseContext");
        increase.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await MainIdleAsync(main);
        Assert.That(view.Text, Is.EqualTo(await RawRangeAsync(fixture, comparison, context: 1)));
    }

    [AvaloniaTest]
    public async Task IneligibleAncestorAndSingleSelectionsShowRealErrorsWithoutInventingSeries()
    {
        using SeriesFixture fixture = await SeriesFixture.CreateAsync();
        MainWindow main = fixture.Main;
        string before = await SnapshotAsync(fixture);
        await SelectAsync(main, fixture.FirstTip, fixture.FirstBase);
        Assert.That(GroupKinds(main), Does.Not.Contain(GitRevisionDiffGroupKind.RangeDiff));
        Invoke(RangeAction(main)); await MainIdleAsync(main);
        Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.Contain("do not form two valid commit ranges"));
        Assert.That(GroupKinds(main), Does.Not.Contain(GitRevisionDiffGroupKind.RangeDiff));
        await SelectAsync(main, fixture.SecondTip);
        Invoke(RangeAction(main)); await MainIdleAsync(main);
        Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.Contain("two, three or four commits"));
        Assert.That(await SnapshotAsync(fixture), Is.EqualTo(before));
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task TurningOffAllParentGroupsRemainsAnExplicitPreferenceWhenInvokingRangeDiff()
    {
        using SeriesFixture fixture = await SeriesFixture.CreateAsync();
        MainWindow main = fixture.Main;
        await SelectAsync(main, fixture.SecondTip, fixture.FirstTip);
        var settings = Field<FileStatusListView>(main, "RevisionFileStatusList").SettingsMenu;
        MenuItem all = settings.Items.OfType<MenuItem>().Single(item => item.Name == "RevisionDiffAllParents");
        all.IsChecked = false; Invoke(all); await MainIdleAsync(main);
        RevisionDiffPreferencesStore store = new();
        Assert.That(store.Load().ShowDiffForAllParents, Is.False);
        string saved = await File.ReadAllTextAsync(store.FilePath);
        Invoke(RangeAction(main)); await MainIdleAsync(main);
        Assert.That(Field<TextBlock>(main, "RepositoryStatus").Text, Does.Contain("Enable file differences for all parents"));
        Assert.That(GroupKinds(main), Does.Not.Contain(GitRevisionDiffGroupKind.RangeDiff));
        Assert.That(await File.ReadAllTextAsync(store.FilePath), Is.EqualTo(saved));
        Assert.That(main.LastException, Is.Null);
    }

    [AvaloniaTest]
    public async Task RefreshPreservesTheActualRangeNodeItsLiteralFilesAndSourceSelection()
    {
        using SeriesFixture fixture = await SeriesFixture.CreateAsync();
        MainWindow main = fixture.Main;
        await SelectAsync(main, fixture.SecondTip, fixture.FirstTip);
        Invoke(RangeAction(main)); await RangeIdleAsync(main);
        string[] selected = History(main).SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
        GitRangeDiffComparison before = SelectedRange(main).RangeDiff!;
        main.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
        main.KeyReleaseQwerty(PhysicalKey.F5, RawInputModifiers.None);
        await RangeIdleAsync(main);
        Assert.That(SelectedRange(main).RangeDiff, Is.EqualTo(before));
        Assert.That(History(main).SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(selected));
        Assert.That(GroupNodes(main).Where(node => node.Tag is GitRevisionDiffGroup { Kind: GitRevisionDiffGroupKind.BaseToSecond })
            .SelectMany(node => node.Items.OfType<TreeViewItem>()).Select(node => ((RevisionDiffFileSelection)node.Tag!).File.Path),
            Does.Contain(SeriesFixture.LiteralPath));
        Assert.That(Viewer(main).Text, Is.EqualTo(await RawRangeAsync(fixture, before)));
        Assert.That(main.LastException, Is.Null);
    }

    private static ListBox History(MainWindow main) => Field<ListBox>(main, "RevisionHistory");
    private static TreeView Files(MainWindow main) => Field<TreeView>(main, "RevisionFiles");
    private static TabControl Details(MainWindow main) => Field<TabControl>(main, "DetailsTabs");
    private static DiffView Viewer(MainWindow main) => Field<DiffView>(main, "CommitDiff");
    private static IEnumerable<TreeViewItem> GroupNodes(MainWindow main) => Files(main).Items.OfType<TreeViewItem>();
    private static GitRevisionDiffGroupKind[] GroupKinds(MainWindow main) => GroupNodes(main)
        .Where(node => node.Tag is GitRevisionDiffGroup).Select(node => ((GitRevisionDiffGroup)node.Tag!).Kind).ToArray();
    private static GitRevisionDiffGroup SelectedRange(MainWindow main)
    {
        Assert.That(Files(main).SelectedItem, Is.TypeOf<TreeViewItem>());
        Assert.That(((TreeViewItem)Files(main).SelectedItem!).Tag, Is.TypeOf<GitRevisionDiffGroup>());
        GitRevisionDiffGroup group = (GitRevisionDiffGroup)((TreeViewItem)Files(main).SelectedItem!).Tag!;
        Assert.That(group.Kind, Is.EqualTo(GitRevisionDiffGroupKind.RangeDiff));
        return group;
    }
    private static MenuItem RangeAction(MainWindow main)
    {
        MenuItem comparison = History(main).ContextMenu!.Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Compare");
        return comparison.Items.OfType<MenuItem>().Single(menu => menu.Header as string == "Range diff");
    }
    private static T Field<T>(Control main, string name) where T : Control
        => main.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Invoke(MenuItem menu) => menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static async Task SelectAsync(MainWindow main, params string[] revisions)
    {
        ListBox history = History(main);
        history.SelectedItems!.Clear(); await MainIdleAsync(main);
        history.SelectedItem = history.Items.OfType<GitCommit>().Single(commit => commit.Hash == revisions[0]);
        await MainIdleAsync(main);
        foreach (string revision in revisions.Skip(1))
        {
            history.SelectedItems.Add(history.Items.OfType<GitCommit>().Single(commit => commit.Hash == revision));
            await MainIdleAsync(main);
        }
        // BelowList initially presents Commit; inspect diff controls only after the
        // same explicit tab selection a user would perform.
        Details(main).SelectedItem = Details(main).Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab");
        main.UpdateLayout();
        Assert.That((history.SelectedItem as GitCommit)?.Hash, Is.EqualTo(revisions[0]));
    }
    private static async Task MainIdleAsync(MainWindow main)
    {
        await Task.Delay(25);
        await UntilAsync(() => !main.IsBusy);
        Assert.That(main.LastException, Is.Null, main.LastException?.ToString());
    }
    private static async Task RangeIdleAsync(MainWindow main)
    {
        await MainIdleAsync(main);
        await UntilAsync(() => !main.IsBusy && Files(main).SelectedItem is TreeViewItem { Tag: GitRevisionDiffGroup { Kind: GitRevisionDiffGroupKind.RangeDiff } }
            && Viewer(main).EffectiveMode == DiffViewMode.RangeDiff && !string.IsNullOrWhiteSpace(Viewer(main).Text));
    }
    private static async Task AssertRangeRefreshAsync(MainWindow main, SeriesFixture fixture)
    {
        GitRangeDiffComparison comparison = SelectedRange(main).RangeDiff!;
        string primary = ((GitCommit)History(main).SelectedItem!).Hash;
        string[] order = History(main).SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
        main.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
        main.KeyReleaseQwerty(PhysicalKey.F5, RawInputModifiers.None);
        await RangeIdleAsync(main);
        Assert.That(SelectedRange(main).RangeDiff, Is.EqualTo(comparison));
        Assert.That((History(main).SelectedItem as GitCommit)?.Hash, Is.EqualTo(primary));
        Assert.That(History(main).SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(order));
        Assert.That(GroupNodes(main), Does.Contain(Files(main).SelectedItem), "The selected range must belong to the refreshed tree.");
        Assert.That(Viewer(main).Text, Is.EqualTo(await RawRangeAsync(fixture, comparison)));
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate() && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        Assert.That(predicate(), Is.True, "The real range-diff workflow did not settle within 15 seconds.");
    }
    private static async Task CaptureAsync(MainWindow main, string filename)
    {
        main.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(60);
        using var image = main.CaptureRenderedFrame();
        Assert.That(image, Is.Not.Null);
        string directory = Path.Combine(AppContext.BaseDirectory, "artifacts/linux/screenshots");
        Directory.CreateDirectory(directory);
        image!.Save(Path.Combine(directory, filename));
    }
    private static async Task<string> SnapshotAsync(SeriesFixture fixture)
    {
        GitResult refs = await fixture.Git("show-ref");
        GitResult status = await fixture.Git("status", "--porcelain=v1", "-z");
        return refs.StandardOutput + "\0" + status.StandardOutput;
    }
    private static async Task<string> RawRangeAsync(SeriesFixture fixture, GitRangeDiffComparison comparison, int context = 3)
    {
        List<string> arguments = ["--no-pager", "-c", "core.quotepath=false", "--literal-pathspecs", "range-diff", "--find-renames", "--find-copies", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=" + context];
        if (context == 0) { arguments.Add("--no-patch"); }
        arguments.Add(comparison.First.BaseRevision + ".." + comparison.First.TipRevision);
        arguments.Add(comparison.Second.BaseRevision + ".." + comparison.Second.TipRevision);
        return (await fixture.Git(arguments.ToArray())).StandardOutput;
    }

    private sealed class SeriesFixture : IDisposable
    {
        public const string LiteralPath = "[range] λ;\n$literal.txt";
        private readonly Fixture _fixture;
        private readonly DateTimeOffset _date = DateTimeOffset.UtcNow.AddDays(1);
        public MainWindow Main => _fixture.Window;
        public GitRepository Repository { get; }
        public string EarlierBase { get; private set; } = "";
        public string FirstBase { get; private set; } = "";
        public string FirstTip { get; private set; } = "";
        public string SecondBase { get; private set; } = "";
        public string SecondTip { get; private set; } = "";
        private SeriesFixture(Fixture fixture, GitRepository repository) { _fixture = fixture; Repository = repository; }
        public static async Task<SeriesFixture> CreateAsync()
        {
            string content = string.Join('\n', Enumerable.Range(1, 40).Select(index => "stable line " + index)) + "\n";
            Fixture native = await Fixture.CreateAsync(content, content, commitInfoPosition: null);
            SeriesFixture fixture = new(native, await GitRepository.OpenAsync(native.Repository));
            try
            {
                fixture.EarlierBase = await fixture.Repository.ResolveRevisionAsync("HEAD");
                await File.WriteAllTextAsync(Path.Combine(native.Repository, "common.txt"), "shared original change\n");
                await fixture.CommitAsync("Shared change before the two series", 1);
                string branchPoint = await fixture.Repository.ResolveRevisionAsync("HEAD");
                await fixture.Git("checkout", "-b", "old-series");
                await File.WriteAllTextAsync(Path.Combine(native.Repository, "file.txt"), content.Replace("stable line 20", "old option", StringComparison.Ordinal));
                await fixture.CommitAsync("Implement the option", 4);
                fixture.FirstBase = await fixture.Repository.ResolveRevisionAsync("HEAD");
                await File.WriteAllTextAsync(Path.Combine(native.Repository, LiteralPath), content.Replace("stable line 20", "old literal option", StringComparison.Ordinal));
                await fixture.CommitAsync("Refine the literal file", 6);
                fixture.FirstTip = await fixture.Repository.ResolveRevisionAsync("HEAD");
                await fixture.Git("checkout", "-b", "new-series", branchPoint);
                await File.WriteAllTextAsync(Path.Combine(native.Repository, "file.txt"), content.Replace("stable line 20", "new option", StringComparison.Ordinal));
                await fixture.CommitAsync("Implement the option", 8);
                fixture.SecondBase = await fixture.Repository.ResolveRevisionAsync("HEAD");
                await File.WriteAllTextAsync(Path.Combine(native.Repository, LiteralPath), content.Replace("stable line 20", "new literal option", StringComparison.Ordinal));
                await fixture.CommitAsync("Refine the literal file", 10);
                fixture.SecondTip = await fixture.Repository.ResolveRevisionAsync("HEAD");
                await native.Window.OpenRepositoryAsync(native.Repository);
                await MainIdleAsync(native.Window);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public async Task<GitResult> Git(params string[] arguments)
        {
            GitResult result = await _fixture.Git.RunAsync(_fixture.Repository, arguments);
            Assert.That(result.Success, Is.True, result.StandardError);
            return result;
        }
        private async Task CommitAsync(string message, int minute)
        {
            await Git("add", "--all");
            string date = _date.AddMinutes(minute).ToString("O", CultureInfo.InvariantCulture);
            GitResult result = await _fixture.Git.RunAsync(_fixture.Repository, ["commit", "-m", message],
                environment: new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date });
            Assert.That(result.Success, Is.True, result.StandardError);
        }
        public void Dispose() => _fixture.Dispose();
    }
}
