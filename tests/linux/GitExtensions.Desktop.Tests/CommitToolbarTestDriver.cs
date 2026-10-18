using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using GitExtensions.Linux.Desktop.Controls;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

/// <summary>Use the real ToolStrip-style presentation without replacing its canonical menu or commands.</summary>
internal static class CommitToolbarTestDriver
{
    internal static Menu Toolbar(CommitDialog owner)
        => owner.GetVisualDescendants().OfType<Menu>().Single(menu => menu.Name == "CommitToolbar");

    internal static CompactMenuToolbar Controller(CommitDialog owner)
    {
        Menu toolbar = Toolbar(owner);
        CompactMenuToolbar? controller = CompactMenuToolbar.For(toolbar);
        Assert.That(controller, Is.Not.Null);
        Assert.That(toolbar.Items.OfType<MenuItem>().Take(controller!.CanonicalItems.Count), Is.EqualTo(controller.CanonicalItems),
            "Overflow must preserve the original item instances and order in the toolbar API.");
        return controller;
    }

    internal static void AssertLayout(CommitDialog owner)
    {
        Pump(owner);
        Menu toolbar = Toolbar(owner); CompactMenuToolbar controller = Controller(owner);
        Rect outer = Bounds(toolbar, owner);
        MenuItem options = controller.CanonicalItems.Single(item => item.Name == "Options");
        Assert.That(controller.IsOverflowed(options), Is.False);
        Assert.That(Bounds(options, owner).Right, Is.EqualTo(outer.Right).Within(.5), "Windows pins Options to the right.");
        Assert.That(toolbar.Bounds.Height, Is.EqualTo(28));
        foreach (MenuItem item in controller.CanonicalItems.Where(item => !controller.IsOverflowed(item))
            .Concat(controller.Overflow.IsVisible ? new[] { controller.Overflow } : Array.Empty<MenuItem>()))
        {
            Rect inner = Bounds(item, owner);
            Assert.That(inner.Width, Is.GreaterThan(0), item.Name);
            Assert.That(inner.Left, Is.GreaterThanOrEqualTo(outer.Left - .5), item.Name);
            Assert.That(inner.Right, Is.LessThanOrEqualTo(outer.Right + .5), item.Name);
            Assert.That(inner.Top, Is.GreaterThanOrEqualTo(outer.Top - .5), item.Name);
            Assert.That(inner.Bottom, Is.LessThanOrEqualTo(outer.Bottom + .5), item.Name);
            AssertAccessible(item); AssertCaption(item);
        }
    }

    internal static MenuItem Presentation(CommitDialog owner, MenuItem canonical, Action<TopLevel>? overflowOpened = null)
    {
        Pump(owner);
        Menu toolbar = Toolbar(owner); CompactMenuToolbar controller = Controller(owner);
        Assert.That(controller.CanonicalItems, Does.Contain(canonical));
        Assert.That(canonical.Parent, Is.SameAs(toolbar));
        if (!controller.IsOverflowed(canonical))
        {
            AssertAccessible(canonical); AssertCaption(canonical); AssertIcon(canonical, canonical);
            return canonical;
        }
        Assert.That(canonical.IsVisible, Is.False, "Use More only when the real layout placed this canonical item there.");
        AssertAccessible(controller.Overflow);
        if (!controller.Overflow.IsSubMenuOpen) { PushProcessTestDriver.Pointer(controller.Overflow); }
        Pump(owner);
        Assert.That(controller.Overflow.IsSubMenuOpen, Is.True);
        MenuItem proxy = controller.ProxyFor(canonical);
        Assert.That(controller.Overflow.Items.OfType<MenuItem>(), Does.Contain(proxy));
        Assert.That(proxy.Parent, Is.SameAs(controller.Overflow));
        Assert.That(proxy.Tag, Is.SameAs(canonical));
        Assert.That(HeaderText(proxy.Header), Is.EqualTo(HeaderText(canonical.Header)), "The overflow caption must remain complete.");
        AssertAccessible(proxy); AssertCaption(proxy); AssertIcon(canonical, proxy);
        overflowOpened?.Invoke(TopLevel.GetTopLevel(proxy)!);
        return proxy;
    }

