using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable, Platform("Linux")]
[SupportedOSPlatform("linux")]
public sealed class GeneralSettingsBehaviorTests
{
    [Test]
    public void UnlimitedHistoryRetainsASentinelAtTheIntegerBoundaryWithoutOverflowingTheNextPage()
    {
        HistoryLoadingPolicy unlimited = new(0);
        int last = unlimited.NextWindow(int.MaxValue - 101, 300);
        Assert.That(last, Is.EqualTo(int.MaxValue - 1)); Assert.That(unlimited.ReadCount(last), Is.EqualTo(int.MaxValue));
        Assert.That(unlimited.CanExpand(last), Is.False); Assert.That(unlimited.HasMore(int.MaxValue, last), Is.False);
        Assert.That(unlimited.NextWindow(last, 300), Is.EqualTo(last));
        HistoryLoadingPolicy finite = new(500);
        Assert.That(finite.ReadCount(finite.NextWindow(300, 300)), Is.EqualTo(501));
        Assert.That(finite.HasMore(501, 500), Is.False, "A sentinel does not make the saved history ceiling expandable.");
    }

    [AvaloniaTest]
    [TestCase(500)]
    [TestCase(0)]
    public async Task TheRealBrowserPagesInBatchesOfThreeHundredAndStopsAtTheSavedCeilingOrTheEnd(int maximum)
    {
        using GitFixture git = await CreateFixture(unborn: true); await ImportHistory(git, 650);
        new PreferencesStore().Save(new() { HistoryLimit = 300, MaxRevisionGraphCommits = maximum });
        string[] expected = (await git.Repository.GetLogAsync(new GitLogQuery(Count: 1000))).Select(commit => commit.Hash).ToArray();
        Assert.That(expected, Has.Length.EqualTo(650)); string material = await RepositoryMaterial(git);
        await using Browser browser = await Browser.Open(git.Repository.Path);
        Assert.That(browser.Hashes, Is.EqualTo(expected.Take(300))); Assert.That(browser.More.IsVisible, Is.True);

        await browser.LoadMore();

        int next = maximum == 0 ? 600 : 500;
        Assert.That(browser.Hashes, Is.EqualTo(expected.Take(next)));
        Assert.That(browser.More.IsVisible, Is.EqualTo(maximum == 0));
        if (maximum == 0)
        {
            await browser.LoadMore(); Assert.That(browser.Hashes, Is.EqualTo(expected));
            Assert.That(browser.More.IsVisible, Is.False, "Unlimited history still detects the real end of the repository.");
        }
        Assert.That(await RepositoryMaterial(git), Is.EqualTo(material));
        Assert.That(new PreferencesStore().Load().HistoryLimit, Is.EqualTo(300));
    }

    [AvaloniaTest]
    [TestCase(500)]
    [TestCase(0)]
    public async Task StartupCommitPreselectionExpandsNewerPagesButCannotEscapeTheSavedCeiling(int maximum)
    {
        using GitFixture git = await CreateFixture(unborn: true); await ImportHistory(git, 650);
        new PreferencesStore().Save(new() { HistoryLimit = 300, MaxRevisionGraphCommits = maximum });
        string target = await git.Repository.ResolveRevisionAsync("HEAD~620");
        string[] expected = (await git.Repository.GetLogAsync(new GitLogQuery(Count: 1000))).Select(commit => commit.Hash).ToArray();
        await using Browser browser = await Browser.Open(new BrowseRequest(git.Repository.Path, SelectedRevision: new(target)));
        if (maximum == 0)
        {
            Assert.That(browser.Hashes, Is.EqualTo(expected)); Assert.That(browser.SelectedHash, Is.EqualTo(target));
        }
        else
        {
            Assert.That(browser.Hashes, Is.EqualTo(expected.Take(500))); Assert.That(browser.Hashes, Does.Not.Contain(target));
            Assert.That(browser.SelectedHash, Is.Not.EqualTo(target));
        }
        Assert.That(browser.More.IsVisible, Is.False);
        Assert.That(browser.Hashes.First(), Is.EqualTo(expected[0]), "A requested selection must not become an ancestor/revision filter.");
    }

