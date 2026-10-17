using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.MainWindowClassicTests.RepositoryFixture;
using Helpers = GitExtensions.Desktop.Tests.FileHistoryDialogTests;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class BrowseCommitGraphTests
{
    [AvaloniaTest]
    public void CompactLaneOnlyGraphKeepsOriginalDefaultHeightAndLabelsForOtherHosts()
    {
        GitCommit[] commits = [Commit("M", "A", "B"), Commit("A", "R"), Commit("B", "R"), Commit("R")];
        GitReference[] refs = [new("refs/heads/main", "M", null, true)];
        CommitGraph legacy = new() { Commits = commits, References = refs };
        legacy.Measure(Size.Infinity);
        Assert.That(CommitGraph.RowHeight, Is.EqualTo(24));
        Assert.That(legacy.LineHeight, Is.EqualTo(24));
        Assert.That(legacy.ShowReferenceLabels, Is.True);
        Assert.That(legacy.DesiredSize.Height, Is.EqualTo(96));
        CommitGraph compact = new() { Commits = commits, References = refs, ShowReferenceLabels = false, LineHeight = 20 };
        compact.Measure(Size.Infinity);
        Assert.That(compact.DesiredSize.Height, Is.EqualTo(80));
        Assert.That(compact.DesiredSize.Width, Is.LessThan(72));
        Assert.That(compact.OrderedCommits, Is.EqualTo(legacy.OrderedCommits));
        compact.Commits = [Commit("R")]; compact.Measure(Size.Infinity);
        Assert.That(compact.DesiredSize.Width, Is.EqualTo(20));
        Assert.That(compact.DesiredSize.Height, Is.EqualTo(20));
        compact.LineHeight = 22; compact.LaneWidth = 18; compact.Measure(Size.Infinity);
        Assert.That(compact.DesiredSize.Width, Is.EqualTo(22));
        Assert.That(compact.DesiredSize.Height, Is.EqualTo(22));
        legacy.Measure(Size.Infinity);
        Assert.That(legacy.DesiredSize.Height, Is.EqualTo(96), "Changing Browse density must leave FileHistory/Export graph instances alone.");
    }

    [AvaloniaTest]
    public async Task ActualMergeRowsAndCompactGraphStayAlignedAfterScrollingAndRenderParentLanesInBothThemes()
    {
        using Fixture fixture = await Fixture.CreateAsync("root\n", "root\n", createMerge: true);
        GitRepository repository = await GitRepository.OpenAsync(fixture.Repository);
        IReadOnlyList<GitCommit> commits = await repository.GetLogAsync(30);
        IReadOnlyList<GitReference> refs = await repository.GetReferencesAsync();
        CommitGraph graph = new() { Commits = commits, References = refs, CurrentHead = await repository.ResolveRevisionAsync("HEAD"),
            ShowReferenceLabels = false, LineHeight = 20 };
        ListBox history = RevisionHistoryRowTests.History(graph.OrderedCommits, refs);
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        body.Children.Add(graph); Grid.SetColumn(history, 1); body.Children.Add(history);
        Window window = new() { Width = 1280, Height = 160, Content = body }; window.Show();
        try
        {
            window.UpdateLayout();
            ScrollViewer scroll = history.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.ScrollChanged += (_, _) => graph.VerticalOffset = scroll.Offset.Y;
            Assert.That(graph.OrderedCommits[0].Parents.Count, Is.EqualTo(2));
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; window.Height = 160;
                scroll.Offset = default; graph.VerticalOffset = 0;
                using WriteableBitmap initial = Helpers.Capture(window, "browse-compact-merge-" + theme + ".png");
                Assert.That(graph.Bounds.Width, Is.LessThan(72));
                Assert.That(graph.Bounds.Width, Is.GreaterThanOrEqualTo(36), "A real two-parent merge must occupy separate graph lanes.");
                ListBoxItem[] rows = history.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
                Assert.That(rows.Select(row => row.Bounds.Height), Is.All.EqualTo(20));
                Assert.That(rows[0].TranslatePoint(default, window)!.Value.Y, Is.EqualTo(graph.TranslatePoint(default, window)!.Value.Y).Within(1));
                Assert.That(ColoredPixels(initial, window, graph), Is.GreaterThan(40), "The real merge needs visible nodes and parent edges, not a single line placeholder.");
                window.Height = 50; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.That(scroll.Extent.Height, Is.GreaterThan(scroll.Viewport.Height));
                using WriteableBitmap compact = Helpers.Capture(window, "browse-compact-merge-before-scroll-" + theme + ".png");
                byte[] before = Bytes(compact);
                scroll.Offset = new Vector(0, 20); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.That(scroll.Offset.Y, Is.EqualTo(20));
                Assert.That(graph.VerticalOffset, Is.EqualTo(scroll.Offset.Y));
                using WriteableBitmap scrolled = Helpers.Capture(window, "browse-compact-merge-scrolled-" + theme + ".png");
                Assert.That(Bytes(scrolled), Is.Not.EqualTo(before));
                ListBoxItem second = history.GetVisualDescendants().OfType<ListBoxItem>()
                    .Single(item => (item.DataContext as GitCommit)?.Hash == graph.OrderedCommits[1].Hash);
                Assert.That(second.TranslatePoint(default, window)!.Value.Y, Is.EqualTo(graph.TranslatePoint(default, window)!.Value.Y).Within(1),
                    "Scrolling the native list must move the second commit onto the corresponding graph row.");
                Point node = graph.TranslatePoint(new Point(10, 10), window)!.Value;
                window.MouseMove(node);
                Assert.That(ToolTip.GetTip(graph) as string, Does.Contain(graph.OrderedCommits[1].Hash));
                scroll.Offset = default; graph.VerticalOffset = 0;
            }
            Assert.That((await repository.GetStatusAsync()).Changes, Is.Empty);
        }
        finally { window.Close(); }
    }

    private static GitCommit Commit(string hash, params string[] parents) => new(hash, parents, "Author", "author@example.invalid", DateTimeOffset.UnixEpoch, hash, "");
    private static byte[] Bytes(WriteableBitmap frame)
    { using ILockedFramebuffer buffer = frame.Lock(); byte[] bytes = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, bytes, 0, bytes.Length); return bytes; }
    private static int ColoredPixels(WriteableBitmap frame, Window owner, Control control)
    {
        Point origin = control.TranslatePoint(default, owner)!.Value;
        using ILockedFramebuffer buffer = frame.Lock(); byte[] bytes = new byte[buffer.RowBytes * buffer.Size.Height]; Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        int count = 0;
        for (int y = Math.Max(0, (int)origin.Y); y < Math.Min(buffer.Size.Height, (int)(origin.Y + control.Bounds.Height)); y++)
        for (int x = Math.Max(0, (int)origin.X); x < Math.Min(buffer.Size.Width, (int)(origin.X + control.Bounds.Width)); x++)
        {
            int offset = y * buffer.RowBytes + x * 4;
            byte one = bytes[offset], two = bytes[offset + 1], three = bytes[offset + 2];
            if (Math.Max(one, Math.Max(two, three)) - Math.Min(one, Math.Min(two, three)) > 30) { count++; }
        }
        return count;
    }
}
