using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class ExceptionDetailDialogTests
{
    [AvaloniaTest]
    [TestCase("en", false)] [TestCase("fr", true)]
    public async Task OriginalToolSheetKeepsReadonlyFullTextSelectionAndBothScrollbarsAfterResize(string language, bool dark)
    {
        using PortableHelperSettingsFixture f = new(language, dark);
        string literal = string.Join('\n', Enumerable.Range(0, 40).Select(index => "frame " + index + " " + new string('x', 180) + " Ω"));
        ExceptionDetailDialog detail = new("Stack Trace", literal, f.Translations) { RequestedThemeVariant = f.Theme };
        f.Track(detail); Task completion = detail.ShowDialog(f.Owner); PortableHelperSettingsFixture.Pump(detail);
        TextBox name = PortableHelperSettingsFixture.Field<TextBox>(detail, "propertyTextBox");
        TextBox value = PortableHelperSettingsFixture.Field<TextBox>(detail, "propertyInformationTextBox");
        Assert.That(detail.ClientSize, Is.EqualTo(new Size(567, 218))); Assert.That(detail.Owner, Is.SameAs(f.Owner));
        Assert.That(detail.CanMinimize || detail.CanMaximize || detail.ShowInTaskbar, Is.False);
        Assert.That(detail.GetVisualDescendants().OfType<Button>().Any(button => button.IsDefault || button.IsCancel), Is.False);
        Assert.That(name.Text, Is.EqualTo("Stack Trace")); Assert.That(name.IsReadOnly && value.IsReadOnly && value.AcceptsReturn, Is.True);
        Assert.That(value.TextWrapping, Is.EqualTo(TextWrapping.NoWrap)); Assert.That(value.Text, Is.EqualTo(literal));
        Assert.That(ScrollViewer.GetHorizontalScrollBarVisibility(value), Is.EqualTo(ScrollBarVisibility.Visible));
        Assert.That(ScrollViewer.GetVerticalScrollBarVisibility(value), Is.EqualTo(ScrollBarVisibility.Visible));
        PushProcessTestDriver.Pointer(value); PortableHelperSettingsFixture.Press(detail, PhysicalKey.A, RawInputModifiers.Control);
        Assert.That(value.SelectedText, Is.EqualTo(literal)); detail.KeyTextInput("cannot overwrite");
        PushProcessTestDriver.Pointer(name); Assert.That(value.SelectedText, Is.EqualTo(literal), "Blur hides the highlight without dropping the selected text.");
        Assert.That(value.Text, Is.EqualTo(literal));
        PortableHelperSettingsFixture.Capture(detail, "exception-detail-" + language + (dark ? "-dark.png" : "-light.png"));
        detail.Width = 340; detail.Height = 160; PortableHelperSettingsFixture.Pump(detail);
        Assert.That(name.Bounds.Width, Is.GreaterThan(100)); Assert.That(value.Bounds.Height, Is.GreaterThan(40));
        ScrollViewer scroll = value.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.That(scroll.Extent.Width, Is.GreaterThan(scroll.Viewport.Width)); Assert.That(scroll.Extent.Height, Is.GreaterThan(scroll.Viewport.Height));
        Assert.That(value.Text, Is.EqualTo(literal)); detail.Close(); await completion.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
