using Avalonia.Controls;

namespace GitExtensions.Desktop.Tests;

internal static class BrowseTestHelpers
{
    internal static IEnumerable<TreeViewItem> NavigationNodes(TreeView navigation) => Descendants(navigation.Items.OfType<TreeViewItem>());
    private static IEnumerable<TreeViewItem> Descendants(IEnumerable<TreeViewItem> nodes)
    {
        foreach (TreeViewItem node in nodes)
        {
            yield return node;
            foreach (TreeViewItem child in Descendants(node.Items.OfType<TreeViewItem>())) { yield return child; }
        }
    }
}
