using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Desktop.Controls.Graph;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionGraphOptionsTests
{
    // The same nontrivial crossing fixture is used by the Windows RevisionGraphTests.
    private const string Crossings = "0:C,1,2,3,4,5,6,7,8,9,A,B 1:R 2:R 3:R 4:C 5:C 6:C 7:R 8:C 9:R A:C B:R C:D D:E E:F F:G G:H,K,R H:I,R I:J,R J:R K:R R";
    private static IEnumerable<TestCaseData> OptionCases
    {
        get
        {
            foreach (bool merge in new[] { false, true })
            foreach (bool diagonals in new[] { false, true })
            foreach (bool straighten in new[] { false, true })
            { yield return new TestCaseData(merge, diagonals, straighten); }
        }
    }

    [Test]
    public void AllThreeDefaultsMatchWindowsAndCrossingReductionIsTheInverseOfLaneMerging()
    {
        RevisionGraphOptions defaults = new();
        Assert.That(defaults, Is.EqualTo(new RevisionGraphOptions(true, true, true)));
        Assert.That(CommitGraphLayout.Build([]).Options, Is.EqualTo(defaults));
        Assert.That(new RevisionGraphConfig(null).ReduceGraphCrossings, Is.False);
        Assert.That(default(RevisionGraphConfig).Options, Is.EqualTo(defaults));
        RevisionGraphOptions custom = defaults with { MergeGraphLanesHavingCommonParent = false, StraightenGraphDiagonals = false };
        RevisionGraph graph = new(custom);
        Assert.That(graph.Config.ReduceGraphCrossings, Is.True);
        graph.Add(new LayoutRevision("A", ["R"], "A")); graph.Clear();
        Assert.That(graph.Config.Options, Is.SameAs(custom), "Clearing a graph retains its immutable options snapshot.");
        Assert.That(defaults, Is.EqualTo(new RevisionGraphOptions()), "Changing an immutable snapshot cannot mutate the shared defaults.");
    }

    [TestCase(true, new[] { 0, 0, 1, 0, 0, 1, 1, 0 })]
    [TestCase(false, new[] { 0, 0, 1, 0, 0, 1, 2, 0 })]
    public void CommonParentLanesMatchTheExactWindowsVerifiedNodePositions(bool merge, int[] lanes)
    {
        // Windows RevisionGraphTests.SegmentsAreStraightened_* .verified.txt;
        // its setup explicitly disables StraightenGraphDiagonals.
        GitCommit[] commits = Parse("8:7,2 7:5,6 6:5 5:4 4:1,3 3:1 2:1 1");
        CommitGraphLayout layout = CommitGraphLayout.Build(commits, options: new(merge, true, false));
        Assert.That(layout.Nodes.Select(node => node.Commit.Hash), Is.EqualTo(commits.Select(commit => commit.Hash)));
        Assert.That(layout.Nodes.Select(node => node.Lane), Is.EqualTo(lanes));
    }

    [Test]
    public void StraighteningUsesTheWindowsMultiLaneCrossingAlgorithmAndItsVerifiedPositions()
    {
        // Windows TurnMultiLaneCrossingsIntoDiagonals, first verified fixture.
        GitCommit[] commits = Parse("0:1,3 1:2 2:R,5,4 3:R 4:R 5:R R");
        CommitGraphLayout straight = CommitGraphLayout.Build(commits, options: new(false, true, true));
        CommitGraphLayout original = CommitGraphLayout.Build(commits, options: new(false, true, false));
        Assert.That(straight.Nodes.Select(node => node.Lane), Is.EqualTo(new[] { 0, 0, 0, 3, 1, 2, 0 }));
        Assert.That(straight.Strokes, Is.Not.EqualTo(original.Strokes), "The real lane-straightening option must change the layout, not only drawing metadata.");
    }

    [TestCase("0:1,4 1:2 2:R,3 3:R 4:R R", new[] { 0, 0, 0, 1, 2, 0 })]
    [TestCase("0:1,5 1:2 2:R,3 3:R,4 4:R 5:R R", new[] { 0, 0, 0, 1, 2, 3, 0 })]
    [TestCase("0:3,2 1:5 2:5 3:5 4:R 5:R R", new[] { 0, 2, 0, 1, 3, 0, 0 })]
    [TestCase("0:6 1:6 2:6 3:6 4:R 5:7 6:R 7:R R", new[] { 0, 1, 2, 3, 4, 5, 0, 2, 0 })]
    public void StraighteningRetainsWindowsUnfoldJoinAndDoNotJoinVerifiedPositions(string specification, int[] lanes)
    {
        // Source oracles: UnfoldOneLaneShiftsToDiagonals.1,
        // DoNotUnfoldOneLaneShiftFollowedByDiagonal.1, JoinMultiLaneCrossings.1,
        // DoNotJoinMultiLaneCrossings.1 .verified.txt in the Windows suite.
        CommitGraphLayout layout = CommitGraphLayout.Build(Parse(specification), options: new(false, true, true));
        Assert.That(layout.Nodes.Select(node => node.Lane), Is.EqualTo(lanes));
    }

    [TestCaseSource(nameof(OptionCases))]
    public void EveryOptionCombinationPreservesTopologyAndContinuousParentEdges(bool merge, bool diagonals, bool straighten)
    {
        RevisionGraphOptions options = new(merge, diagonals, straighten);
        GitCommit[] commits = Parse(Crossings);
        foreach (bool onlyFirstParent in new[] { false, true })
        {
            CommitGraphLayout layout = CommitGraphLayout.Build(commits, onlyFirstParent, options);
            Assert.That(layout.Options, Is.SameAs(options));
            Assert.That(layout.Nodes.Select(node => node.Commit.Hash), Is.EquivalentTo(commits.Select(commit => commit.Hash)));
            Assert.That(layout.Nodes.All(node => node.Lane >= 0 && node.Lane < layout.LaneCount), Is.True);
            for (int childRow = 0; childRow < layout.Nodes.Count; childRow++)
            {
                GitCommit commit = layout.Nodes[childRow].Commit;
                foreach (string parent in onlyFirstParent ? commit.Parents.Take(1) : commit.Parents)
                {
                    int parentRow = layout.Nodes.ToList().FindIndex(node => node.Commit.Hash == parent);
                    GraphStroke[] edge = layout.Strokes.Where(stroke => stroke.Child == commit.Hash && stroke.Parent == parent).ToArray();
                    Assert.That(parentRow, Is.GreaterThan(childRow));
                    Assert.That(edge, Has.Length.EqualTo(2 * (parentRow - childRow)));
                    for (int row = childRow; row < parentRow; row++)
                    {
                        GraphStroke outgoing = edge.Single(stroke => stroke.Row == row && !stroke.UpperHalf);
                        GraphStroke incoming = edge.Single(stroke => stroke.Row == row + 1 && stroke.UpperHalf);
                        Assert.That(outgoing.CenterLane + outgoing.AdjacentLane, Is.EqualTo(incoming.CenterLane + incoming.AdjacentLane));
                        Assert.That(outgoing.Color, Is.EqualTo(incoming.Color));
                    }
                }
            }
            for (int row = 0; row < layout.Nodes.Count; row++)
            foreach (GraphRenderSegment segment in layout.RenderSegmentsForRow(row, 16, 24))
            {
                Assert.That(commits.Single(commit => commit.Hash == segment.Child).Parents, Does.Contain(segment.Parent));
                Assert.That(segment.Path, Is.Not.Empty);
                foreach (GraphPathPart part in segment.Path)
                {
                    foreach (Point point in new[] { part.From, part.To }.Concat(part.IsBezier ? new[] { part.Control1!.Value, part.Control2!.Value } : []))
                    { Assert.That(double.IsFinite(point.X) && double.IsFinite(point.Y), Is.True); }
                }
            }
            CommitGraphLayout otherRendering = CommitGraphLayout.Build(commits, onlyFirstParent, options with { RenderGraphWithDiagonals = !diagonals });
            Assert.That(otherRendering.Nodes, Is.EqualTo(layout.Nodes), "Rendering diagonals does not change the layout's topology or node placement.");
            Assert.That(otherRendering.Strokes, Is.EqualTo(layout.Strokes));
        }
    }

    [Test]
    public void CommonParentRenderingOmitsSecondarySharedCurvesButKeepsThemForHighlighting()
    {
        CommitGraphLayout layout = CommitGraphLayout.Build(Parse("A:R B:R C:R D:R R"));
        int normal = Enumerable.Range(0, layout.Nodes.Count).Sum(row => layout.RenderSegmentsForRow(row, 16, 24).Count);
        int highlighted = Enumerable.Range(0, layout.Nodes.Count).Sum(row => layout.RenderSegmentsForRow(row, 16, 24, skipSecondarySharedSegments: false).Count);
        Assert.That(highlighted, Is.GreaterThan(normal), "Windows avoids overpainting entirely shared lanes unless individual edges must be highlighted.");
        IReadOnlySet<string> related = new HashSet<string>(["B"], StringComparer.Ordinal);
        foreach (int row in Enumerable.Range(0, layout.Nodes.Count))
        {
            bool seenHighlighted = false;
            foreach (GraphRenderSegment segment in layout.RenderSegmentsForRow(row, 16, 24, false, related))
            {
                bool selected = related.Contains(segment.Child) || related.Contains(segment.Parent);
                Assert.That(!seenHighlighted || selected, Is.True, "Colored shared edges must be painted after unrelated gray edges.");
                seenHighlighted |= selected;
            }
        }
    }

    [Test]
    public void SegmentRendererRetainsTheWindowsControlPointsRatherThanAlwaysDrawingTheSameCubic()
    {
        List<GraphPathPart> curves = [];
        RevisionGraphSegmentRenderer curvy = new(new(true, false, true), 16, 24, curves);
        curvy.DrawTo(8, 0); curvy.DrawTo(24, 24);
        Assert.That(curves, Is.EqualTo(new[] { new GraphPathPart(new(7.875, 0), new(23.875, 24), new(7.875, 12), new(23.875, 12)) }));
        List<GraphPathPart> diagonals = [];
        RevisionGraphSegmentRenderer diagonal = new(new(), 16, 24, diagonals);
        diagonal.DrawTo(8, 0); diagonal.DrawTo(24, 24);
        Assert.That(diagonals, Is.EqualTo(new[]
        {
            new GraphPathPart(new(7.875, 0), new(15.875, 12), new(7.875, 6), new(11.875, 6)),
            new GraphPathPart(new(23.875, 24), new(15.875, 12), new(23.875, 18), new(19.875, 18))
        }));
        List<GraphPathPart> straight = [];
        RevisionGraphSegmentRenderer aligned = new(new(), 16, 24, straight);
        aligned.DrawTo(8, 0, false); aligned.DrawTo(24, 24, false);
        Assert.That(straight, Is.EqualTo(new[] { new GraphPathPart(new(7.875, 0), new(23.875, 24)) }));
        List<GraphPathPart> vertical = [];
        RevisionGraphSegmentRenderer sameLane = new(new(), 16, 24, vertical);
        sameLane.DrawTo(8, 0); sameLane.DrawTo(8, 24);
        Assert.That(vertical, Is.EqualTo(new[] { new GraphPathPart(new(8, 0), new(8, 24)) }));
    }

    [TestCaseSource(nameof(OptionCases))]
    public void TruncatedAndDisconnectedHistoriesDoNotInventParentsUnderAnyOptions(bool merge, bool diagonals, bool straighten)
    {
        CommitGraphLayout layout = CommitGraphLayout.Build(Parse("M:A,B UNRELATED A:OLDER_A B:OLDER_B"), options: new(merge, diagonals, straighten));
        Assert.That(layout.Strokes.Where(stroke => stroke.ContinuesOutsideHistory).Select(stroke => stroke.Parent), Is.EquivalentTo(new[] { "OLDER_A", "OLDER_B" }));
        Assert.That(layout.Strokes.Any(stroke => stroke.Child == "UNRELATED" || stroke.Parent == "UNRELATED"), Is.False);
        Assert.That(layout.Nodes.Select(node => node.Commit.Hash), Does.Contain("UNRELATED"));
        for (int row = 0; row < layout.Nodes.Count; row++)
        { Assert.That(layout.RenderSegmentsForRow(row, 16, 24).Any(segment => segment.Child == "UNRELATED" || segment.Parent == "UNRELATED"), Is.False); }
    }

    [AvaloniaTest]
    [TestCase(false)] [TestCase(true)]
    public void ChangingOptionsRebuildsAndRendersAllEightVariantsWhilePreservingBrowserState(bool dark)
    {
        GitCommit[] commits = Parse(Crossings);
        GitReference[] references = [new("refs/heads/main", "0", null, true), new("refs/tags/v1", "R", null, false)];
        CommitGraph graph = new()
        {
            Commits = commits, References = references, CurrentHead = "0", SelectedHashes = ["C", "R"],
            VerticalOffset = 12, ShowReferenceLabels = false, LineHeight = 24, LaneWidth = 16, OnlyFirstParent = false
        };
        IReadOnlyCollection<string> selection = graph.SelectedHashes;
        Window window = new() { Width = 360, Height = 590, Content = graph, Background = dark ? Brushes.Black : Brushes.White,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        Dictionary<(bool Merge, bool Diagonals, bool Straighten), byte[]> renders = [];
        try
        {
            window.Show(); Pump(window);
            foreach (bool merge in new[] { false, true })
            foreach (bool diagonals in new[] { false, true })
            foreach (bool straighten in new[] { false, true })
            {
                RevisionGraphOptions options = new(merge, diagonals, straighten);
                graph.Options = options; Pump(window);
                CommitGraphLayout layout = Layout(graph);
                graph.Options = options with { };
                Assert.That(Layout(graph), Is.SameAs(layout), "An equal immutable snapshot must not rebuild the layout.");
                Assert.That(graph.Commits, Is.SameAs(commits)); Assert.That(graph.References, Is.SameAs(references));
                Assert.That(graph.SelectedHashes, Is.SameAs(selection)); Assert.That(graph.SelectedHashes, Is.EquivalentTo(new[] { "C", "R" }));
                Assert.That(graph.CurrentHead, Is.EqualTo("0")); Assert.That(graph.VerticalOffset, Is.EqualTo(12));
                Assert.That(graph.OnlyFirstParent, Is.False); Assert.That(layout.Options, Is.EqualTo(options));
                Assert.That(graph.OrderedCommits.Select(commit => commit.Hash), Is.EqualTo(commits.Select(commit => commit.Hash)));
                graph.Measure(Size.Infinity); Assert.That(graph.DesiredSize.Height, Is.EqualTo(commits.Length * 24));
                using WriteableBitmap? frame = window.CaptureRenderedFrame(); Assert.That(frame, Is.Not.Null);
                renders[(merge, diagonals, straighten)] = Pixels(frame!);
                Capture(frame!, $"revision-graph-options-merge-{merge}-diagonals-{diagonals}-straighten-{straighten}-{(dark ? "dark" : "light")}.png");
            }
            foreach (bool merge in new[] { false, true })
            foreach (bool straighten in new[] { false, true })
            { Assert.That(renders[(merge, true, straighten)], Is.Not.EqualTo(renders[(merge, false, straighten)]), "Toggling RenderGraphWithDiagonals must change the actual pixels."); }
            foreach (bool diagonals in new[] { false, true })
            { Assert.That(renders[(true, diagonals, false)], Is.Not.EqualTo(renders[(false, diagonals, false)]), "Common-parent merging must affect the actual displayed lanes."); }
            graph.OnlyFirstParent = true; graph.Options = new(false, false, false); Pump(window);
            Assert.That(graph.OnlyFirstParent, Is.True); Assert.That(graph.Commits, Is.SameAs(commits));
            Assert.That(graph.References, Is.SameAs(references)); Assert.That(graph.SelectedHashes, Is.SameAs(selection)); Assert.That(graph.VerticalOffset, Is.EqualTo(12));
            Assert.That(Layout(graph).Strokes.Any(stroke => stroke.Child == "0" && stroke.Parent == "1"), Is.False);
        }
        finally { window.Close(); }
    }

    private static CommitGraphLayout Layout(CommitGraph graph) => (CommitGraphLayout)typeof(CommitGraph).GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(graph)!;
    private static GitCommit[] Parse(string specification) => specification.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(item =>
    {
        string[] parts = item.Split(':', 2);
        return new GitCommit(parts[0], parts.Length > 1 ? parts[1].Split(',') : [], "Author", "author@example.com", DateTimeOffset.UnixEpoch, parts[0], "");
    }).ToArray();
    private static void Pump(TopLevel root)
    { for (int frame = 0; frame < 4; frame++) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } Dispatcher.UIThread.RunJobs(); }
    private static byte[] Pixels(WriteableBitmap bitmap)
    { using var locked = bitmap.Lock(); byte[] bytes = new byte[locked.RowBytes * locked.Size.Height]; Marshal.Copy(locked.Address, bytes, 0, bytes.Length); return bytes; }
    private static void Capture(WriteableBitmap bitmap, string name)
    {
        string root = Environment.GetEnvironmentVariable("GITEXTENSIONS_SCREENSHOTS") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(root); string path = Path.Combine(root, name); bitmap.Save(path); TestContext.AddTestAttachment(path);
    }
}
