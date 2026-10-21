using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DetailedGraphPublicationTests
{
    [AvaloniaTest]
    public async Task SavedPublicationTargetsOnlyItsCapturedProfileAndDetachmentRetiresTheSubscription()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        string configA = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!;
        CommitGraph a = Graph(); DetailedGraphSettings.Bind(a);
        f.Git.SetEnvironment("XDG_CONFIG_HOME", f.Git.Output("other-profile")); PreferencesStore other = new(); other.Save(new());
        CommitGraph b = Graph(); DetailedGraphSettings.Bind(b);
        f.Git.SetEnvironment("XDG_CONFIG_HOME", configA);
        Window one = new() { Content = a, Width = 220, Height = 150 }, two = new() { Content = b, Width = 220, Height = 150 };
        try
        {
            one.Show(); two.Show(); Pump(one); Pump(two); State beforeA = State.Capture(a), beforeB = State.Capture(b);
            byte[] otherBefore = File.ReadAllBytes(other.FilePath);
            DesktopPreferences saved = f.Desktop.Load() with { MergeGraphLanesHavingCommonParent = false, RenderGraphWithDiagonals = false };
            f.Desktop.Save(saved); byte[] written = File.ReadAllBytes(f.Desktop.FilePath);
            DetailedGraphSettings.PublishSaved(f.Desktop.FilePath, saved); Pump(one); Pump(two);
            Assert.That(a.Options, Is.EqualTo(DetailedGraphSettings.From(saved))); Assert.That(b.Options, Is.EqualTo(new RevisionGraphOptions()));
            beforeA.AssertUnchanged(a); beforeB.AssertUnchanged(b);
            Assert.That(File.ReadAllBytes(f.Desktop.FilePath), Is.EqualTo(written)); Assert.That(File.ReadAllBytes(other.FilePath), Is.EqualTo(otherBefore));
            one.Content = null; RevisionGraphOptions detached = a.Options;
            DesktopPreferences newer = saved with { StraightenGraphDiagonals = false }; f.Desktop.Save(newer);
            DetailedGraphSettings.PublishSaved(f.Desktop.FilePath, newer); Pump(one);
            Assert.That(a.Options, Is.SameAs(detached), "A detached graph must not receive global publications.");
            one.Content = a; Pump(one); Assert.That(a.Options, Is.EqualTo(DetailedGraphSettings.From(newer)), "Reattachment reads the profile saved while this graph was detached.");
            beforeA.AssertUnchanged(a);
        }
        finally { one.Close(); two.Close(); }
    }

    [AvaloniaTest]
    public async Task AQueuedPublicationCannotOutliveItsAttachmentAndOverwriteTheNewlyReadProfile()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); CommitGraph graph = Graph(); DetailedGraphSettings.Bind(graph);
        Window host = new() { Content = graph, Width = 240, Height = 160 };
        try
        {
            host.Show(); Pump(host); State before = State.Capture(graph);
            DesktopPreferences stale = f.Desktop.Load() with { RenderGraphWithDiagonals = false }; f.Desktop.Save(stale);
            // Join while the UI thread stays here: the publication is definitely queued, never pumped yet.
            PublishFromBackground(f.Desktop.FilePath, stale);
            host.Content = null;
            DesktopPreferences recent = stale with { RenderGraphWithDiagonals = true, StraightenGraphDiagonals = false }; f.Desktop.Save(recent);
            host.Content = graph;
            Assert.That(graph.Options, Is.EqualTo(DetailedGraphSettings.From(recent)), "The new attachment must first read the latest actual file.");
            Pump(host);
            Assert.That(graph.Options, Is.EqualTo(DetailedGraphSettings.From(recent)), "A queued callback from the previous attachment must be retired.");
            before.AssertUnchanged(graph);
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public async Task BackgroundPublicationIsMarshalledToUiAndPreservesTheExistingGraphState()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(); CommitGraph graph = Graph(); DetailedGraphSettings.Bind(graph);
        Window host = new() { Content = graph, Width = 240, Height = 160 };
        try
        {
            host.Show(); Pump(host); State before = State.Capture(graph); RevisionGraphOptions old = graph.Options;
            DesktopPreferences saved = f.Desktop.Load() with { StraightenGraphDiagonals = false }; f.Desktop.Save(saved);
            PublishFromBackground(f.Desktop.FilePath, saved);
            Assert.That(graph.Options, Is.SameAs(old), "A worker thread must not mutate a UI control synchronously.");
            Pump(host); Assert.That(graph.Options, Is.EqualTo(DetailedGraphSettings.From(saved))); before.AssertUnchanged(graph);
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public async Task BindingAnUnreadableProfileDoesNotRepairItOrResetAnExistingGraph()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        File.WriteAllText(f.Desktop.FilePath, "{malformed-graph-profile"); byte[] broken = File.ReadAllBytes(f.Desktop.FilePath);
        CommitGraph graph = Graph(); graph.Options = new(false, false, false); State before = State.Capture(graph);
        DetailedGraphSettings.Bind(graph); Window host = new() { Content = graph, Width = 240, Height = 160 };
        try
        {
            host.Show(); Pump(host); Assert.That(graph.Options, Is.EqualTo(new RevisionGraphOptions(false, false, false)));
            Assert.That(File.ReadAllBytes(f.Desktop.FilePath), Is.EqualTo(broken)); before.AssertUnchanged(graph);
            DesktopPreferences repairedByUser = new() { RenderGraphWithDiagonals = false }; f.Desktop.Save(repairedByUser);
            DetailedGraphSettings.PublishSaved(f.Desktop.FilePath, repairedByUser); Pump(host);
            Assert.That(graph.Options, Is.EqualTo(DetailedGraphSettings.From(repairedByUser))); before.AssertUnchanged(graph);
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public async Task BrowseFileHistoryAndExportRevisionListAllReceiveTheSameSavedOptionsWithoutReloadingTheirData()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        MainWindow main = new(); FileHistoryDialog history = new(f.Repository!, "file.txt");
        ExportRevisionList exports = new("DetailedExportRows", false); Window export = new() { Content = exports, Width = 780, Height = 420 };
        try
        {
            main.Show(); await main.OpenRepositoryAsync(f.Repository!.Path);
            await PushProcessTestDriver.UntilAsync(main, () => !main.IsBusy && main.WindowWorkCompletion.IsCompleted && main.RepositoryBranchLoading.IsCompleted);
            Assert.That(main.LastException, Is.Null);
            history.Show(); await history.InitializeAsync(); await history.InformationCompletion;
            Assert.That(history.LastError, Is.Null); Assert.That(history.History, Is.Not.Empty);
            IReadOnlyList<GitCommit> commits = await f.Repository.GetLogAsync(); IReadOnlyList<GitReference> refs = await f.Repository.GetReferencesAsync();
            exports.SetData(commits, refs, (await f.Repository.GetStatusAsync()).Head); export.Show();
            Pump(main); Pump(history); Pump(export);
            CommitGraph[] graphs = [main.GetVisualDescendants().OfType<CommitGraph>().Single(), history.GetVisualDescendants().OfType<CommitGraph>().Single(), exports.GetVisualDescendants().OfType<CommitGraph>().Single()];
            Assert.That(graphs.Select(graph => TopLevel.GetTopLevel(graph)), Is.EqualTo(new TopLevel[] { main, history, export }), "The real three host controls must be attached to their own host before publication.");
            ListBox[] dataRows = [main.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "RevisionHistory"),
                history.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "FileHistoryRevisions"), exports.Rows];
            State[] states = graphs.Select((graph, index) => State.Capture(graph, dataRows[index])).ToArray();
            long lastCommandIdBeforePublication = GitCommandLog.Snapshot().LastOrDefault()?.Id ?? 0;
            DesktopPreferences saved = f.Desktop.Load() with { MergeGraphLanesHavingCommonParent = false, RenderGraphWithDiagonals = false, StraightenGraphDiagonals = false };
            f.Desktop.Save(saved); DetailedGraphSettings.PublishSaved(f.Desktop.FilePath, saved); Pump(main); Pump(history); Pump(export);
            for (int i = 0; i < graphs.Length; i++) { Assert.That(graphs[i].Options, Is.EqualTo(new RevisionGraphOptions(false, false, false))); states[i].AssertUnchanged(graphs[i]); }
            Assert.That(GitCommandLog.Snapshot().Where(entry => entry.Id > lastCommandIdBeforePublication).Select(entry => entry.Command), Does.Not.Contain("git log"),
                "Publishing graph presentation options must not reread history in any of the three hosts.");
            ListBox rows = exports.Rows; Assert.That(rows.ItemsSource!.OfType<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(commits.Select(commit => commit.Hash)));
        }
        finally
        {
            export.Close(); history.Close(); main.Close();
            await DetailedSettingsUiHost.Until(() => !export.IsVisible && !history.IsVisible && !main.IsVisible);
        }
    }

    [AvaloniaTest]
    public async Task LegacyProfilesWithoutGraphKeysUseTheWindowsDefaultsWithoutWritingAMigration()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync();
        File.WriteAllText(f.Desktop.FilePath, "{\"Language\":\"English\",\"Theme\":\"Light\"}"); byte[] before = File.ReadAllBytes(f.Desktop.FilePath);
        CommitGraph graph = Graph(); DetailedGraphSettings.Bind(graph); Window host = new() { Content = graph, Width = 240, Height = 160 };
        try { host.Show(); Pump(host); Assert.That(graph.Options, Is.EqualTo(new RevisionGraphOptions(true, true, true))); Assert.That(File.ReadAllBytes(f.Desktop.FilePath), Is.EqualTo(before)); }
        finally { host.Close(); }
    }

    private static CommitGraph Graph() => new()
    {
        Commits = [Commit("M", "A", "B"), Commit("A", "R"), Commit("B", "R"), Commit("R")],
        References = [new("refs/heads/main", "M", null, true)], CurrentHead = "M", SelectedHashes = ["A", "B"], VerticalOffset = 7.5
    };
    private static GitCommit Commit(string hash, params string[] parents) => new(hash, parents, "Author", "author@example.com", DateTimeOffset.UnixEpoch, hash, "");
    private static void Pump(TopLevel root) => DetailedSettingsUiHost.Pump(root);
    private static void PublishFromBackground(string profile, DesktopPreferences preferences)
    {
        Exception? failure = null; Thread worker = new(() => { try { DetailedGraphSettings.PublishSaved(profile, preferences); } catch (Exception error) { failure = error; } });
        worker.Start(); Assert.That(worker.Join(TimeSpan.FromSeconds(5)), Is.True); Assert.That(failure, Is.Null);
    }
    private sealed record State(IReadOnlyList<GitCommit> Commits, IReadOnlyList<GitReference> References, IReadOnlyCollection<string> Selection,
        string? Head, double Offset, CommitGraphLayout Layout, IncrementalCommitGraphLayout? Incremental,
        GitCommit[] Input, GitCommit[] RealOrder, object[] OrderedItems, string[] SelectedHashes, bool? Completed,
        ListBox? Rows, object? RowSource, object[] RowItems, object? SelectedItem, object[] SelectedItems, int? Anchor)
    {
        internal static State Capture(CommitGraph graph) => Capture(graph, null);
        internal static State Capture(CommitGraph graph, ListBox? rows)
        {
            CommitGraphLayout layout = (CommitGraphLayout)typeof(CommitGraph).GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph)!;
            IncrementalCommitGraphLayout? incremental = IncrementalLayout(graph);
            // Snapshot the active engine's order; its incremental lists are mutable across Reconfigure.
            GitCommit[] realOrder = incremental is null ? layout.Nodes.Select(node => node.Commit).ToArray() : incremental.OrderedCommits.ToArray();
            object[] orderedItems = incremental is null ? layout.Nodes.Select(node => (object)node.Commit).ToArray() : incremental.OrderedItems.ToArray();
            return new(graph.Commits, graph.References, graph.SelectedHashes, graph.CurrentHead, graph.VerticalOffset, layout, incremental,
                graph.Commits.ToArray(), realOrder, orderedItems, graph.SelectedHashes.ToArray(), incremental?.IsComplete,
                rows, rows?.ItemsSource, rows?.Items.Cast<object>().ToArray() ?? [], rows?.SelectedItem,
                rows?.SelectedItems?.Cast<object>().ToArray() ?? [], rows?.Selection.AnchorIndex);
        }
        internal void AssertUnchanged(CommitGraph graph)
        {
            Assert.That(graph.Commits, Is.SameAs(Commits)); Assert.That(graph.References, Is.SameAs(References)); Assert.That(graph.SelectedHashes, Is.SameAs(Selection));
            Assert.That(graph.CurrentHead, Is.EqualTo(Head)); Assert.That(graph.VerticalOffset, Is.EqualTo(Offset));
            Assert.That(graph.SelectedHashes, Is.EqualTo(SelectedHashes));
            AssertSameItems(graph.Commits.Cast<object>(), Input.Cast<object>().ToArray());
            Assert.That(IncrementalLayout(graph), Is.SameAs(Incremental), "Publication must retain the active engine kind and incremental data generation.");
            Assert.That(graph.OrderedCommits.Select(commit => commit.Hash), Is.EqualTo(RealOrder.Select(commit => commit.Hash)));
            AssertSameItems(graph.OrderedCommits.Cast<object>(), RealOrder.Cast<object>().ToArray());
            AssertSameItems(graph.OrderedItems, OrderedItems);
            if (Incremental is not null)
            {
                Assert.That(Incremental.Options, Is.EqualTo(graph.Options), "The active native incremental graph must receive the saved options.");
                Assert.That(Incremental.IsComplete, Is.EqualTo(Completed));
                Assert.That(Incremental.Count, Is.EqualTo(Input.Length)); Assert.That(Incremental.RowCount, Is.EqualTo(OrderedItems.Length));
                AssertSameItems(Incremental.OrderedItems, OrderedItems);
            }
            else
            {
                // Standalone and legacy hosts still derive the baseline from their monolithic layout nodes.
                Assert.That(graph.OrderedCommits.Select(commit => commit.Hash), Is.EqualTo(Layout.Nodes.Select(node => node.Commit.Hash)));
            }
            if (Rows is not null)
            {
                Assert.That(Rows.ItemsSource, Is.SameAs(RowSource)); AssertSameItems(Rows.Items.Cast<object>(), RowItems);
                Assert.That(Rows.SelectedItem, Is.SameAs(SelectedItem));
                AssertSameItems(Rows.SelectedItems?.Cast<object>() ?? [], SelectedItems);
                Assert.That(Rows.Selection.AnchorIndex, Is.EqualTo(Anchor));
            }
        }
        private static IncrementalCommitGraphLayout? IncrementalLayout(CommitGraph graph)
            => (IncrementalCommitGraphLayout?)typeof(CommitGraph).GetField("_incrementalLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph);
        private static void AssertSameItems(IEnumerable<object> actual, object[] expected)
        {
            object[] items = actual.ToArray(); Assert.That(items.Length, Is.EqualTo(expected.Length));
            for (int index = 0; index < items.Length; index++) { Assert.That(items[index], Is.SameAs(expected[index]), "Published item " + index); }
        }
    }
}
