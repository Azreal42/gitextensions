using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RevisionAvatarRenderingTests
{
    [AvaloniaTest]
    [TestCase(0d, 0d)]
    [TestCase(0d, 16d)]
    [TestCase(16d, 0d)]
    public void CollapsedAuthorAvatarDrawsNothingAndRendersAgainWhenBothDimensionsArePositive(double width, double height)
    {
        GitCommit commit = new("0123456789abcdef", [], "Desktop Tester", "desktop@example.com",
            new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero), "Avatar rendering regression", "");
        Control row = RevisionHistoryRow.Create(commit, []);
        Control avatar = row.GetVisualDescendants().OfType<Control>()
            .Single(control => control.Name == "RevisionAuthorAvatar");
        avatar.Width = double.NaN;
        avatar.Height = double.NaN;
        avatar.HorizontalAlignment = HorizontalAlignment.Stretch;
        avatar.VerticalAlignment = VerticalAlignment.Stretch;

        Size collapsed = new(width, height);
        avatar.Measure(collapsed);
        avatar.Arrange(new Rect(collapsed));
        Assert.That(avatar.Bounds.Size, Is.EqualTo(collapsed), "Exercise the real zero-sized avatar rather than its original fixed 16px bounds.");

        DrawingGroup collapsedDrawing = new();
        Assert.DoesNotThrow(() =>
        {
            using DrawingContext context = collapsedDrawing.Open();
            avatar.Render(context);
        }, "A transient zero width or height must not construct text with a zero em size.");
        Assert.That(collapsedDrawing.Children, Is.Empty, "An avatar without drawable area must return before recording any drawing.");

        Size visible = new(16, 16);
        avatar.Measure(visible);
        avatar.Arrange(new Rect(visible));
        Assert.That(avatar.Bounds.Size, Is.EqualTo(visible));
        DrawingGroup visibleDrawing = new();
        Assert.DoesNotThrow(() =>
        {
            using DrawingContext context = visibleDrawing.Open();
            avatar.Render(context);
        });
        Assert.That(visibleDrawing.Children, Is.Not.Empty, "The same avatar must recover its visible rendering after layout restores its size.");
        Assert.That(visibleDrawing.GetBounds().Width, Is.GreaterThan(0));
        Assert.That(visibleDrawing.GetBounds().Height, Is.GreaterThan(0));
    }
}
