using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ForkPointSelectionTests
{
    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task PhysicalCtrlKAdvancesPastTheSelectedBranchThenRepeatedlyMovesOnlyTheDiffBase(bool dark)
    {
        await using Fixture f = await Fixture.CreateAsync(dark: dark);
        await f.SelectAsync(f.T); string[] navigation = f.Navigation(); int navigationIndex = f.NavigationIndex;
        await f.NextAsync();
        Assert.That(f.Selected, Is.EqualTo(new[] { f.T, f.M })); f.AssertComparison(f.M, f.T);
        Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.M), "Windows CurrentCell remains on the fork selected before re-toggling the original tip.");
        Assert.That(f.Navigation(), Is.EqualTo(navigation)); Assert.That(f.NavigationIndex, Is.EqualTo(navigationIndex));
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(new[] { f.T, f.R })); f.AssertComparison(f.R, f.T);
        int calls = GitCommandLog.Snapshot().Count;
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(new[] { f.T, f.R }));
        Assert.That(GitCommandLog.Snapshot().Count, Is.EqualTo(calls), "At the loaded root the same final selection needs no new detail read.");
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task AMergeWithOneChildIsSkippedBecauseForkDetectionCountsChildrenRatherThanParents()
    {
        await using Fixture f = await Fixture.CreateAsync(fork: false);
        Assert.That(f.Reader.Single(commit => commit.Hash == f.M).Parents, Is.EqualTo(new[] { f.Q, f.P }));
        await f.SelectAsync(f.T); await f.NextAsync();
        Assert.That(f.Selected, Is.EqualTo(new[] { f.T, f.R })); f.AssertComparison(f.R, f.T);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase("local")] [TestCase("remote-hidden")] [TestCase("tag")]
    public async Task FirstParentUsesExactLocalOrRemoteRefsEvenWhenTheRemoteLabelIsHiddenButIgnoresTags(string reference)
    {
        await using Fixture f = await Fixture.CreateAsync(reference: reference);
        Assert.That(f.Reader.Single(commit => commit.Hash == f.M).Parents[0], Is.EqualTo(f.Q));
        if (reference == "remote-hidden")
        {
            Assert.That(f.Graph.References.Any(item => item.Name == "refs/remotes/origin/hold"), Is.False);
            Assert.That(f.Reader.Any(commit => commit.Hash == f.Q), Is.True);
        }
        await f.SelectAsync(f.M); await f.NextAsync();
        string expected = reference == "tag" ? f.R : f.Q;
        Assert.That(f.Selected, Is.EqualTo(new[] { f.M, expected })); f.AssertComparison(expected, f.M);
        Assert.That(f.Selected, Does.Not.Contain(f.P), "The second parent carries its own local branch but is never used by this first-parent command.");
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task TheLoadedBoundaryStopsWithoutFetchingAParentOrOpeningAFilteredRevisionMessage()
    {
        await using Fixture f = await Fixture.CreateAsync(limit: 1);
        Assert.That(f.Reader.Select(commit => commit.Hash), Is.EqualTo(new[] { f.T }));
        await f.SelectAsync(f.T); int commands = GitCommandLog.Snapshot().Count; await f.NextAsync();
        Assert.That(f.Selected, Is.EqualTo(new[] { f.T })); Assert.That(GitCommandLog.Snapshot().Count, Is.EqualTo(commands));
        Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task ARealHideMergeFilterCreatesALoadedParentBoundaryInsteadOfResolvingAroundTheMissingRow()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.FilterAsync(hideMerge: true);
        Assert.That(f.Reader.Select(commit => commit.Hash), Does.Not.Contain(f.M));
        Assert.That(f.Reader.Single(commit => commit.Hash == f.T).Parents[0], Is.EqualTo(f.M));
        await f.SelectAsync(f.T); int commands = GitCommandLog.Snapshot().Count; await f.NextAsync();
        Assert.That(f.Selected, Is.EqualTo(new[] { f.T })); Assert.That(GitCommandLog.Snapshot().Count, Is.EqualTo(commands));
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task FirstParentModeUsesTheActualLoadedGraphEdgesRatherThanRawSecondParentEdges(bool firstParent)
    {
        await using Fixture f = await Fixture.CreateAsync(fork: false, sideFromQ: true);
        await f.FilterAsync(firstParent: firstParent, currentOnly: true);
        Assert.That(f.Reader.Any(commit => commit.Hash == f.P), Is.EqualTo(!firstParent));
        await f.SelectAsync(f.M); await f.NextAsync();
        string expected = firstParent ? f.R : f.Q;
        Assert.That(f.Selected, Is.EqualTo(new[] { f.M, expected })); f.AssertComparison(expected, f.M);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task ABaseThatReachesTheKeptOriginalRowCollapsesToThatRowWithoutDeselectingTheLastSelection()
    {
        await using Fixture f = await Fixture.CreateAsync();
        // Native SelectedRows is newest first: M then Ctrl+R supplies [R,M].
        await f.SelectAsync(f.M); await f.AddAsync(f.R);
        Assert.That(f.Selected, Is.EqualTo(new[] { f.M, f.R })); await f.NextAsync();
        Assert.That(f.Selected, Is.EqualTo(new[] { f.R })); Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.R));
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task NoSelectionIsANoOpAndCtrlKUsesTheExistingApplicationShortcutGuardWithoutEditingText()
    {
        await using Fixture f = await Fixture.CreateAsync();
        f.History.SelectedItems!.Clear(); await f.IdleAsync(); f.History.Focus();
        int commands = GitCommandLog.Snapshot().Count; await f.NextAsync();
        Assert.That(f.Selected, Is.Empty); Assert.That(GitCommandLog.Snapshot().Count, Is.EqualTo(commands));
        await f.SelectAsync(f.T);
        TextBox path = Field<TextBox>(f.Main, "RepositoryPath"); PushProcessTestDriver.Pointer(path); Press(f.Main, PhysicalKey.A, RawInputModifiers.Control);
        string? text = path.Text;
        Press(f.Main, PhysicalKey.K, RawInputModifiers.Control); await f.IdleAsync();
        // Windows IsTextEditKey does not classify Ctrl+K as an editing key; the existing portable guard agrees.
        Assert.That(path.Text, Is.EqualTo(text)); Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
        Assert.That(f.Selected, Is.EqualTo(new[] { f.T, f.M })); f.AssertComparison(f.M, f.T);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task TheConfiguredRevisionGridBindingOwnsTheCommandInsteadOfAnUnconfigurableCtrlKFallback()
    {
        await using Fixture f = await Fixture.CreateAsync(); await f.SelectAsync(f.T);
        HotkeyBindings bindings = DesktopHotkeys.Current.Clone(); bindings.Set("RevisionGrid", HotkeyCatalog.Find("RevisionGrid", "SelectNextForkPointAsDiffBase")!.Code, new(Key.F9));
        new HotkeyPreferencesStore().Save(bindings); Dispatcher.UIThread.RunJobs();
        Press(f.Main, PhysicalKey.K, RawInputModifiers.Control); await f.IdleAsync(); Assert.That(f.Selected, Is.EqualTo(new[] { f.T }));
        Press(f.Main, PhysicalKey.F9); await f.IdleAsync(); Assert.That(f.Selected, Is.EqualTo(new[] { f.T, f.M }));
        f.AssertComparison(f.M, f.T); f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task ASelectedArtificialEndpointRemainsTypedWhileItsBaseMovesInTheSharedDiffReader(bool staged)
    {
        await using Fixture f = await Fixture.CreateAsync(artificial: true, dirty: true);
        PushProcessTestDriver.Pointer(Field<Button>(f.Main, staged ? "IndexRevision" : "WorkingDirectoryRevision")); await f.IdleAsync();
        GitCommandLog.Clear(); await f.NextAsync();
        GitComparison comparison = f.ActiveComparisons().Values.Single();
        Assert.That(comparison.Base, Is.EqualTo(new GitCompareRevision(f.M)));
        Assert.That(comparison.Compare.Kind, Is.EqualTo(staged ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree));
        Assert.That(f.Selected, Is.EqualTo(new[] { f.M }), "The other endpoint is the existing typed artificial selection, never a fake GitCommit.");
        Assert.That(((TabItem)Field<TabControl>(f.Main, "DetailsTabs").SelectedItem!).Name, Is.EqualTo("DiffTab"));
        Assert.That(((FileContentView)Private(f.Main, "_commitContent")!).TextView.Text, Does.Contain(staged ? "+INDEX FILE" : "+WORKTREE FILE"));
        Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.M));
        await f.NextAsync(); comparison = f.ActiveComparisons().Values.Single();
        Assert.That(comparison.Base, Is.EqualTo(new GitCompareRevision(f.R))); Assert.That(comparison.Compare.Kind, Is.EqualTo(staged ? GitCompareRevisionKind.Index : GitCompareRevisionKind.WorkingTree));
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public async Task AnUnbornArtificialSelectionHasNoInventedHeadAndNoRepositoryReadOnCtrlK(bool staged)
    {
        await using Fixture f = await Fixture.CreateAsync(artificial: true, unborn: true);
        PushProcessTestDriver.Pointer(Field<Button>(f.Main, staged ? "IndexRevision" : "WorkingDirectoryRevision")); await f.IdleAsync();
        Control button = Field<Button>(f.Main, staged ? "IndexRevision" : "WorkingDirectoryRevision"); GitCommandLog.Clear();
        await f.NextAsync(); Assert.That(f.Selected, Is.Empty); Assert.That(button.IsKeyboardFocusWithin, Is.True);
        Assert.That(GitCommandLog.Snapshot(), Is.Empty); Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task ThreeEndpointSelectionReplacesTheOldBaseAndReversesTheRetoggledSurvivorsLikeWindows()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.SelectAsync(f.T); await f.AddAsync(f.Q); await f.AddAsync(f.P);
        string[] navigation = f.Navigation(); int navigationIndex = f.NavigationIndex;
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(new[] { f.Q, f.P, f.M }));
        f.AssertComparison(f.M, f.Q); Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.M));
        Assert.That(f.Navigation(), Is.EqualTo(navigation)); Assert.That(f.NavigationIndex, Is.EqualTo(navigationIndex));
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    [TestCase("T,Q,P", "Q,P,M", "M", "E", "M")]
    [TestCase("T,Q,M", "Q,M", "M", "E", "M")]
    [TestCase("T,M,Q", "Q", "Q", "P", "R")]
    [TestCase("T,M,Q,P", "Q,P", "M", "E", "P")]
    [TestCase("T,Q,M,P", "Q,P", "P", "M", "P")]
    [TestCase("T,Q,P,E", "Q,P,E,M", "M", "E", "E")]
    [TestCase("T,Q,P,E,R", "Q,P,E,R,M", "M", "E", "M")]
    public async Task NativeRetoggleOrderAndCollisionsKeepTheCorrectPrimaryCaretAndFirstPhysicalArrow(
        string clicks, string expectedRows, string caret, string firstUp, string comparisonBase)
    {
        await using Fixture f = await Fixture.CreateAsync();
        Assert.That(f.History.Items.OfType<GitCommit>().Select(commit => commit.Hash),
            Is.EqualTo(f.Hashes("T,E,M,P,Q,R")), "The native graph order fixes the physical Up neighbour without sorting a test oracle.");
        string[] inputs = f.Hashes(clicks), expected = f.Hashes(expectedRows);
        await f.SelectAsync(inputs[0]); foreach (string hash in inputs.Skip(1)) { await f.AddAsync(hash); }
        Assert.That(f.Selected, Is.EquivalentTo(inputs));
        string[] navigation = f.Navigation(); int navigationIndex = f.NavigationIndex;
        List<string[]> publications = [];
        f.History.SelectionChanged += (_, _) => publications.Add(f.Selected);
        await f.NextAsync();
        Assert.That(f.Selected, Is.EqualTo(expected));
        Assert.That((f.History.SelectedItem as GitCommit)?.Hash, Is.EqualTo(expected[0]), "The displayed commit and shared readers must use the Windows primary.");
        Assert.That(publications.Count, Is.EqualTo(1), "Publish the final selection once, without a transient empty or fork-only detail read.");
        Assert.That(publications[0], Is.EqualTo(expected), "Every observer sees the final canonical order immediately.");
        Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.Hash(caret)));
        Assert.That(f.History.Selection.AnchorIndex, Is.EqualTo(f.History.Items.IndexOf(f.Reader.Single(commit => commit.Hash == f.Hash(caret)))));
        GitRevisionDiffGroupKind kind = expected.Length == 1 ? GitRevisionDiffGroupKind.Parent : GitRevisionDiffGroupKind.Comparison;
        IReadOnlyList<GitRevisionDiffGroup> groups = (IReadOnlyList<GitRevisionDiffGroup>)Private(f.Main, "_revisionDiffGroups")!;
        Assert.That(groups.Any(group => group.Kind == kind && group.Revision == expected[0] && group.BaseRevision == f.Hash(comparisonBase)), Is.True);
        Assert.That(f.Navigation(), Is.EqualTo(navigation)); Assert.That(f.NavigationIndex, Is.EqualTo(navigationIndex));
        Assert.That(f.Main.OwnedWindows.Where(window => window.IsVisible), Is.Empty); f.AssertNoReload();
        Press(f.Main, PhysicalKey.ArrowUp); await f.IdleAsync();
        Assert.That(f.Selected, Is.EqualTo(new[] { f.Hash(firstUp) }));
        Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.Hash(firstUp))); Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task RepeatedThreeSideCommandsReverseTheRetoggledSidesEvenWhenTheLoadedRootIsAlreadyTheBase()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.SelectAsync(f.T); await f.AddAsync(f.Q); await f.AddAsync(f.P);
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(f.Hashes("Q,P,M"))); f.AssertComparison(f.M, f.Q);
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(f.Hashes("P,Q,R"))); f.AssertComparison(f.R, f.P);
        Assert.That((f.History.SelectedItem as GitCommit)?.Hash, Is.EqualTo(f.P)); Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.R));
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(f.Hashes("Q,P,R"))); f.AssertComparison(f.R, f.Q);
        Assert.That((f.History.SelectedItem as GitCommit)?.Hash, Is.EqualTo(f.Q)); Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.R));
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(f.Hashes("P,Q,R"))); f.AssertComparison(f.R, f.P);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task TwoRawPointerSelectionsUseNativeRecencyRatherThanTheOldListBoxPrimary()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.SelectAsync(f.R); await f.AddAsync(f.M);
        Assert.That(f.Selected, Is.EqualTo(f.Hashes("R,M")));
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(f.Hashes("M,R"))); f.AssertComparison(f.R, f.M);
        Assert.That((f.History.SelectedItem as GitCommit)?.Hash, Is.EqualTo(f.M)); Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.R));
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task APhysicalShiftRangeUsesNativeInsertionOrderAndTheSurvivingCaretAfterCollision()
    {
        await using Fixture f = await Fixture.CreateAsync();
        Assert.That(f.History.Items.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(f.Hashes("T,E,M,P,Q,R")));
        await f.SelectAsync(f.T); await f.ShiftAsync(f.P);
        Assert.That(f.Selected, Is.EquivalentTo(f.Hashes("T,E,M,P")));
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(f.Hashes("E,P"))); f.AssertComparison(f.P, f.E);
        Assert.That((f.History.SelectedItem as GitCommit)?.Hash, Is.EqualTo(f.E)); Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.P));
        Assert.That(f.History.Selection.AnchorIndex, Is.EqualTo(f.History.Items.IndexOf(f.Reader.Single(commit => commit.Hash == f.P))));
        Press(f.Main, PhysicalKey.ArrowUp); await f.IdleAsync();
        Assert.That(f.Selected, Is.EqualTo(new[] { f.M })); Assert.That(FocusedCommit(f.Main), Is.EqualTo(f.M));
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    [AvaloniaTest]
    public async Task AtomicMultiSidePublicationKeepsTheExistingBackAndForwardBookmarksUsable()
    {
        await using Fixture f = await Fixture.CreateAsync();
        await f.SelectAsync(f.R); await f.SelectAsync(f.T); await f.AddAsync(f.Q); await f.AddAsync(f.P);
        string[] navigation = f.Navigation(); int navigationIndex = f.NavigationIndex;
        await f.NextAsync(); Assert.That(f.Selected, Is.EqualTo(f.Hashes("Q,P,M")));
        Assert.That(f.Navigation(), Is.EqualTo(navigation)); Assert.That(f.NavigationIndex, Is.EqualTo(navigationIndex));
        Press(f.Main, PhysicalKey.ArrowLeft, RawInputModifiers.Alt); await f.IdleAsync(); Assert.That(f.Selected, Is.EqualTo(new[] { f.R }));
        Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
        Press(f.Main, PhysicalKey.ArrowRight, RawInputModifiers.Alt); await f.IdleAsync(); Assert.That(f.Selected, Is.EqualTo(new[] { f.T }));
        Assert.That(f.History.IsKeyboardFocusWithin, Is.True);
        f.AssertNoReload(); Assert.That(await f.MaterialAsync(), Is.EqualTo(f.Ready));
    }

    private sealed record Material(string Head, string Index, string Refs, string Configuration, string Files);
    private sealed class Fixture(GitFixture git, MainWindow main, string r, string q, string p, string m, string t) : IAsyncDisposable
    {
        internal MainWindow Main => main;
        internal string R => r; internal string Q => q; internal string P => p; internal string M => m; internal string T => t;
        internal string E => Reader.Single(commit => commit.Subject == "Other child E").Hash;
        internal string Hash(string name) => name switch { "R" => R, "Q" => Q, "P" => P, "M" => M, "E" => E, "T" => T, _ => throw new ArgumentException("Unknown fixture row.", nameof(name)) };
        internal string[] Hashes(string names) => names.Split(',').Select(Hash).ToArray();
        internal ListBox History => Field<ListBox>(main, "RevisionHistory");
        internal CommitGraph Graph => main.GetVisualDescendants().OfType<CommitGraph>().Single();
        internal IReadOnlyList<GitCommit> Reader => Graph.Commits;
        internal string[] Selected => History.SelectedItems!.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
        internal object? Source { get; private set; }
        private object?[] _loadedRows = [];
        private IReadOnlyList<GitCommit> _loadedGraphCommits = [];
        private GitCommit[] _loadedGraphRows = [];
        internal Material Ready { get; private set; } = null!;
        internal string[] Navigation() => ((List<string>)Private(main, "_revisionNavigation")!).ToArray();
        internal int NavigationIndex => (int)Private(main, "_revisionNavigationIndex")!;
        internal static async Task<Fixture> CreateAsync(bool fork = true, string? reference = null, int limit = 300, bool dark = false,
            bool artificial = false, bool dirty = false, bool unborn = false, bool sideFromQ = false)
        {
            GitFixture git = await GitFixture.CreateAsync(unborn: true); MainWindow? main = null;
            try
            {
                git.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                string r = "", q = "", p = "", m = "", t = ""; int tick = 0;
                void Date() { string date = "2020-01-01T00:00:" + (++tick).ToString("D2", CultureInfo.InvariantCulture) + "+00:00"; git.SetEnvironment("GIT_AUTHOR_DATE", date); git.SetEnvironment("GIT_COMMITTER_DATE", date); }
                async Task<string> Commit(string subject) { Date(); await git.GitAsync("add", "--all"); await git.GitAsync("commit", "-m", subject); return await git.Repository.ResolveRevisionAsync("HEAD"); }
                if (!unborn)
                {
                    await File.WriteAllTextAsync(git.File, "ROOT FILE\n"); r = await Commit("Root R");
                    if (!sideFromQ) { await git.GitAsync("branch", "side", r); }
                    await File.WriteAllTextAsync(git.File, "Q FILE\n"); q = await Commit("First parent Q");
                    if (sideFromQ) { await git.GitAsync("branch", "side", q); }
                    await git.GitAsync("checkout", "side"); await File.WriteAllTextAsync(git.PathInRepository("side.txt"), "P OTHER\n"); p = await Commit("Second parent P");
                    await git.GitAsync("checkout", "main"); Date(); await git.GitAsync("merge", "--no-ff", "side", "-m", "Merge M"); m = await git.Repository.ResolveRevisionAsync("HEAD");
                    if (fork) { await git.GitAsync("checkout", "-b", "alternate"); await File.WriteAllTextAsync(git.PathInRepository("alternate.txt"), "E OTHER\n"); await Commit("Other child E"); await git.GitAsync("checkout", "main"); }
                    await File.WriteAllTextAsync(git.File, "TIP FILE\n"); t = await Commit("Tip T");
                    if (reference == "local") { await git.GitAsync("branch", "hold", q); }
                    else if (reference == "remote-hidden") { await git.GitAsync("update-ref", "refs/remotes/origin/hold", q); }
                    else if (reference == "tag") { await git.GitAsync("tag", "tag-only", q); }
                    if (dirty) { await File.WriteAllTextAsync(git.File, "INDEX FILE\n"); await git.GitAsync("add", "--", "file.txt"); await File.WriteAllTextAsync(git.File, "WORKTREE FILE\n"); }
                }
                new PreferencesStore().Save(new() { HistoryLimit = limit });
                new HistoryViewPreferencesStore().Save(new() { ShowArtificialCommits = artificial, ShowRemoteBranches = reference != "remote-hidden" });
                new BrowserLayoutPreferencesStore().Save(new(CommitInfoPosition.RightwardFromList));
                main = new() { Width = 1440, Height = 960, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light }; main.Show(); await main.OpenRepositoryAsync(git.Repository.Path);
                Fixture f = new(git, main, r, q, p, m, t); await f.IdleAsync();
                TabControl tabs = Field<TabControl>(main, "DetailsTabs"); tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "DiffTab"); await f.IdleAsync();
                f.CaptureLoadedRows(); f.Ready = await f.MaterialAsync(); GitCommandLog.Clear(); return f;
            }
            catch { if (main is not null) { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); } git.Dispose(); throw; }
        }
        internal async Task FilterAsync(bool hideMerge = false, bool firstParent = false, bool currentOnly = false)
        {
            object? previous = History.ItemsSource; History.Focus(); Press(main, PhysicalKey.I, RawInputModifiers.Control);
            await Until(() => main.OwnedWindows.OfType<HistoryFilterDialog>().Any()); HistoryFilterDialog filter = main.OwnedWindows.OfType<HistoryFilterDialog>().Single();
            foreach ((string name, bool value) in new[] { ("HideMergeCommits", hideMerge), ("OnlyFirstParent", firstParent), ("CurrentBranchOnly", currentOnly) })
            { CheckBox box = Field<CheckBox>(filter, name); if (box.IsChecked != value) { box.BringIntoView(); Pump(filter); PushProcessTestDriver.Pointer(box); } }
            PushProcessTestDriver.Pointer(filter.GetVisualDescendants().OfType<Button>().Single(button => button.IsDefault));
            await Until(() => !filter.IsVisible && !ReferenceEquals(History.ItemsSource, previous) && !main.IsBusy && main.WindowWorkCompletion.IsCompleted);
            await IdleAsync(); CaptureLoadedRows(); GitCommandLog.Clear();
        }
        internal async Task SelectAsync(string hash) { Click(hash); await IdleAsync(); }
        internal async Task AddAsync(string hash) { Click(hash, RawInputModifiers.Control); await IdleAsync(); }
        internal async Task ShiftAsync(string hash) { Click(hash, RawInputModifiers.Shift); await IdleAsync(); }
        private void Click(string hash, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            GitCommit commit = History.Items.OfType<GitCommit>().Single(item => item.Hash == hash); History.ScrollIntoView(commit); Pump(main);
            Control row = (Control)History.ContainerFromItem(commit)!;
            Point point = row.TranslatePoint(new Point(Math.Min(180, row.Bounds.Width / 2), row.Bounds.Height / 2), main)!.Value;
            Assert.That((main.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(row), Is.True);
            main.MouseMove(point); main.MouseDown(point, MouseButton.Left, modifiers); main.MouseUp(point, MouseButton.Left, modifiers); Pump(main);
        }
        internal async Task NextAsync() { Press(main, PhysicalKey.K, RawInputModifiers.Control); await IdleAsync(); }
        internal async Task IdleAsync() { await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted); Pump(main); Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); }
        internal void AssertComparison(string from, string to)
        {
            IReadOnlyList<GitRevisionDiffGroup> groups = (IReadOnlyList<GitRevisionDiffGroup>)Private(main, "_revisionDiffGroups")!;
            Assert.That(groups.Any(group => group.Kind == GitRevisionDiffGroupKind.Comparison && group.BaseRevision == from && group.Revision == to), Is.True);
            Assert.That(History.IsKeyboardFocusWithin, Is.True); Assert.That(main.OwnedWindows.Where(window => window.IsVisible), Is.Empty);
        }
        internal IReadOnlyDictionary<GitRevisionDiffGroup, GitComparison> ActiveComparisons()
            => (IReadOnlyDictionary<GitRevisionDiffGroup, GitComparison>)Private(main, "_revisionComparisons")!;
        private void CaptureLoadedRows()
        {
            Source = History.ItemsSource;
            _loadedRows = History.Items.Cast<object?>().ToArray();
            _loadedGraphCommits = Graph.Commits;
            _loadedGraphRows = _loadedGraphCommits.ToArray();
        }
        internal void AssertNoReload()
        {
            Assert.That(History.ItemsSource, Is.SameAs(Source));
            Assert.That(History.Items.Count, Is.EqualTo(_loadedRows.Length));
            for (int index = 0; index < _loadedRows.Length; index++)
            { Assert.That(History.Items[index], Is.SameAs(_loadedRows[index]), $"Loaded row {index} must retain its identity and position."); }
            Assert.That(Graph.Commits, Is.SameAs(_loadedGraphCommits));
            Assert.That(Graph.Commits.Count, Is.EqualTo(_loadedGraphRows.Length));
            for (int index = 0; index < _loadedGraphRows.Length; index++)
            { Assert.That(Graph.Commits[index], Is.SameAs(_loadedGraphRows[index]), $"Loaded graph row {index} must retain its identity and position."); }
            // The shared comparison reader may run merge-base when rendering the selected endpoints.
            // The command must preserve the loaded grid and must not reload history/status or fetch.
            Assert.That(GitCommandLog.Snapshot().Select(entry => entry.Command), Does.Not.Contain("git log").And.Not.Contain("git status").And.Not.Contain("git fetch"));
        }
        internal async Task<Material> MaterialAsync()
        {
            GitResult head = await git.GitAsync("rev-parse", "--git-dir"); string gitDir = Path.GetFullPath(Path.Combine(git.Repository.Path, head.StandardOutput.Trim()));
            string Head() => File.ReadAllText(Path.Combine(gitDir, "HEAD")) + "\n" + (File.Exists(Path.Combine(gitDir, "refs/heads/main")) ? File.ReadAllText(Path.Combine(gitDir, "refs/heads/main")) : "<unborn>");
            string Digest(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "<absent>";
            string refs = "packed:" + Digest(Path.Combine(gitDir, "packed-refs")) + "\n" + string.Join('\n', Directory.EnumerateFiles(Path.Combine(gitDir, "refs"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(gitDir, path) + ":" + Digest(path)));
            string files = string.Join('\n', Directory.EnumerateFiles(git.Repository.Path, "*", SearchOption.AllDirectories).Where(path => !Path.GetRelativePath(git.Repository.Path, path).StartsWith(".git/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(git.Repository.Path, path) + ":" + Digest(path)));
            return new(Head(), Digest(Path.Combine(gitDir, "index")), refs, Digest(Path.Combine(gitDir, "config")), files);
        }
        public async ValueTask DisposeAsync() { main.Close(); await Until(() => !main.IsVisible && main.WindowWorkCompletion.IsCompleted); git.Dispose(); DesktopHotkeys.Reload(); }
    }
    private static object? Private(MainWindow main, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
    private static string? FocusedCommit(MainWindow main) => (main.FocusManager?.GetFocusedElement() as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault(item => item.FindAncestorOfType<ListBox>()?.Name == "RevisionHistory")?.DataContext is GitCommit commit ? commit.Hash : null;
    private static T Field<T>(TopLevel root, string name) where T : Control => PushProcessTestDriver.Field<T>(root, name);
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); if (window.IsVisible) { window.KeyReleaseQwerty(key, modifiers); } Pump(window); }
    private static void Pump(TopLevel root) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> ready)
    { Stopwatch timer = Stopwatch.StartNew(); while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Dispatcher.UIThread.RunJobs(); Assert.That(ready(), Is.True); }
}
