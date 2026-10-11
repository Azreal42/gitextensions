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
    public void ChangingThemeRedrawsReferenceBadgesWithTheirOriginalColors()
    {
        CommitGraph graph = new()
        {
            Commits = [Commit("HEAD")], References = [new("refs/heads/main", "HEAD", null, true)], Width = 180
        };
        Window window = new() { Width = 210, Height = 80, Content = graph, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        try
        {
            window.Show();
            using Avalonia.Media.Imaging.WriteableBitmap? light = window.CaptureRenderedFrame();
            Assert.That(light, Is.Not.Null);
            AssertContains(light!, Color.Parse("#008000"));
            window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            using Avalonia.Media.Imaging.WriteableBitmap? dark = window.CaptureRenderedFrame();
            Assert.That(dark, Is.Not.Null);
            AssertContains(dark!, Color.Parse("#7FE28A"));
        }
        finally { window.Close(); }
    }

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

    private static void AssertContains(Avalonia.Media.Imaging.WriteableBitmap bitmap, Color color)
    {
        using Avalonia.Platform.ILockedFramebuffer pixels = bitmap.Lock();
        byte[] data = new byte[pixels.RowBytes * pixels.Size.Height];
        Marshal.Copy(pixels.Address, data, 0, data.Length);
        bool bgra = pixels.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        int count = 0;
        for (int y = 0; y < pixels.Size.Height; y++)
        {
            for (int x = 0; x < pixels.Size.Width; x++)
            {
                int offset = y * pixels.RowBytes + x * 4;
                if (data[offset] == (bgra ? color.B : color.R) && data[offset + 1] == color.G && data[offset + 2] == (bgra ? color.R : color.B) && data[offset + 3] == 255) { count++; }
            }
        }
        Assert.That(count, Is.GreaterThan(0), "Reference badge text must be redrawn in its original theme color (thin antialiased borders have partial alpha).");
    }
}
