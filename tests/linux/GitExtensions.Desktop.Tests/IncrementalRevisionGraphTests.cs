using System.Collections.Specialized;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using GitFixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class IncrementalRevisionGraphTests
{
    [Test]
    [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
    public void NativeModelAppendsWithoutCompletingOrCachingAllRowsAndReusesWindowsOrderAndGeometry(bool merge, bool firstParent)
    {
        // Same Windows graph fixture as RevisionGraphTests.SegmentsAreStraightened (newest first here).
        GitCommit[] topology = Parse("8:7,2 7:5,6 6:5 5:4 4:1,3 3:1 2:1 1");
        GitCommit[] tail = Chain(2000, "tail/");
        GitCommit[] commits = [.. topology, .. tail];
        RevisionGraphOptions options = new(merge, true, false);
        IncrementalCommitGraphLayout model = new(firstParent, options, topology[0].Hash);
        object native = Native(model);
        foreach (GitCommit[] batch in commits.Chunk(113))
        {
            model.Append(batch);
            Assert.That(model.IsComplete, Is.False); Assert.That(Native(model), Is.SameAs(native));
            Assert.That(model.CachedCount, Is.Zero, "Admitting/reordering text rows must not materialize graph lanes.");
        }
        Assert.That(model.Count, Is.GreaterThan(500));
        model.Complete(); Assert.That(model.CachedCount, Is.Zero, "EOF is not a request to cache the entire graph.");
        model.PrepareViewport(0, 18);
        Assert.That(model.CachedCount, Is.GreaterThan(18).And.LessThan(200));
        CommitGraphLayout expected = CommitGraphLayout.Build(commits, firstParent, options);
        Assert.That(model.OrderedCommits, Is.EqualTo(expected.Nodes.Select(node => node.Commit)));
        for (int row = 0; row <= 18; row++)
        {
            GraphNode actual = model.GetPreparedNode(row)!;
            Assert.That(actual.Commit, Is.SameAs(expected.Nodes[row].Commit)); Assert.That(actual.Lane, Is.EqualTo(expected.Nodes[row].Lane));
            Assert.That(actual.Color, Is.EqualTo(expected.Nodes[row].Color));
            Assert.That(JsonSerializer.Serialize(model.RenderSegmentsForRow(row, 12, 24, true, null)),
                Is.EqualTo(JsonSerializer.Serialize(expected.RenderSegmentsForRow(row, 12, 24))));
        }
        Assert.That(Native(model), Is.SameAs(native));
    }

    [Test]
    public void AShortLiveTailDoesNotSpinPretendEofOrDrawATruncationMarkerBeforeCompletion()
    {
        GitCommit[] commits = Chain(100, "live/");
        IncrementalCommitGraphLayout model = new(false, new(), commits[0].Hash);
        model.Append(commits[..5]); model.PrepareViewport(0, 4);
        Assert.That(model.IsComplete, Is.False); Assert.That(model.VisibleStrokes, Is.Empty);
        model.Append(commits[5..]); model.PrepareViewport(0, 12);
        Assert.That(model.GetPreparedNode(0), Is.Not.Null); Assert.That(model.IsComplete, Is.False);
        model.Complete(); model.Complete(); model.PrepareViewport(90, 99);
        Assert.That(model.GetPreparedNode(99), Is.Not.Null); Assert.That(model.Count, Is.EqualTo(100));
        Assert.Throws<InvalidOperationException>(() => model.Append([commits[0]]));
    }

    [AvaloniaTest]
    public async Task ActualGitHistoryBeyondFiveHundredKeepsTheFirstPublishedSourceObjectsFocusAndViewportDuringAppend()
    {
        using GitFixture f = await GitFixture.CreateAsync(unborn: true); await Import(f, 650);
        IReadOnlyList<GitCommit> commits = await f.Repository.GetRevisionGraphAsync(new(Count: 1000, All: true));
        Assert.That(commits, Has.Count.EqualTo(650));
        ExportRevisionList list = new("ChooseRevision", false, view: new() { ShowAvatar = false });
        Window host = Host(list); list.BeginIncrementalData(await f.Repository.GetReferencesAsync(), commits[0].Hash);
        object source = list.Rows.ItemsSource!; List<NotifyCollectionChangedAction> changes = [];
        ((INotifyCollectionChanged)source).CollectionChanged += (_, change) => changes.Add(change.Action);
        list.AppendCommits(commits.Take(100).ToArray());
        try
        {
            host.Show(); Pump(host);
            CommitGraph graph = Graph(list); object native = Native(Model(graph)); IReadOnlyList<GitCommit> graphInput = graph.Commits;
            Pointer(host, Subject(list.Rows, commits[3]));
            Control focused = (Control)host.FocusManager!.GetFocusedElement()!; Control row = Row(list.Rows, commits[3]);
            ScrollViewer scroll = Scroll(list.Rows); Vector beforeOffset = scroll.Offset;
            for (int start = 100; start < commits.Count; start += 113)
            {
                list.AppendCommits(commits.Skip(start).Take(113).ToArray()); Pump(host);
                Assert.That(list.Rows.ItemsSource, Is.SameAs(source)); Assert.That(list.Rows.SelectedItem, Is.SameAs(commits[3]));
                Assert.That(host.FocusManager?.GetFocusedElement(), Is.SameAs(focused)); Assert.That(Row(list.Rows, commits[3]), Is.SameAs(row));
                Assert.That(scroll.Offset, Is.EqualTo(beforeOffset)); Assert.That(Model(graph).IsComplete, Is.False);
                Assert.That(Native(Model(graph)), Is.SameAs(native)); Assert.That(graph.Commits, Is.SameAs(graphInput));
            }
            list.CompleteCommits(); Pump(host);
            Assert.That(list.Rows.ItemCount, Is.EqualTo(650)); Assert.That(list.Rows.Items.Cast<GitCommit>(), Is.EqualTo(commits));
            Assert.That(changes, Does.Not.Contain(NotifyCollectionChangedAction.Reset)); Assert.That(Model(graph).CachedCount, Is.LessThan(200));
            Assert.That(list.Rows.Selection.AnchorIndex, Is.EqualTo(3));
            Press(host, PhysicalKey.ArrowDown); Assert.That(list.Rows.SelectedItem, Is.SameAs(commits[4])); Assert.That(list.Rows.IsKeyboardFocusWithin, Is.True);
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public async Task ActualClockSkewedGitReordersByWindowsScoresWithoutPublishingANullSelectionOrLosingTheCaretKey()
    {
        using GitFixture f = await GitFixture.CreateAsync();
        string root = (await f.GitAsync("rev-parse", "HEAD")).StandardOutput.Trim();
        string tree = (await f.GitAsync("rev-parse", "HEAD^{tree}")).StandardOutput.Trim();
        string independent = await CommitTree(f, tree, null, "2005-01-01T00:00:00+0000", "Independent");
        string child = await CommitTree(f, tree, root, "2000-01-01T00:00:00+0000", "Older-clock child");
        await f.GitAsync("update-ref", "refs/heads/independent", independent); await f.GitAsync("update-ref", "refs/heads/old-clock", child);
        GitCommit[] raw = (await f.Repository.GetRevisionGraphAsync(new(Count: 100, All: true))).ToArray();
        Assert.That(raw.Select(commit => commit.Hash), Is.EqualTo(new[] { root, independent, child }), "The fixture must really exercise a non-topological native log.");
        ChooseCommitSelectionPresenter presenter = new("ChooseRows", false, text => text, new() { ShowAvatar = false });
        presenter.BeginIncrementalData(await f.Repository.GetReferencesAsync(), root, artificialAllowed: false);
        presenter.AppendCommits(raw[..2]); Window host = Host(presenter);
        object source = presenter.Rows.ItemsSource!; List<ChooseCommitSelection?> publications = [];
        presenter.SelectionChanged += (_, _) => publications.Add(presenter.Selection);
        List<NotifyCollectionChangedAction> notifications = [];
        ((INotifyCollectionChanged)source).CollectionChanged += (_, change) => notifications.Add(change.Action);
        try
        {
            host.Show(); Pump(host); Pointer(host, Subject(presenter.Rows, raw[0]));
            ChooseCommitSelection selected = presenter.Selection!; publications.Clear(); Vector offset = Scroll(presenter.Rows).Offset;
            presenter.AppendCommits(raw[2..]); Pump(host);
            Assert.That(presenter.Rows.ItemsSource, Is.SameAs(source));
            Assert.That(presenter.Rows.Items.Cast<GitCommit>().Select(commit => commit.Hash), Is.EqualTo(new[] { independent, child, root }),
                "Windows Add pushes an already loaded parent behind its late child, retaining the unrelated earlier score.");
            Assert.That(presenter.Selection, Is.SameAs(selected)); Assert.That(presenter.Selection!.Commit, Is.SameAs(raw[0]));
            Assert.That(publications, Is.Empty, "Maintaining a keyed source must not publish a temporary null/user selection.");
            Assert.That(presenter.Rows.SelectedItem, Is.SameAs(raw[0])); Assert.That(presenter.Rows.Selection.AnchorIndex, Is.EqualTo(2));
            Assert.That(presenter.Rows.IsKeyboardFocusWithin, Is.True); Assert.That(Scroll(presenter.Rows).Offset, Is.EqualTo(offset));
            Assert.That(notifications, Does.Contain(NotifyCollectionChangedAction.Move).And.Not.Contain(NotifyCollectionChangedAction.Reset));
            Press(host, PhysicalKey.ArrowUp); Assert.That(presenter.Selection!.Commit, Is.SameAs(raw[2]));
            Assert.That(presenter.Rows.IsKeyboardFocusWithin, Is.True); presenter.CompleteCommits();
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public async Task NativeArrowsRevealUnrealizedRowsWithStreamingAutoScrollDisabledAndAppendPreservesTheScrolledViewport()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind: "none");
        GitCommit[] commits = Chain(240, "arrow/"); ExportRevisionList list = new("ChooseRows", false, view: new() { ShowAvatar = false });
        list.BeginIncrementalData([], commits[0].Hash); list.AppendCommits(commits[..120]); Window host = Host(list, height: 190);
        object source = list.Rows.ItemsSource!;
        try
        {
            host.Show(); Pump(host); Pointer(host, Subject(list.Rows, commits[1]));
            for (int index = 2; index <= 35; index++)
            {
                Press(host, PhysicalKey.ArrowDown);
                Assert.That(list.Rows.SelectedItem, Is.SameAs(commits[index])); Assert.That(list.Rows.IsKeyboardFocusWithin, Is.True);
            }
            ScrollViewer scroll = Scroll(list.Rows); Assert.That(scroll.Offset.Y, Is.GreaterThan(0));
            Control row = Row(list.Rows, commits[35]); Point point = row.TranslatePoint(default, scroll)!.Value;
            Assert.That(point.Y + row.Bounds.Height, Is.GreaterThan(0)); Assert.That(point.Y, Is.LessThan(scroll.Bounds.Height));
            Vector offset = scroll.Offset; int anchor = list.Rows.Selection.AnchorIndex;
            list.AppendCommits(commits[120..]); Pump(host);
            Assert.That(list.Rows.ItemsSource, Is.SameAs(source)); Assert.That(scroll.Offset, Is.EqualTo(offset));
            Assert.That(list.Rows.Selection.AnchorIndex, Is.EqualTo(anchor)); Assert.That(list.Rows.SelectedItem, Is.SameAs(commits[35]));
            Press(host, PhysicalKey.ArrowDown); Assert.That(list.Rows.SelectedItem, Is.SameAs(commits[36]));
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public async Task DuplicatesAndOptionsPublicationPreserveObjectsAndAnExternalEditorFocusWithoutReset()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind: "none");
        GitCommit[] commits = Chain(180, "options/"); ExportRevisionList list = new("ChooseRows", false, view: new() { ShowAvatar = false });
        TextBox editor = new() { Text = "unrelated draft", Height = 30 }; DockPanel body = new() { LastChildFill = true };
        DockPanel.SetDock(editor, Dock.Bottom); body.Children.Add(editor); body.Children.Add(list);
        list.BeginIncrementalData([], commits[0].Hash); list.AppendCommits(commits[..100]); Window host = Host(body);
        object source = list.Rows.ItemsSource!;
        try
        {
            host.Show(); Pump(host); list.Rows.SelectedItem = commits[3]; Pointer(host, editor);
            Control focused = (Control)host.FocusManager!.GetFocusedElement()!;
            list.AppendCommits([commits[0] with { Subject = "duplicate must not replace the admitted object" }, .. commits[100..]]);
            Graph(list).Options = new(false, false, false); Pump(host);
            Assert.That(host.FocusManager?.GetFocusedElement(), Is.SameAs(focused)); Assert.That(editor.Text, Is.EqualTo("unrelated draft"));
            Assert.That(list.Rows.ItemsSource, Is.SameAs(source)); Assert.That(list.Rows.Items[0], Is.SameAs(commits[0]));
            Assert.That(list.Rows.SelectedItem, Is.SameAs(commits[3])); Assert.That(list.Rows.ItemCount, Is.EqualTo(180));
            Assert.That(Graph(list).Options, Is.EqualTo(new RevisionGraphOptions(false, false, false)));
            Assert.That(Model(Graph(list)).IsComplete, Is.False); list.CompleteCommits();
        }
        finally { host.Close(); }
    }

    [AvaloniaTest]
    public async Task HiddenGraphAdmitsTheWholeStreamAndEmptyEofWithoutBuildingLaneCaches()
    {
        using DetailedSettingsTestScope f = await DetailedSettingsTestScope.CreateAsync(kind: "none");
        ExportRevisionList list = new("ChooseRows", false, view: new() { ShowGraph = false, ShowAvatar = false });
        list.BeginIncrementalData([], null); object emptySource = list.Rows.ItemsSource!; list.CompleteCommits();
        Assert.That(list.Rows.ItemCount, Is.Zero); Assert.That(list.Rows.ItemsSource, Is.SameAs(emptySource));
        GitCommit[] commits = Chain(1200, "hidden/"); list.BeginIncrementalData([], commits[0].Hash);
        object source = list.Rows.ItemsSource!; foreach (GitCommit[] batch in commits.Chunk(100)) { list.AppendCommits(batch); }
        list.CompleteCommits(); Window host = Host(list);
        try
        {
            host.Show(); Pump(host); Assert.That(list.Rows.ItemsSource, Is.SameAs(source)); Assert.That(list.Rows.ItemCount, Is.EqualTo(1200));
            Assert.That(Model(Graph(list)).CachedCount, Is.Zero); Assert.That(Graph(list).IsVisible, Is.False);
        }
        finally { host.Close(); }
    }

    private static Window Host(Control body, double height = 320) => new() { Width = 1000, Height = height, Content = body, RequestedThemeVariant = ThemeVariant.Light };
    private static GitCommit[] Chain(int count, string prefix) => Enumerable.Range(0, count).Select(index => new GitCommit(prefix + index,
        index + 1 < count ? [prefix + (index + 1)] : [], "Tester", "tester@example.invalid", DateTimeOffset.UnixEpoch.AddSeconds(count - index), "Revision " + index, "")).ToArray();
    private static GitCommit[] Parse(string specification) => specification.Split(' ').Select(value => value.Split(':')).Select(parts =>
        new GitCommit(parts[0], parts.Length == 1 ? [] : parts[1].Split(','), "Tester", "tester@example.invalid", DateTimeOffset.UnixEpoch, parts[0], "")).ToArray();
    private static object Native(IncrementalCommitGraphLayout model) => typeof(IncrementalCommitGraphLayout).GetField("_graph", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
    private static IncrementalCommitGraphLayout Model(CommitGraph graph) => (IncrementalCommitGraphLayout)typeof(CommitGraph).GetField("_incrementalLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph)!;
    private static CommitGraph Graph(Control control) => control.GetVisualDescendants().OfType<CommitGraph>().Single();
    private static ScrollViewer Scroll(ListBox rows) => rows.GetVisualDescendants().OfType<ScrollViewer>().First();
    private static Control Row(ListBox rows, GitCommit commit) => rows.GetVisualDescendants().OfType<ListBoxItem>()
        .Single(item => ReferenceEquals(item.DataContext, commit));
    private static Control Subject(ListBox rows, GitCommit commit) => Row(rows, commit).GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RevisionSubject");
    private static void Pump(Window host)
    { host.UpdateLayout(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Pointer(Window host, Control control)
    {
        Pump(host); Point point = control.TranslatePoint(default, host)!.Value + new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        Assert.That(new Rect(default, host.ClientSize).Contains(point), Is.True);
        host.MouseMove(point); host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left); Pump(host);
    }
    private static void Press(Window host, PhysicalKey key)
    { host.KeyPressQwerty(key, RawInputModifiers.None); host.KeyReleaseQwerty(key, RawInputModifiers.None); Pump(host); }
    private static async Task<string> CommitTree(GitFixture f, string tree, string? parent, string date, string title)
    {
        GitResult result = await new GitRunner("/usr/bin/git").RunAsync(f.Repository.Path,
            parent is null ? ["commit-tree", tree] : ["commit-tree", tree, "-p", parent], standardInput: title + "\n",
            environment: new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date });
        Assert.That(result.Success, Is.True, result.StandardError); return result.StandardOutput.Trim();
    }
    private static async Task Import(GitFixture f, int count)
    {
        StringBuilder input = new();
        for (int index = 0; index < count; index++)
        {
            string message = "Streamed revision " + index + "\n";
            input.Append("commit refs/heads/main\ncommitter Stream Tester <stream@example.invalid> ").Append(1700000000L + index)
                .Append(" +0000\ndata ").Append(Encoding.UTF8.GetByteCount(message)).Append('\n').Append(message);
            if (index == 0) { input.Append("M 100644 inline seed.txt\ndata 5\nseed\n"); } input.Append('\n');
        }
        input.Append("done\n"); GitResult result = await new GitRunner("/usr/bin/git").RunAsync(f.Repository.Path, ["fast-import", "--quiet"], standardInput: input.ToString());
        Assert.That(result.Success, Is.True, result.StandardError); await f.GitAsync("reset", "--hard", "HEAD");
    }
}