    internal static MenuItem OpenMenu(CommitDialog owner, MenuItem canonical, Action<TopLevel>? overflowOpened = null)
    {
        Menu toolbar = Toolbar(owner); CompactMenuToolbar controller = Controller(owner);
        MenuItem presentation = Presentation(owner, canonical, overflowOpened);
        PushProcessTestDriver.Pointer(presentation); Pump(owner);
        Assert.That(canonical.IsSubMenuOpen, Is.True, "The actual pointer must open the original canonical menu.");
        Assert.That(toolbar.SelectedItem, Is.SameAs(canonical));
        Assert.That(controller.Overflow.IsSubMenuOpen, Is.False, "The proxy hands over to the one canonical menu tree.");
        MenuItem first = canonical.Items.OfType<MenuItem>().First(item => item.IsVisible && item.IsEnabled);
        Assert.That(first.Parent, Is.SameAs(canonical));
        Assert.That(first.GetVisualRoot(), Is.InstanceOf<TopLevel>(), "The original submenu must have a real rendered popup host.");
        AssertAccessible(first);
        return canonical;
    }

    internal static void AssertAccessible(Control control)
    {
        TopLevel? root = TopLevel.GetTopLevel(control);
        Assert.That(root, Is.Not.Null, control.Name);
        Pump(root!);
        Rect rect = Bounds(control, root!); Rect viewport = new(default, root!.ClientSize);
        Assert.That(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, Is.True, control.Name);
        Assert.That(rect.Width > 0 && rect.Height > 0, Is.True, control.Name);
        Assert.That(rect.Left, Is.GreaterThanOrEqualTo(viewport.Left - .5), control.Name);
        Assert.That(rect.Right, Is.LessThanOrEqualTo(viewport.Right + .5), control.Name);
        Assert.That(rect.Top, Is.GreaterThanOrEqualTo(viewport.Top - .5), control.Name);
        Assert.That(rect.Bottom, Is.LessThanOrEqualTo(viewport.Bottom + .5), control.Name);
        Visual? hit = root.InputHitTest(rect.Center) as Visual;
        Assert.That(hit?.GetSelfAndVisualAncestors().Contains(control), Is.True, "The real presentation must receive pointer input: " + control.Name);
    }

    private static void AssertCaption(MenuItem item)
    {
        if (item.Header is not Control header) { return; }
        foreach (TextBlock text in header.GetSelfAndVisualDescendants().OfType<TextBlock>())
        {
            Assert.That(text.TextLayout.TextLines.Any(line => line.HasCollapsed), Is.False, item.Name + " must not abbreviate its caption.");
            Assert.That(text.TextLayout.WidthIncludingTrailingWhitespace, Is.LessThanOrEqualTo(text.Bounds.Width + .5), item.Name);
        }
    }

    private static void AssertIcon(MenuItem canonical, MenuItem presentation)
    {
        Image[] originals = canonical.Header is Control header ? header.GetSelfAndVisualDescendants().OfType<Image>().ToArray() : [];
        Image[] drawn = presentation.GetVisualDescendants().OfType<Image>().Where(image => image.Bounds.Size == new Size(16, 16)).ToArray();
        Assert.That(drawn, Has.Length.EqualTo(originals.Length), "The presented command must draw every original 16 px icon: " + canonical.Name);
        Assert.That(drawn.Select(image => image.Source), Is.EqualTo(originals.Select(image => image.Source)), "Overflow must use the original icon source.");
    }

    private static string? HeaderText(object? header) => header is Control control
        ? control.GetSelfAndVisualDescendants().OfType<AccessText>().FirstOrDefault()?.Text
            ?? control.GetSelfAndVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text
        : header?.ToString();
    private static Rect Bounds(Control control, TopLevel root) => new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);
    internal static void Pump(TopLevel root)
    {
        for (int frame = 0; frame < 10; frame++)
        { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        Dispatcher.UIThread.RunJobs();
    }
}