    [AvaloniaTest]
    public async Task QuickSearchRereadsItsProviderWhenTheNextSearchStartsWithoutRetimingTheSearchAlreadyRunning()
    {
        int timeout = 2000, reads = 0;
        using RevisionQuickSearch search = new(() => 1, () => 0, (_, _) => true, "Searching: ", () => { reads++; return timeout; });
        Window window = new() { Width = 350, Height = 120, Content = search.Notice }; window.Show();
        try
        {
            Assert.That(search.Type("first"), Is.Zero); Assert.That(reads, Is.EqualTo(1));
            timeout = 100; await DelayWithDispatcher(250);
            Assert.That(search.Notice.IsVisible, Is.True); Assert.That(reads, Is.EqualTo(1), "Changing the source alone does not run another search.");
            Stopwatch elapsed = Stopwatch.StartNew(); search.Type(" next");
            Assert.That(reads, Is.EqualTo(2)); await Until(() => !search.Notice.IsVisible);
            Assert.That(elapsed.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(70)));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            Assert.That(search.ActiveText, Is.Empty);
            search.Next(down: true); Assert.That(reads, Is.EqualTo(3));
            Assert.That(search.ActiveText, Is.EqualTo("first next"), "Timeout preserves the term used by next-match navigation.");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task ATemporarilyUnreadableTimeoutUsesTheLastAcceptedIntervalRatherThanLosingQuickSearch()
    {
        bool unreadable = false; int reads = 0;
        using RevisionQuickSearch search = new(() => 1, () => 0, (_, _) => true, "Searching: ", () =>
        { reads++; return unreadable ? throw new IOException("fixture-preferences-locked") : 100; });
        Window window = new() { Width = 350, Height = 120, Content = search.Notice }; window.Show();
        try
        {
            search.Type("a"); await Until(() => !search.Notice.IsVisible); unreadable = true;
            search.Type("b"); Assert.That(search.Notice.IsVisible, Is.True); await Until(() => !search.Notice.IsVisible);
            Assert.That(reads, Is.EqualTo(2)); Assert.That(search.ActiveText, Is.Empty);
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task TheOpenBrowserUsesAPublishedTimeoutOnItsVeryNextTypedSearchWithoutReloadingHistory()
    {
        using GitFixture git = await CreateFixture(); PreferencesStore store = new(); store.Save(new() { RevisionGridQuickSearchTimeout = 4000 });
        await using Browser browser = await Browser.Open(git.Repository.Path);
        object? historySource = browser.History.ItemsSource; browser.ClickFirstCommit();
        browser.Main.KeyTextInput("initial"); Pump(browser.Main); Border notice = Field<Border>(browser.Main, "RevisionQuickSearchNotice");
        Assert.That(notice.IsVisible, Is.True); store.Save(store.Load() with { RevisionGridQuickSearchTimeout = 100 });
        GitCommandLog.Clear(); browser.Main.KeyTextInput(" more"); Pump(browser.Main);
        await Until(() => !notice.IsVisible); await browser.Idle();
        Assert.That(browser.History.ItemsSource, Is.SameAs(historySource)); Assert.That(browser.History.IsKeyboardFocusWithin, Is.True);
        Assert.That(GitCommandLog.Snapshot().Where(entry => entry.Command is "git log" or "git reflog"), Is.Empty);
    }

    [Test]
    public void LastVisitedIsPersistedEvenWhenAnchorsExhaustCapacityAndPresentationSortingHidesItFromTheRecentList()
    {
        string root = Path.Combine(Path.GetTempPath(), "gitextensions-last-visit-" + Guid.NewGuid().ToString("N"));
        try
        {
            RepositoryHistoryState state = AnchoredHistory(root).Visit(Path.Combine(root, "visited outside retained MRU"));
            Assert.That(state.Recent, Has.Count.EqualTo(2)); Assert.That(state.Recent.Select(entry => entry.Path), Does.Not.Contain(state.LastVisitedRepositoryPath));
            RepositoryHistorySelection presentation = RepositoryHistoryPresentation.Select(state);
            Assert.That(presentation.Top.Concat(presentation.Recent).Select(row => row.Path), Does.Not.Contain(state.LastVisitedRepositoryPath));
            RepositoryHistoryStore store = new(root); store.Save(state); RepositoryHistoryState loaded = store.Load();
            Assert.That(loaded.StartupRepositoryPath, Is.EqualTo(state.LastVisitedRepositoryPath));
            Assert.That(loaded.Recent.Select(entry => entry.Path), Is.EqualTo(state.Recent.Select(entry => entry.Path)));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Test]
    public void MissingLegacyLastVisitFallsBackOnceButAnExplicitNullDoesNotSubstituteAnotherRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), "gitextensions-legacy-last-visit-" + Guid.NewGuid().ToString("N"));
        try
        {
            RepositoryHistoryStore store = new(root); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            string path = Path.Combine(root, "legacy repository");
            File.WriteAllText(store.FilePath, JsonSerializer.Serialize(new { Recent = new[] { new RepositoryHistoryEntry(path) } }));
            RepositoryHistoryState legacy = store.Load(); Assert.That(legacy.StartupRepositoryPath, Is.EqualTo(path));
            store.Save(legacy); using JsonDocument saved = JsonDocument.Parse(File.ReadAllBytes(store.FilePath));
            Assert.That(saved.RootElement.GetProperty(nameof(RepositoryHistoryState.LastVisitedRepositoryPath)).GetString(), Is.EqualTo(path));
            File.WriteAllText(store.FilePath, JsonSerializer.Serialize(new { Recent = new[] { new RepositoryHistoryEntry(path) }, LastVisitedRepositoryPath = (string?)null }));
            Assert.That(store.Load().StartupRepositoryPath, Is.Null);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task StartupReopensTheActualLastVisitOnlyWhenEnabledEvenWhenItIsNotInTheRetainedRecentEntries(bool enabled)
    {
        using GitFixture git = await CreateFixture();
        new PreferencesStore().Save(new() { StartWithRecentWorkingDir = enabled });
        RepositoryHistoryStore history = new(); history.Save(AnchoredHistory(git.Output("history anchors")).Visit(git.Repository.Path));
        Assert.That(history.Load().Recent.Select(entry => entry.Path), Does.Not.Contain(git.Repository.Path));
        await using Browser browser = await Browser.Open(new BrowseRequest(), expectRepository: enabled);
        Assert.That(browser.History.Items.OfType<GitCommit>().Any(), Is.EqualTo(enabled));
        if (enabled) { Assert.That(Field<TextBox>(browser.Main, "RepositoryPath").Text, Is.EqualTo(git.Repository.Path)); }
        else { Assert.That(browser.Main.Title, Is.EqualTo("Git Extensions")); }
        Assert.That(history.Load().LastVisitedRepositoryPath, Is.EqualTo(git.Repository.Path));
    }

    [AvaloniaTest]
    [TestCase("missing")]
    [TestCase("nested-directory")]
    [TestCase("relative")]
    [TestCase("explicit-null")]
    public async Task AnInvalidRecordedLastVisitLeavesTheDashboardOpenAndDoesNotChooseARecentOrParentRepository(string kind)
    {
        using GitFixture git = await CreateFixture(); new PreferencesStore().Save(new() { StartWithRecentWorkingDir = true });
        string nested = git.PathInRepository("old nested repository"); Directory.CreateDirectory(nested);
        string? last = kind switch { "missing" => git.Output("gone repository"), "nested-directory" => nested, "relative" => "relative repo", _ => null };
        RepositoryHistoryStore history = new(); history.Save(new() { Recent = [new(git.Repository.Path)], LastVisitedRepositoryPath = last });
        byte[] before = File.ReadAllBytes(history.FilePath); string material = await RepositoryMaterial(git);
        await using Browser browser = await Browser.Open(new BrowseRequest(), expectRepository: false);
        Assert.That(browser.Hashes, Is.Empty); Assert.That(browser.Main.Title, Is.EqualTo("Git Extensions"));
        Assert.That(browser.Main.GetVisualDescendants().OfType<DashboardControl>().Single().IsVisible, Is.True);
        Assert.That(File.ReadAllBytes(history.FilePath), Is.EqualTo(before)); Assert.That(await RepositoryMaterial(git), Is.EqualTo(material));
    }

    [AvaloniaTest]
    public async Task AnExplicitRepositoryStillOpensWhenAutomaticReopeningIsDisabled()
    {
        using GitFixture git = await CreateFixture(); new PreferencesStore().Save(new() { StartWithRecentWorkingDir = false });
        new RepositoryHistoryStore().Save(new() { LastVisitedRepositoryPath = git.Output("unrelated missing repository") });
        await using Browser browser = await Browser.Open(git.Repository.Path);
        Assert.That(browser.Hashes, Has.Length.EqualTo(1));
        Assert.That(Field<TextBox>(browser.Main, "RepositoryPath").Text, Is.EqualTo(git.Repository.Path));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task CheckoutInitializationUsesTheSavedDirtyCheckPolicyWithoutRunningGitStatusWhenDisabled(bool checkChanges)
    {
        using GitFixture git = await CreateFixture(); await File.WriteAllTextAsync(git.File, "Uncommitted local content Ω\n");
        new PreferencesStore().Save(new() { CheckForUncommittedChangesInCheckoutBranch = checkChanges });
        string arguments = git.Output("checkout-arguments.jsonl"), helper = git.Output("record-checkout-git.py");
        File.WriteAllText(helper, "#!/usr/bin/python3\nimport json, os, sys\nwith open(" + JsonSerializer.Serialize(arguments)
            + ", 'a', encoding='utf-8') as log:\n    log.write(json.dumps(sys.argv[1:]) + '\\n')\nos.execv('/usr/bin/git', ['/usr/bin/git'] + sys.argv[1:])\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string material = await RepositoryMaterial(git);
        CheckoutBranchDialog dialog = new(new GitRepository(git.Repository.Path, new GitRunner(helper))); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            string[][] calls = File.ReadAllLines(arguments).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
            Assert.That(calls, Is.Not.Empty);
            Assert.That(calls.Any(call => call.Contains("status", StringComparer.Ordinal)), Is.EqualTo(checkChanges));
            Assert.That(Field<TextBlock>(dialog, "CheckoutCurrentBranch").Text, Does.Contain("main"));
            Assert.That(Field<TextBlock>(dialog, "CheckoutCurrentBranch").Text?.Contains("Local changes", StringComparison.Ordinal), Is.EqualTo(checkChanges));
            Assert.That(await RepositoryMaterial(git), Is.EqualTo(material));
        }
        finally { dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsBusy); }
    }

    [AvaloniaTest]
    [TestCase(true, null)]
    [TestCase(false, null)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task CheckoutReadsTheCanonicalAutoStashChoiceAndOnlyFallsBackToAnExplicitLegacyValue(bool legacy, bool? canonical)
    {
        using GitFixture git = await CreateFixture(); new ReferencePreferencesStore().Save(new(IncludeUntracked: legacy));
        PullPreferencesStore store = new();
        if (canonical.HasValue) { store.Save(new(IncludeUntrackedFilesInAutoStash: canonical.Value)); }
        else { File.WriteAllText(store.FilePath, "{\"DefaultAction\":\"Merge\",\"AutoStash\":true}\n"); }
        byte[] before = File.ReadAllBytes(store.FilePath); bool expected = canonical ?? legacy;
        CheckoutBranchDialog dialog = new(git.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(store.Load().IncludeUntrackedFilesInAutoStash, Is.EqualTo(expected));
            Assert.That(Field<CheckBox>(dialog, "CheckoutStashUntracked").IsChecked, Is.EqualTo(expected));
            Assert.That(File.ReadAllBytes(store.FilePath), Is.EqualTo(before), "Reading the legacy choice must not silently rewrite the profile.");
        }
        finally { dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsBusy); }
    }

    [AvaloniaTest]
    public async Task AnUntouchedCheckoutProfileUsesTheWindowsFalseDefaultInsteadOfTheOldReferenceRecordDefault()
    {
        using GitFixture git = await CreateFixture(); PullPreferencesStore store = new();
        Assert.That(store.Load().IncludeUntrackedFilesInAutoStash, Is.False);
        CheckoutBranchDialog dialog = new(git.Repository); dialog.Show();
        try
        {
            await dialog.InitializeAsync(); Assert.That(dialog.LastError, Is.Null, dialog.LastException?.ToString());
            Assert.That(Field<CheckBox>(dialog, "CheckoutStashUntracked").IsChecked, Is.False);
            Assert.That(File.Exists(store.FilePath), Is.False);
        }
        finally { dialog.Close(); await Until(() => !dialog.IsVisible && !dialog.IsBusy); }
    }

    private static RepositoryHistoryState AnchoredHistory(string root) => new()
    {
        HistorySize = 1, MaxTopRepositories = 1, SortRecent = true, SortTopRepos = true, HideTopRepositoriesFromRecentList = true,
        Recent = [new(Path.Combine(root, "aaa top anchor")) { Anchor = RepositoryHistoryAnchor.Top },
            new(Path.Combine(root, "zzz recent anchor")) { Anchor = RepositoryHistoryAnchor.Recent }]
    };

    private static async Task<GitFixture> CreateFixture(bool unborn = false)
    {
        GitFixture fixture = await GitFixture.CreateAsync(unborn);
        fixture.SetEnvironment("GIT_OPTIONAL_LOCKS", "0"); return fixture;
    }

    private static async Task ImportHistory(GitFixture git, int count)
    {
        StringBuilder input = new();
        for (int index = 0; index < count; index++)
        {
            string message = "History commit " + index.ToString("D4", CultureInfo.InvariantCulture) + "\n";
            input.Append("commit refs/heads/main\ncommitter General Settings Tester <general@example.com> ")
                .Append(1700000000L + index).Append(" +0000\ndata ").Append(Encoding.UTF8.GetByteCount(message)).Append('\n').Append(message);
            if (index == 0) { input.Append("M 100644 inline seed.txt\ndata 5\nseed\n"); }
            input.Append('\n');
        }
        input.Append("done\n");
        GitResult imported = await new GitRunner("/usr/bin/git").RunAsync(git.Repository.Path, ["fast-import", "--quiet"], standardInput: input.ToString());
        Assert.That(imported.Success, Is.True, imported.StandardError); await git.GitAsync("reset", "--hard", "HEAD");
    }

    private static async Task<string> RepositoryMaterial(GitFixture git) => JsonSerializer.Serialize(new
    {
        Head = (await git.GitAsync("rev-parse", "HEAD")).StandardOutput,
        Refs = (await git.GitAsync("show-ref")).StandardOutput,
        Index = Convert.ToHexString(await File.ReadAllBytesAsync(git.PathInRepository(".git/index"))),
        Files = Directory.EnumerateFiles(git.Repository.Path, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(git.PathInRepository(".git") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => new { Path = Path.GetRelativePath(git.Repository.Path, path), Bytes = Convert.ToHexString(File.ReadAllBytes(path)) }).ToArray()
    });

    private sealed class Browser : IAsyncDisposable
    {
        internal MainWindow Main { get; }
        internal ListBox History => Field<ListBox>(Main, "RevisionHistory");
        internal Button More => Field<Button>(Main, "BrowseLoadMore");
        internal string[] Hashes => History.Items.OfType<GitCommit>().Select(commit => commit.Hash).ToArray();
        internal string? SelectedHash => (History.SelectedItem as GitCommit)?.Hash;
        private Browser(MainWindow main) => Main = main;
        internal static Task<Browser> Open(string path) => Open(new BrowseRequest(path));
        internal static async Task<Browser> Open(BrowseRequest request, bool expectRepository = true)
        {
            if (request.RepositoryPath is null) { Assert.That(GitExtensions.Linux.Desktop.Program.RepositoryPath, Is.Null, "This startup fixture must not inherit an explicit process argument."); }
            MainWindow main = new(request) { Width = 1440, Height = 960 }; main.Show(); Browser browser = new(main);
            try
            {
                await Until(() => !main.IsBusy && main.WindowWorkCompletion.IsCompleted
                    && (!expectRepository || browser.Hashes.Length > 0 || main.LastException is not null));
                await main.WindowWorkCompletion;
                if (main.GetVisualDescendants().OfType<DashboardControl>().SingleOrDefault() is { } dashboard)
                { await dashboard.MetadataLoading.WaitAsync(TimeSpan.FromSeconds(20)); }
                Pump(main);
                Assert.That(main.LastException, Is.Null, main.LastException?.ToString()); return browser;
            }
            catch { await browser.DisposeAsync(); throw; }
        }
        internal async Task LoadMore()
        {
            if (More.IsEffectivelyVisible) { PushProcessTestDriver.Pointer(More); }
            else
            {
                CompactBrowserToolbar toolbar = Field<CompactBrowserToolbar>(Main, "RepositoryToolbar"); PushProcessTestDriver.Pointer(toolbar.Overflow); Pump(Main);
                MenuItem action = toolbar.OverflowFlyout.Items.OfType<MenuItem>().Single(item => item.Header as string == "Load more");
                PushProcessTestDriver.Pointer(action);
            }
            await Idle();
        }
        internal void ClickFirstCommit()
        {
            GitCommit first = History.Items.OfType<GitCommit>().First(); History.ScrollIntoView(first); Pump(Main);
            PushProcessTestDriver.Pointer((Control)History.ContainerFromItem(first)!); Pump(Main);
        }
        internal async Task Idle() { await Until(() => !Main.IsBusy && Main.WindowWorkCompletion.IsCompleted); await Main.WindowWorkCompletion; Pump(Main); }
        public async ValueTask DisposeAsync()
        {
            Main.Close(); await Until(() => !Main.IsVisible && Main.WindowWorkCompletion.IsCompleted); await Main.WindowWorkCompletion;
        }
    }

    private static T Field<T>(TopLevel window, string name) where T : Control => PushProcessTestDriver.Field<T>(window, name);
    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(20)) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.That(condition(), Is.True, "Timed out while waiting for the actual browser/input operation.");
    }
    private static async Task DelayWithDispatcher(int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }
}
