using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using System.Runtime.InteropServices;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public class CommitGraphControlTests
{
    [AvaloniaTest]
    public void MergeGraphRendersReferencesAndScrollsWithoutChangingRowHeight()
    {
        GitCommit[] commits =
        [
            Commit("M", "A", "B"), Commit("A", "R"), Commit("B", "R"), Commit("R")
        ];
        CommitGraph graph = new()
        {
            Commits = commits,
            References = [new("refs/heads/main", "M", null, true), new("refs/tags/v1", "R", null, false)],
            CurrentHead = "M",
            Width = 180
        };
        Window window = new() { Width = 210, Height = 120, Content = graph, Background = Brushes.Black, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        try
        {
            window.Show();
            graph.Measure(Size.Infinity);
            Assert.That(graph.DesiredSize.Height, Is.EqualTo(96));
            Assert.That(graph.OrderedCommits.Select(commit => commit.Hash), Is.EqualTo(new[] { "M", "A", "B", "R" }));
            using Avalonia.Media.Imaging.WriteableBitmap? first = window.CaptureRenderedFrame();
            Assert.That(first, Is.Not.Null);
            first!.Save(Path.Combine(TestContext.CurrentContext.WorkDirectory, "commit-graph.png"));
            byte[] before = Pixels(first);
            graph.VerticalOffset = 24;
            using Avalonia.Media.Imaging.WriteableBitmap? scrolled = window.CaptureRenderedFrame();
            Assert.That(scrolled, Is.Not.Null);
            Assert.That(Pixels(scrolled!), Is.Not.EqualTo(before), "Scrolling must change the rendered nodes and parent edges.");

            graph.Commits = [Commit("NEW_ROOT")];
            graph.Measure(Size.Infinity);
            Assert.That(graph.DesiredSize.Height, Is.EqualTo(24));
            Assert.That(graph.OrderedCommits.Single().Hash, Is.EqualTo("NEW_ROOT"));
        }
        finally { window.Close(); }
    }

    private static GitCommit Commit(string hash, params string[] parents)
        => new(hash, parents, "Author", "author@example.com", DateTimeOffset.UnixEpoch, $"Commit {hash}", "");

    private static byte[] Pixels(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    {
        using Avalonia.Platform.ILockedFramebuffer pixels = bitmap.Lock();
        byte[] data = new byte[pixels.RowBytes * pixels.Size.Height];
        Marshal.Copy(pixels.Address, data, 0, data.Length);
        return data;
    }
}
