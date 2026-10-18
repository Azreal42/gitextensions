using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

/// <summary>Exercise the visible Settings tree, including expansion, before editing a page.</summary>
internal static class SettingsNavigationTestDriver
{
    internal static void Select(Window window, string id)
    {
        Pump(window);
        TreeView tree = PushProcessTestDriver.Field<TreeView>(window, "SettingsNavigationTree");
        TreeViewItem[] path = FindPath(tree.Items.OfType<TreeViewItem>(), "Settings" + id + "Node")
            ?? throw new AssertionException("Settings page not found in the navigation tree: " + id);
        foreach (TreeViewItem parent in path.SkipLast(1))
        {
            if (parent.IsExpanded) { continue; }
            PointerHeader(parent); Pump(window);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); Pump(window);
            Assert.That(parent.IsExpanded, Is.True, "The actual Settings tree must expand with Right.");
        }
        TreeViewItem node = path[^1]; node.BringIntoView(); Pump(window); PointerHeader(node); Pump(window);
        Assert.That(tree.SelectedItem, Is.SameAs(node));
    }

    internal static void PointerHeader(TreeViewItem node)
    {
        TopLevel window = TopLevel.GetTopLevel(node)!;
        for (int frame = 0; frame < 10; frame++) { Pump(window); }
        Control header = node.Header as Control ?? node;
        Point point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        Assert.That(header.IsEffectivelyVisible && node.IsEffectivelyEnabled
            && new Rect(default, window.ClientSize).Contains(point)
            && (window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Contains(node) == true,
            Is.True, "Real Settings tree header pointer target: " + node.Name);
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump(window);
    }

    private static TreeViewItem[]? FindPath(IEnumerable<TreeViewItem> nodes, string name)
    {
        foreach (TreeViewItem node in nodes)
        {
            if (node.Name == name) { return [node]; }
            if (FindPath(node.Items.OfType<TreeViewItem>(), name) is { } children) { return [node, .. children]; }
        }
        return null;
    }

    private static void Pump(TopLevel window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}
